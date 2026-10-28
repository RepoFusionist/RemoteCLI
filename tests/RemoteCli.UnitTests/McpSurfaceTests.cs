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
        var transport = CreateTransport("Remote CLI offline MCP integration test", desktopProject, storageEnabled: false);

        await using var client = await McpClient.CreateAsync(transport);
        var tools = await client.ListToolsAsync();
        var toolNames = tools.Select(tool => tool.Name).Order(StringComparer.Ordinal).ToArray();

        var prompts = await client.ListPromptsAsync();
        Assert.Contains(prompts, prompt => prompt.Name == "remotecli-remote-policy");

        var statusResult = await client.CallToolAsync(
            "remotecli_connection_status",
            new Dictionary<string, object?>());
        Assert.False(statusResult.IsError);

        var statusText = string.Join(
            Environment.NewLine,
            statusResult.Content.OfType<TextContentBlock>().Select(content => content.Text));
        using var statusDocument = System.Text.Json.JsonDocument.Parse(statusText);
        var connectionState = statusDocument.RootElement.GetProperty("connectionState").GetString();
        Assert.False(string.IsNullOrWhiteSpace(connectionState));
        var connected = statusDocument.RootElement.GetProperty("connected").GetBoolean();
        _testOutput.WriteLine("Remote CLI MCP local connection state: " + connectionState);

        if (connected)
        {
            // A connected desktop may belong to the user's active remote session. Verify discovery only;
            // never issue a command or change connection state from this offline surface test.
            Assert.Contains("remotecli_connection_status", toolNames);
            Assert.Contains("remotecli_connection_close", toolNames);
            var permissions = statusDocument.RootElement.GetProperty("grantedPermissions").GetString() ?? string.Empty;
            if (permissions.Contains("CommandExecute", StringComparison.Ordinal))
            {
                Assert.Contains("remotecli_remote_command_start", toolNames);
            }
        }
        else
        {
            Assert.Equal(
                [
                    "conversation_status",
                    "remotecli_connection_auth",
                    "remotecli_connection_close",
                    "remotecli_connection_connect",
                    "remotecli_connection_discover",
                    "remotecli_connection_reconnect",
                    "remotecli_connection_status",
                    "remotecli_connection_wait"
                ],
                toolNames);
            Assert.DoesNotContain(toolNames, name => name.StartsWith("remotecli_remote_", StringComparison.Ordinal));
            var nextAction = statusDocument.RootElement.GetProperty("nextAction").GetString();
            if (string.Equals(connectionState, "ServiceUnavailable", StringComparison.Ordinal))
            {
                Assert.Equal("start_desktop", nextAction);

                var waitResult = await client.CallToolAsync(
                    "remotecli_connection_wait",
                    new Dictionary<string, object?>
                    {
                        ["afterStateVersion"] = statusDocument.RootElement.GetProperty("stateVersion").GetInt64(),
                        ["waitMilliseconds"] = 0
                    });
                Assert.False(waitResult.IsError);

                // The user's desktop may start or connect between IPC requests. Keep this
                // live integration test read-only; offline error paths have unit coverage.
            }
            else
            {
                // A desktop service is present without an authenticated session. A user may have
                // explicitly closed it, so both Disconnected and Closed are valid idle states.
                Assert.Contains(connectionState, new[] { "Disconnected", "Closed", "Connecting", "AwaitingAuthentication", "Authenticating", "Faulted", "Expired" });
                if (connectionState is "Disconnected" or "Closed")
                    Assert.Equal("connect", nextAction);
            }
        }

        var enabledTransport = CreateTransport("Remote CLI storage opt-in integration test", desktopProject, storageEnabled: true);
        await using var enabledClient = await McpClient.CreateAsync(enabledTransport);
        var enabledToolNames = (await enabledClient.ListToolsAsync()).Select(tool => tool.Name).ToArray();
        Assert.Contains("conversation_sync", enabledToolNames);
        Assert.Contains("remotecli_connection_status", enabledToolNames);
    }

    private static StdioClientTransport CreateTransport(string name, string desktopProject, bool storageEnabled)
    {
        var packagedExecutable = Environment.GetEnvironmentVariable("REMOTECLI_TEST_MCP_EXE");
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
