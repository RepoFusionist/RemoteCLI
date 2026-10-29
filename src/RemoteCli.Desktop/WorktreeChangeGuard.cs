namespace RemoteCli.Desktop;

// One rollback baseline per model request, created only after approval of the first
// operation that may change local files. Read-only conversation needs no disk scan.
internal sealed class WorktreeChangeGuard(Func<CancellationToken, Task<string>> capture)
{
    private readonly object gate = new();
    private Task<string>? baseline;

    internal static bool NeedsCommit(GatewayApprovalCategory category, string content) => category switch
    {
        GatewayApprovalCategory.WorkspaceWrite or GatewayApprovalCategory.CommandInput or GatewayApprovalCategory.Mcp => true,
        GatewayApprovalCategory.Command => GatewayApprovalPolicy.IsDestructiveCommand(content),
        _ => false
    };

    internal async Task<bool> ApproveAsync(GatewayApprovalCategory category, string content,
        Func<Task<bool>> approve, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!await approve().ConfigureAwait(false)) return false;
        token.ThrowIfCancellationRequested();
        if (!NeedsCommit(category, content)) return true;
        Task<string> pending;
        lock (gate) pending = baseline ??= CommitBaselineAsync(token);
        await pending.WaitAsync(token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        return true;
    }

    private async Task<string> CommitBaselineAsync(CancellationToken token)
    {
        var id = await capture(token).ConfigureAwait(false);
        if (!WorkspaceWorktrees.IsCommit(id)) throw new IOException("Git 工作树回退点无效，未执行本机修改。");
        return id;
    }

    internal bool Prepared => baseline is { IsCompletedSuccessfully: true };
}
