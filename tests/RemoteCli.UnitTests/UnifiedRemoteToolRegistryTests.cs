using System.Text;
using System.Text.Json;
using Common.Protocol;
using RemoteCli.Remote;

namespace RemoteCli.UnitTests;

public sealed class UnifiedRemoteToolRegistryTests
{
    [Fact]
    public async Task AutomaticApprovalSkipsPromptsForPermissionGatedTools()
    {
        var approvals = 0;
        var registry = new RemoteToolRegistry(
            [new ApprovalTestTool()],
            (_, _) =>
            {
                Interlocked.Increment(ref approvals);
                return Task.FromResult(ToolApprovalDecision.Deny);
            },
            autoApproveTools: true);

        var result = await registry.ExecuteAsync(
            "test.approval",
            JsonSerializer.SerializeToElement(new { }),
            CancellationToken.None);

        Assert.True(result.GetProperty("completed").GetBoolean());
        Assert.Equal(0, approvals);
    }

    [Fact]
    public async Task ToolQueueHasABoundAndCancellationClearsPendingApprovals()
    {
        var firstApprovalStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blockApprovals = 1;
        var registry = new RemoteToolRegistry(
            [new ApprovalTestTool()],
            async (_, cancellationToken) =>
            {
                if (Volatile.Read(ref blockApprovals) == 1)
                {
                    firstApprovalStarted.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }

                return ToolApprovalDecision.AllowOnce;
            });

        var cancellations = Enumerable.Range(0, 16).Select(_ => new CancellationTokenSource()).ToArray();
        try
        {
            Task<JsonElement> Start(CancellationToken cancellationToken) => registry.ExecuteAsync(
                "test.approval", JsonSerializer.SerializeToElement(new { }), cancellationToken);

            var pending = cancellations.Select(source => Start(source.Token)).ToArray();
            await firstApprovalStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var full = await Assert.ThrowsAsync<RemoteToolException>(() => Start(CancellationToken.None));
            Assert.Equal("remote_tool_queue_full", full.Code);

            foreach (var source in cancellations) source.Cancel();
            foreach (var call in pending)
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
            }

            Volatile.Write(ref blockApprovals, 0);
            var result = await Start(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(result.GetProperty("completed").GetBoolean());
        }
        finally
        {
            foreach (var source in cancellations) source.Dispose();
        }
    }

    [Fact]
    public async Task WorkspaceToolsRequireApprovalAndPreserveConcurrentChanges()
    {
        var root = Path.Combine(Path.GetTempPath(), "longshell-tool-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var approvals = 0;
            var registry = RemoteToolRegistry.CreateForDesktop(
                root,
                [],
                (_, _) =>
                {
                    approvals++;
                    return Task.FromResult(ToolApprovalDecision.AllowForSession);
                });

            var create = JsonSerializer.SerializeToElement(new { path = "notes/test.txt", content = "before" }, ProtocolJson.Options);
            var created = await registry.ExecuteAsync("remote.file_create", create, CancellationToken.None);
            Assert.True(created.GetProperty("created").GetBoolean());

            var read = await registry.ExecuteAsync(
                "remote.file_read",
                JsonSerializer.SerializeToElement(new { path = "notes/test.txt" }, ProtocolJson.Options),
                CancellationToken.None);
            Assert.Equal("before", read.GetProperty("content").GetString());

            var edit = JsonSerializer.SerializeToElement(new
            {
                path = "notes/test.txt",
                oldText = "before",
                newText = "after",
                expectedSha256 = read.GetProperty("sha256").GetString()
            }, ProtocolJson.Options);
            var edited = await registry.ExecuteAsync("remote.file_edit", edit, CancellationToken.None);
            Assert.True(edited.GetProperty("edited").GetBoolean());
            Assert.Equal("after", await File.ReadAllTextAsync(Path.Combine(root, "notes", "test.txt"), Encoding.UTF8));
            Assert.Equal(2, approvals);

            await File.WriteAllTextAsync(Path.Combine(root, "notes", "test.txt"), "changed", new UTF8Encoding(false));
            var failure = await Assert.ThrowsAsync<RemoteToolException>(
                () => registry.ExecuteAsync("remote.file_edit", edit, CancellationToken.None));
            Assert.Equal("file_changed", failure.Code);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task WorkspaceToolRejectsPathEscape()
    {
        var root = Path.Combine(Path.GetTempPath(), "longshell-tool-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var registry = RemoteToolRegistry.CreateForDesktop(
                root,
                [],
                (_, _) => Task.FromResult(ToolApprovalDecision.AllowOnce));
            var arguments = JsonSerializer.SerializeToElement(new { path = "../outside.txt", content = "x" }, ProtocolJson.Options);
            var failure = await Assert.ThrowsAsync<RemoteToolException>(
                () => registry.ExecuteAsync("remote.file_create", arguments, CancellationToken.None));
            Assert.Equal("path_outside_workspace", failure.Code);
            Assert.False(File.Exists(Path.Combine(root, "..", "outside.txt")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class ApprovalTestTool : IRemoteTool
    {
        private static JsonElement Schema
        {
            get
            {
                using var document = JsonDocument.Parse("""{"type":"object","properties":{},"required":[],"additionalProperties":false}""");
                return document.RootElement.Clone();
            }
        }

        public RemoteToolDescriptor Descriptor { get; } = new(
            "test.approval", "1", "Approval queue test tool.", Schema, null);
        public string? ApprovalKey => "test.approval";
        public string PermissionDescription => "Run a test operation.";

        public Task<JsonElement> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(JsonSerializer.SerializeToElement(new { completed = true }, ProtocolJson.Options));
        }
    }
}
