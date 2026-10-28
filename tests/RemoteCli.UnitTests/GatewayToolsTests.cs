using System.Net;
using System.Text;
using System.Text.Json;
using Common.Protocol;
using RemoteCli.Desktop;

namespace RemoteCli.UnitTests;

public sealed class GatewayToolsTests
{
    private static JsonElement Args(object value) => JsonSerializer.SerializeToElement(value);

    [Fact]
    public async Task PatchRequiresApprovalAndAppliesValidatedMultiFileChanges()
    {
        var root = Path.Combine(Path.GetTempPath(), "longshell-patch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "old.txt"), "first\nsecond\n");
            await File.WriteAllTextAsync(Path.Combine(root, "delete.txt"), "obsolete\n");
            var patch = "*** Begin Patch\n*** Update File: old.txt\n@@\n first\n-second\n+changed\n*** Add File: added.txt\n+new\n*** Delete File: delete.txt\n*** End Patch\n";
            var denied = new GatewayPatch(root, (_, _) => Task.FromResult(false)).Create();
            Assert.StartsWith("错误：", await denied.Execute(Args(new { patch }), CancellationToken.None));
            Assert.Equal("first\nsecond\n", await File.ReadAllTextAsync(Path.Combine(root, "old.txt")));
            Assert.False(File.Exists(Path.Combine(root, "added.txt")));
            var allowed = new GatewayPatch(root, (_, _) => Task.FromResult(true)).Create();
            Assert.StartsWith("已应用", await allowed.Execute(Args(new { patch }), CancellationToken.None));
            Assert.Equal("first\nchanged\n", await File.ReadAllTextAsync(Path.Combine(root, "old.txt")));
            Assert.Equal("new\n", await File.ReadAllTextAsync(Path.Combine(root, "added.txt")));
            Assert.False(File.Exists(Path.Combine(root, "delete.txt")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task PatchRejectsChangedFileAfterApprovalAndOutsidePaths()
    {
        var root = Path.Combine(Path.GetTempPath(), "longshell-patch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "a.txt");
            await File.WriteAllTextAsync(path, "old\n");
            var tool = new GatewayPatch(root, async (_, _) => { await File.WriteAllTextAsync(path, "other\n"); return true; }).Create();
            var patch = "*** Begin Patch\n*** Update File: a.txt\n@@\n-old\n+new\n*** End Patch\n";
            Assert.StartsWith("错误：", await tool.Execute(Args(new { patch }), CancellationToken.None));
            Assert.Equal("other\n", await File.ReadAllTextAsync(path));
            var escape = "*** Begin Patch\n*** Add File: ../outside.txt\n+x\n*** End Patch\n";
            Assert.StartsWith("错误：", await tool.Execute(Args(new { patch = escape }), CancellationToken.None));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task GlobAndGrepReuseRemoteFileImplementations()
    {
        var root = Path.Combine(Path.GetTempPath(), "longshell-search-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "example.cs"), "line\nfoo42\n");
            var tools = LocalChatTools.Create(root, ["glob", "grep"]);
            var glob = await tools.Single(t => t.Name == "glob").Execute(Args(new { pattern = "*.cs" }), CancellationToken.None);
            var grep = await tools.Single(t => t.Name == "grep").Execute(Args(new { pattern = "foo[0-9]+" }), CancellationToken.None);
            Assert.Contains("example.cs", glob);
            Assert.Contains("foo42", grep);
            Assert.Contains("\"line\":2", grep);
            Assert.StartsWith("错误：", await tools.Single(t => t.Name == "grep").Execute(Args(new { pattern = "[" }), CancellationToken.None));
            var read = LocalChatTools.Create(root, [LocalChatTools.ReadFile]).Single();
            var lines = await read.Execute(Args(new { path = "example.cs", start_line = 2, max_lines = 1 }), CancellationToken.None);
            Assert.Contains("2: foo42", lines);
            Assert.DoesNotContain("1: line", lines);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task RemoteToolsPreserveSessionAndCoordinatorPermissionBoundary()
    {
        var id = Guid.NewGuid();
        var status = new LocalConnectionStatus(1, "Running", LocalConnectionState.Connected, true, id, "relay", RemotePermission.SessionRead,
            null, 7, null, null, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(2), "peer");
        LocalServiceRequest? seen = null;
        var remote = new GatewayRemoteTools(() => status, (request, _) =>
        {
            seen = request;
            return Task.FromResult(new LocalServiceResponse(1, request.RequestId, request.Type, false, request.ExpectedStateVersion ?? 0,
                Args(new { }), new LocalConnectionError("remote", "permission_denied", "Denied", false, 0)));
        });
        var call = remote.Create(["remote_tools"]).Single(t => t.Name == "remote_tool_call");
        var result = await call.Execute(Args(new { name = "remote.file_read", arguments = new { path = "a" } }), CancellationToken.None);
        Assert.Contains("permission_denied", result);
        Assert.Equal(id, seen!.ExpectedSessionId);
        Assert.Equal(7, seen.ExpectedStateVersion);
        var command = remote.Create(["remote_commands"]).Single(t => t.Name == "remote_command_start");
        seen = null;
        Assert.StartsWith("错误：", await command.Execute(Args(new { commandText = "x", timeoutSeconds = -1 }), CancellationToken.None));
        Assert.Null(seen);
        status = status with { SessionId = Guid.NewGuid() };
        seen = null;
        Assert.StartsWith("错误：", await call.Execute(Args(new { name = "remote.file_read", arguments = new { path = "a" } }), CancellationToken.None));
        Assert.Null(seen);
    }

    [Fact]
    public async Task ExaRequiresApprovalAndMapsResultsWithoutExposingKey()
    {
        var handler = new TestHandler();
        var denied = new GatewayExaSearch(() => "test-key", (_, _) => Task.FromResult(false), () => new HttpClient(handler, false)).Create();
        Assert.StartsWith("错误：", await denied.Execute(Args(new { query = "example" }), CancellationToken.None));
        Assert.Equal(0, handler.Calls);
        var allowed = new GatewayExaSearch(() => "test-key", (_, _) => Task.FromResult(true), () => new HttpClient(handler, false)).Create();
        var result = await allowed.Execute(Args(new { query = "example" }), CancellationToken.None);
        Assert.Equal(1, handler.Calls);
        Assert.Contains("https://example.com/", result);
        Assert.Contains("snippet", result);
        Assert.DoesNotContain("test-key", result);
    }

    [Fact]
    public async Task WebSearchUsesConfiguredExaProvider()
    {
        var handler = new TestHandler();
        var options = new GatewayWebSearchOptions(
            "exa",
            "test-key",
            "https://api.exa.ai",
            5,
            (_, _) => Task.FromResult(true),
            () => new HttpClient(handler, false));
        var tool = LocalChatTools.Create(string.Empty, [LocalChatTools.WebSearch], options).Single();

        var result = await tool.Execute(Args(new { query = "example" }), CancellationToken.None);

        Assert.Contains("https://example.com/", result);
        Assert.Contains("snippet", result);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task WebSearchUsesDeepSeekSearchApiAndMapsSummary()
    {
        var handler = new DeepSeekTestHandler();
        var options = new GatewayWebSearchOptions(
            "deepseek",
            "deepseek-test-key",
            "https://api.deepseek.com/v1/tools",
            5,
            (_, _) => Task.FromResult(true),
            () => new HttpClient(handler, false));
        var tool = LocalChatTools.Create(string.Empty, [LocalChatTools.WebSearch], options).Single();

        var result = await tool.Execute(Args(new { query = "example" }), CancellationToken.None);

        Assert.Contains("https://example.org/", result);
        Assert.Contains("DeepSeek summary", result);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task RemoteOperationFailureIsNotReportedAsToolSuccess()
    {
        var sessionId = Guid.NewGuid();
        var status = new LocalConnectionStatus(1, "Running", LocalConnectionState.Connected, true, sessionId, "relay", RemotePermission.CommandExecute,
            null, 7, null, null, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(2), "peer");
        var remote = new GatewayRemoteTools(() => status, (request, _) => Task.FromResult(new LocalServiceResponse(1, request.RequestId, request.Type, true, 7,
            Args(new { done = true, error = new { code = "remote_tool_failed", message = "failed" } }), null)));
        var receive = remote.Create(["remote_tools"]).Single(t => t.Name == "remote_tool_receive");
        var result = await receive.Execute(Args(new { operationId = Guid.NewGuid(), cursor = 0, waitMilliseconds = 0 }), CancellationToken.None);
        Assert.StartsWith("错误：", result);
        Assert.Contains("remote_tool_failed", result);
    }

    [Fact]
    public async Task LocalCommandRequiresApprovalAndReportsExitCode()
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var denied = new GatewayCommandSession(Path.GetTempPath(), (_, _) => Task.FromResult(false));
        Assert.StartsWith("错误：", await denied.Create()[0].Execute(Args(new { cmd = "Write-Output hello", yield_time_ms = 3000 }), CancellationToken.None));
        await using var allowed = new GatewayCommandSession(Path.GetTempPath(), (_, _) => Task.FromResult(true));
        var result = await allowed.Create()[0].Execute(Args(new { cmd = "Write-Output hello", yield_time_ms = 10000 }), CancellationToken.None);
        Assert.Contains("hello", result);
        Assert.Contains("[exit code: 0]", result);
    }

    [Fact]
    public async Task LocalCommandCanReceiveInputInTheSameRequest()
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var session = new GatewayCommandSession(Path.GetTempPath(), (_, _) => Task.FromResult(true));
        var tools = session.Create();
        var initial = await tools[0].Execute(Args(new { cmd = "[Console]::WriteLine([Console]::ReadLine())", yield_time_ms = 100 }), CancellationToken.None);
        var match = System.Text.RegularExpressions.Regex.Match(initial, "session_id: ([0-9a-f]+)");
        Assert.True(match.Success, initial);
        var result = await tools[1].Execute(Args(new { session_id = match.Groups[1].Value, chars = "interactive-test\n", yield_time_ms = 10000 }), CancellationToken.None);
        Assert.Contains("interactive-test", result);
        Assert.Contains("[exit code: 0]", result);
    }

    [Fact]
    public async Task LocalCommandPreservesUnicodeOutput()
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var session = new GatewayCommandSession(Path.GetTempPath(), (_, _) => Task.FromResult(true));
        var result = await session.Create()[0].Execute(Args(new { cmd = "Write-Output '中文'", yield_time_ms = 10000 }), CancellationToken.None);
        Assert.Contains("中文", result);
    }

    private sealed class TestHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++;
            Assert.Equal("https://api.exa.ai/search", request.RequestUri!.ToString());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"results\":[{\"url\":\"https://example.com/\",\"title\":\"Example\",\"highlights\":[\"snippet\"]}]}", Encoding.UTF8, "application/json") });
        }
    }

    private sealed class DeepSeekTestHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++;
            Assert.Equal("https://api.deepseek.com/v1/tools/search", request.RequestUri!.ToString());
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("deepseek-test-key", request.Headers.Authorization.Parameter);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"results\":[{\"url\":\"https://example.org/\",\"title\":\"DeepSeek Example\",\"summary\":\"DeepSeek summary\"}]}", Encoding.UTF8, "application/json")
            });
        }
    }
}
