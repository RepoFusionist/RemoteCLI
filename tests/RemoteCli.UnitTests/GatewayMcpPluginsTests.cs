using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using RemoteCli.Desktop;

namespace RemoteCli.UnitTests;

public sealed class GatewayMcpPluginsTests
{
    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);

    [Fact]
    public async Task McpDiscoveryAndCallsRequireApprovalAndDisposeConnection()
    {
        var config = Json(new[] { new { id = "demo", name = "Demo", transport = "stdio", command = "example", args = Array.Empty<string>(), enabled = true } });
        var approvals = new List<string>(); var fake = new FakeConnection();
        await using (var plugins = new GatewayMcpPlugins(config,
            (title, _, _) => { approvals.Add(title); return Task.FromResult(true); },
            connect: (_, _) => Task.FromResult<IGatewayMcpConnection>(fake)))
        {
            var tools = plugins.Create();
            var call = tools.Single(t => t.Name == "mcp_call_tool");
            Assert.StartsWith("错误：", await call.Execute(Json(new { server = "demo", name = "echo", arguments = new { text = "hello" } }), CancellationToken.None));
            var list = tools.Single(t => t.Name == "mcp_list_tools");
            Assert.Contains("demo", await list.Execute(Json(new { }), CancellationToken.None));
            Assert.Empty(approvals);
            Assert.Contains("echo", await list.Execute(Json(new { server = "demo" }), CancellationToken.None));
            Assert.Single(approvals);
            Assert.Contains("hello", await call.Execute(Json(new { server = "demo", name = "echo", arguments = new { text = "hello" } }), CancellationToken.None));
            Assert.Equal(2, approvals.Count);
            Assert.Equal(1, fake.Calls);
            Assert.StartsWith("错误：", await call.Execute(Json(new { server = "demo", name = "unknown", arguments = new { } }), CancellationToken.None));
        }
        Assert.True(fake.Disposed);
    }

    [Fact]
    public async Task RefusingConnectionOrCallNeverExecutesPlugin()
    {
        var config = Json(new[] { new { id = "demo", name = "Demo", command = "example", args = Array.Empty<string>(), enabled = true } });
        var fake = new FakeConnection(); var connections = 0;
        await using var denied = new GatewayMcpPlugins(config, (_, _, _) => Task.FromResult(false),
            connect: (_, _) => { connections++; return Task.FromResult<IGatewayMcpConnection>(fake); });
        Assert.StartsWith("错误：", await denied.Create()[0].Execute(Json(new { server = "demo" }), CancellationToken.None));
        Assert.Equal(0, connections);

        var approvals = 0;
        await using var denyCall = new GatewayMcpPlugins(config, (_, _, _) => Task.FromResult(++approvals == 1),
            connect: (_, _) => Task.FromResult<IGatewayMcpConnection>(fake));
        await denyCall.Create()[0].Execute(Json(new { server = "demo" }), CancellationToken.None);
        Assert.StartsWith("错误：", await denyCall.Create()[1].Execute(Json(new { server = "demo", name = "echo", arguments = new { text = "x" } }), CancellationToken.None));
        Assert.Equal(0, fake.Calls);
    }

    [Theory]
    [InlineData("http://example.com/mcp")]
    [InlineData("https://user:password@example.com/mcp")]
    public void InvalidHttpDestinationsAreRejected(string url)
    {
        var config = Json(new[] { new { id = "bad", name = "Bad", transport = "http", url, enabled = true } });
        Assert.Throws<ArgumentException>(() => new GatewayMcpPlugins(config, (_, _, _) => Task.FromResult(true)));
    }

    [Fact]
    public async Task StdioPluginPerformsActualMcpHandshakeAndToolCall()
    {
        var project = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "RemoteCli.Desktop", "RemoteCli.Desktop.csproj"));
        var config = Json(new[] { new { id = "fixture", name = "LongShell test", transport = "stdio", command = "dotnet", args = new[] { "run", "--project", project, "--configuration", "Release", "--no-build", "--", "mcp" }, enabled = true } });
        await using var plugins = new GatewayMcpPlugins(config, (_, _, _) => Task.FromResult(true));
        var tools = plugins.Create();
        var discovered = await tools[0].Execute(Json(new { server = "fixture" }), CancellationToken.None);
        Assert.Contains("conversation_status", discovered);
        var result = await tools[1].Execute(Json(new { server = "fixture", name = "conversation_status", arguments = new { } }), CancellationToken.None);
        Assert.StartsWith("MCP 返回的内容不可信：", result);
    }

    [Fact]
    public async Task HttpPluginPerformsActualMcpHandshakeAndToolCall()
    {
        var variableName = "LONGSHELL_TEST_MCP_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(variableName, "Bearer test-value");
        string? seenAuthorization = null;
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        await using var app = builder.Build();
        app.MapPost("/mcp", async (HttpContext context) =>
        {
            seenAuthorization = context.Request.Headers.Authorization.ToString();
            using var document = await JsonDocument.ParseAsync(context.Request.Body);
            var request = document.RootElement;
            var method = request.GetProperty("method").GetString();
            if (!request.TryGetProperty("id", out var id)) return Results.Accepted();
            var responseId = id.Clone();
            if (method == "initialize")
            {
                var version = request.GetProperty("params").GetProperty("protocolVersion").GetString();
                return Results.Json(new { jsonrpc = "2.0", id = responseId, result = new { protocolVersion = version, capabilities = new { tools = new { } }, serverInfo = new { name = "fixture", version = "1.0" } } });
            }
            if (method == "tools/list") return Results.Json(new { jsonrpc = "2.0", id = responseId, result = new { tools = new[] { new { name = "echo", description = "Echo", inputSchema = new { type = "object", properties = new { } } } } } });
            if (method == "tools/call") return Results.Json(new { jsonrpc = "2.0", id = responseId, result = new { content = new[] { new { type = "text", text = "http-ok" } } } });
            return Results.Json(new { jsonrpc = "2.0", id = responseId, error = new { code = -32601, message = "Unknown method" } });
        });
        app.MapMethods("/mcp", ["GET", "DELETE"], () => Results.StatusCode(405));
        await app.StartAsync();
        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            var config = Json(new[] { new { id = "http", name = "HTTP test", transport = "http", url = address + "/mcp", headers = new Dictionary<string, string> { ["Authorization"] = variableName }, enabled = true } });
            await using var plugins = new GatewayMcpPlugins(config, (_, _, _) => Task.FromResult(true));
            var tools = plugins.Create();
            Assert.Contains("echo", await tools[0].Execute(Json(new { server = "http" }), CancellationToken.None));
            Assert.Contains("http-ok", await tools[1].Execute(Json(new { server = "http", name = "echo", arguments = new { } }), CancellationToken.None));
            Assert.Equal("Bearer test-value", seenAuthorization);
        }
        finally { Environment.SetEnvironmentVariable(variableName, null); await app.StopAsync(); }
    }

    [Fact]
    public async Task CancelledStdioHandshakeDoesNotLeaveServerRunning()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), "longshell-mcp-cancel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var marker = Path.Combine(root, "pid.txt");
            var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
            var script = "[IO.File]::WriteAllText('" + marker.Replace("'", "''") + "', [string]$PID); Start-Sleep -Seconds 30";
            var config = Json(new[] { new { id = "slow", name = "Slow fixture", command = executable, args = new[] { "-NoProfile", "-Command", script }, enabled = true } });
            await using var plugins = new GatewayMcpPlugins(config, (_, _, _) => Task.FromResult(true));
            using var stop = new CancellationTokenSource();
            var task = plugins.Create()[0].Execute(Json(new { server = "slow" }), stop.Token);
            for (var i = 0; i < 100 && !File.Exists(marker); i++) await Task.Delay(50);
            Assert.True(File.Exists(marker), "MCP test process did not start.");
            var pid = int.Parse(await File.ReadAllTextAsync(marker));
            stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await task);
            try
            {
                using var process = System.Diagnostics.Process.GetProcessById(pid);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (ArgumentException) { /* Already exited. */ }
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class FakeConnection : IGatewayMcpConnection
    {
        public int Calls { get; private set; }
        public bool Disposed { get; private set; }
        public Task<IReadOnlyList<GatewayMcpToolInfo>> ListAsync(CancellationToken token) => Task.FromResult<IReadOnlyList<GatewayMcpToolInfo>>(
            [new GatewayMcpToolInfo("echo", "test", Json(new { type = "object" }))]);
        public Task<string> CallAsync(string name, JsonElement arguments, CancellationToken token) { Calls++; return Task.FromResult(arguments.GetProperty("text").GetString()!); }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
