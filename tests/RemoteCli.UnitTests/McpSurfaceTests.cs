using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using RemoteCli.Local;
using Xunit.Abstractions;

namespace RemoteCli.UnitTests;

public sealed class McpSurfaceTests
{
    private readonly ITestOutputHelper _testOutput;

    public McpSurfaceTests(ITestOutputHelper testOutput)
    {
        _testOutput = testOutput;
    }

    [Fact]
    public async Task StdioServerExposesOnlySafeCurrentToolsAndPolicyPrompt()
    {
        var desktopProject = GetDesktopProjectPath(typeof(ConversationStore).Assembly.Location);
        var transport = CreateTransport("LongShell offline MCP integration test", desktopProject, storageEnabled: false);

        await using var client = await McpClient.CreateAsync(transport);
        var tools = await client.ListToolsAsync();
        var toolNames = tools.Select(tool => tool.Name).Order(StringComparer.Ordinal).ToArray();

        var prompts = await client.ListPromptsAsync();
        Assert.Contains(prompts, prompt => prompt.Name == "longshell-remote-policy");

        var statusResult = await client.CallToolAsync(
            "longshell_connection_status",
            new Dictionary<string, object?>());
        Assert.False(statusResult.IsError);

        var statusText = string.Join(
            Environment.NewLine,
            statusResult.Content.OfType<TextContentBlock>().Select(content => content.Text));
        using var statusDocument = System.Text.Json.JsonDocument.Parse(statusText);
        var connectionState = statusDocument.RootElement.GetProperty("connectionState").GetString();
        Assert.False(string.IsNullOrWhiteSpace(connectionState));
        var connected = statusDocument.RootElement.GetProperty("connected").GetBoolean();
        _testOutput.WriteLine("LongShell MCP local connection state: " + connectionState);

        if (connected)
        {
            // A connected desktop may belong to the user's active remote session. Verify discovery only;
            // never issue a command or change connection state from this offline surface test.
            Assert.Contains("longshell_connection_status", toolNames);
            Assert.Contains("longshell_connection_close", toolNames);
            var permissions = statusDocument.RootElement.GetProperty("grantedPermissions").GetString() ?? string.Empty;
            if (permissions.Contains("CommandExecute", StringComparison.Ordinal))
            {
                Assert.Contains("longshell_remote_command_start", toolNames);
            }
        }
        else
        {
            Assert.Equal(
                [
                    "conversation_status",
                    "longshell_connection_auth",
                    "longshell_connection_close",
                    "longshell_connection_connect",
                    "longshell_connection_discover",
                    "longshell_connection_reconnect",
                    "longshell_connection_status",
                    "longshell_connection_wait"
                ],
                toolNames);
            Assert.DoesNotContain(toolNames, name => name.StartsWith("longshell_remote_", StringComparison.Ordinal));
            Assert.Equal("start_desktop", statusDocument.RootElement.GetProperty("nextAction").GetString());

            var waitResult = await client.CallToolAsync(
                "longshell_connection_wait",
                new Dictionary<string, object?>
                {
                    ["afterStateVersion"] = statusDocument.RootElement.GetProperty("stateVersion").GetInt64(),
                    ["waitMilliseconds"] = 0
                });
            Assert.False(waitResult.IsError);

            foreach (var toolName in new[] { "longshell_connection_connect", "longshell_connection_reconnect" })
            {
                var connectResult = await client.CallToolAsync(toolName, new Dictionary<string, object?>());
                Assert.True(connectResult.IsError);
                var connectText = string.Join(
                    Environment.NewLine,
                    connectResult.Content.OfType<TextContentBlock>().Select(content => content.Text));
                using var connectDocument = System.Text.Json.JsonDocument.Parse(connectText);
                Assert.Equal("ServiceUnavailable", connectDocument.RootElement.GetProperty("connectionState").GetString());
            }

            var staleRemoteCall = await client.CallToolAsync(
                "longshell_remote_command_start",
                new Dictionary<string, object?>
                {
                    ["commandText"] = "Get-Date",
                    ["timeoutSeconds"] = 30
                });
            Assert.True(staleRemoteCall.IsError);
            var staleRemoteText = string.Join(
                Environment.NewLine,
                staleRemoteCall.Content.OfType<TextContentBlock>().Select(content => content.Text));
            using var staleRemoteDocument = System.Text.Json.JsonDocument.Parse(staleRemoteText);
            Assert.Equal("ServiceUnavailable", staleRemoteDocument.RootElement.GetProperty("connectionState").GetString());
            Assert.Equal("NotStarted", staleRemoteDocument.RootElement.GetProperty("operationOutcome").GetString());

            var lostResult = await client.CallToolAsync(
                "longshell_remote_command_receive",
                new Dictionary<string, object?>
                {
                    ["operationId"] = Guid.NewGuid().ToString("D"),
                    ["cursor"] = 0,
                    ["waitMilliseconds"] = 0
                });
            Assert.True(lostResult.IsError);
            var lostResultText = string.Join(
                Environment.NewLine,
                lostResult.Content.OfType<TextContentBlock>().Select(content => content.Text));
            using var lostResultDocument = System.Text.Json.JsonDocument.Parse(lostResultText);
            Assert.Equal("Unknown", lostResultDocument.RootElement.GetProperty("operationOutcome").GetString());
        }

        var enabledTransport = CreateTransport("LongShell storage opt-in integration test", desktopProject, storageEnabled: true);
        await using var enabledClient = await McpClient.CreateAsync(enabledTransport);
        var enabledToolNames = (await enabledClient.ListToolsAsync()).Select(tool => tool.Name).ToArray();
        Assert.Contains("conversation_sync", enabledToolNames);
        Assert.Contains("longshell_connection_status", enabledToolNames);
    }

    private static StdioClientTransport CreateTransport(string name, string desktopProject, bool storageEnabled)
    {
        var packagedExecutable = Environment.GetEnvironmentVariable("LONGSHELL_TEST_MCP_EXE");
        if (!string.IsNullOrWhiteSpace(packagedExecutable))
        {
            return new StdioClientTransport(new StdioClientTransportOptions
            {
                Name = name,
                Command = packagedExecutable,
                Arguments = storageEnabled
                    ? ["mcp", "--enable-conversation-storage"]
                    : ["mcp"]
            });
        }

        var arguments = new List<string>
        {
            "run",
            "--project",
            desktopProject,
            "--configuration",
            "Release",
            "--no-build",
            "--",
            "mcp"
        };
        if (storageEnabled)
        {
            arguments.Add("--enable-conversation-storage");
        }

        return new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = name,
            Command = "dotnet",
            Arguments = arguments
        });
    }

    private static string GetDesktopProjectPath(string localAssembly)
    {
        var outputDirectory = Path.GetDirectoryName(localAssembly)!;
        return Path.GetFullPath(Path.Combine(outputDirectory, "..", "..", "..", "..", "..", "src", "RemoteCli.Desktop", "RemoteCli.Desktop.csproj"));
    }
}
