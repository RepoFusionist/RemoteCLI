using System.Text.Json;

namespace RemoteCli.Desktop;

internal sealed partial class MainForm
{
    private static bool IsTrustedWorktreeSource(string root)
    {
        if (!File.Exists(UiStatePath)) return false;
        using var document = JsonDocument.Parse(File.ReadAllBytes(UiStatePath));
        if (!document.RootElement.TryGetProperty("state", out var state) || !state.TryGetProperty("workspaces", out var workspaces)) return false;
        return workspaces.EnumerateArray().Any(workspace => workspace.TryGetProperty("trusted", out var trusted) && trusted.ValueKind == JsonValueKind.True &&
            workspace.TryGetProperty("path", out var path) && string.Equals(Path.GetFullPath(path.GetString() ?? "").TrimEnd('\\'), Path.GetFullPath(root).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
    }
    private static WorkspaceWorktrees Worktrees(string sourceRoot)
    {
        if (!IsTrustedWorktreeSource(sourceRoot)) throw new IOException("工作目录未信任，无法打开会话工作树。");
        if (!WorkspaceWorktrees.TryCreate(sourceRoot, out var store)) throw new IOException("未找到 Git，请安装 Git 后使用会话工作树。");
        return store;
    }
    private async Task PreviewWorkspaceWorktreeAsync(JsonElement request)
    {
        var requestId = ReadString(request, "requestId");
        try
        {
            var store = Worktrees(ReadString(request, "workspaceRoot"));
            var preview = await Task.Run(() => store.Inspect(ReadString(request, "conversationId"), ReadString(request, "worktreeCommit"))).ConfigureAwait(false);
            PostToUi(new { type = "worktree-preview", requestId, success = true, fingerprint = preview.Fingerprint,
                added = preview.Added, changed = preview.Changed, removed = preview.Removed, directoryChanges = preview.DirectoryChanges, changes = preview.Changes });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or JsonException or InvalidOperationException)
        { PostToUi(new { type = "worktree-preview", requestId, success = false, error = ex.Message }); }
    }
    private async Task RestoreWorkspaceWorktreeAsync(JsonElement request)
    {
        var requestId = ReadString(request, "requestId");
        if (Interlocked.CompareExchange(ref _controllerModelRequestActive, 1, 0) != 0)
        { PostToUi(new { type = "worktree-restored", requestId, success = false, error = "模型请求运行中，无法回退。" }); return; }
        try
        {
            var conversationId = ReadString(request, "conversationId");
            await _gatewayTerminalPool.CloseAsync(conversationId).ConfigureAwait(false);
            var store = Worktrees(ReadString(request, "workspaceRoot"));
            var previous = store.Open(conversationId);
            var active = await Task.Run(() => store.Restore(conversationId, ReadString(request, "worktreeCommit"), ReadString(request, "fingerprint"))).ConfigureAwait(false);
            PostToUi(new { type = "worktree-restored", requestId, success = true, worktreeRoot = store.ExecutionRoot(active),
                branch = active.Branch, head = active.Head, previousWorktreeRoot = store.ExecutionRoot(previous) });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or JsonException or InvalidOperationException)
        { PostToUi(new { type = "worktree-restored", requestId, success = false, error = ex.Message }); }
        finally { Interlocked.Exchange(ref _controllerModelRequestActive, 0); }
    }
}
