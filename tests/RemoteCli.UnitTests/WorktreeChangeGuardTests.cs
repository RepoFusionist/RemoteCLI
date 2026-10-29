using RemoteCli.Desktop;

namespace RemoteCli.UnitTests;

public sealed class WorktreeChangeGuardTests
{
    [Fact]
    public async Task ReadsAndDeniedWritesDoNotCreateCommits()
    {
        var calls = 0;
        var guard = new WorktreeChangeGuard(_ => { calls++; return Task.FromResult(new string('a', 40)); });
        Assert.True(await guard.ApproveAsync(GatewayApprovalCategory.Network, "query", () => Task.FromResult(true), CancellationToken.None));
        Assert.True(await guard.ApproveAsync(GatewayApprovalCategory.Command, "git status", () => Task.FromResult(true), CancellationToken.None));
        Assert.False(await guard.ApproveAsync(GatewayApprovalCategory.WorkspaceWrite, "write", () => Task.FromResult(false), CancellationToken.None));
        Assert.Equal(0, calls); Assert.False(guard.Prepared);
    }

    [Fact]
    public async Task ConcurrentChangesShareOneNativeBaselineCommit()
    {
        var calls = 0; var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var guard = new WorktreeChangeGuard(_ => { Interlocked.Increment(ref calls); return completion.Task; });
        var actions = Enumerable.Range(0, 6).Select(_ => guard.ApproveAsync(GatewayApprovalCategory.WorkspaceWrite, "write",
            () => Task.FromResult(true), CancellationToken.None)).ToArray();
        Assert.Equal(1, calls); completion.SetResult(new string('a', 40));
        Assert.All(await Task.WhenAll(actions), Assert.True); Assert.True(guard.Prepared);
    }

    [Fact]
    public async Task InvalidCommitOrCancelledApprovalPreventsChange()
    {
        var guard = new WorktreeChangeGuard(_ => Task.FromResult(new string('a', 32)));
        await Assert.ThrowsAsync<IOException>(() => guard.ApproveAsync(GatewayApprovalCategory.WorkspaceWrite, "write", () => Task.FromResult(true), CancellationToken.None));
        Assert.False(guard.Prepared);
        using var cts = new CancellationTokenSource();
        var cancelled = new WorktreeChangeGuard(_ => throw new Xunit.Sdk.XunitException("must not commit"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.ApproveAsync(GatewayApprovalCategory.WorkspaceWrite, "write",
            () => { cts.Cancel(); return Task.FromResult(true); }, cts.Token));
    }
}
