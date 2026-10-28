using System.Text.Json;

namespace RemoteCli.Desktop;

internal sealed partial class MainForm
{
    private readonly SemaphoreSlim _checkpointGate = new(1, 1);
    private static WorkspaceCheckpoints Checkpoints() => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RemoteCLI", "checkpoints"));

    private static bool IsTrustedCheckpointWorkspace(string root)
    {
        if (string.IsNullOrWhiteSpace(root) || !File.Exists(UiStatePath)) return false;
        using var document = JsonDocument.Parse(File.ReadAllBytes(UiStatePath));
        if (!document.RootElement.TryGetProperty("state", out var state) ||
            !state.TryGetProperty("workspaces", out var workspaces) || workspaces.ValueKind != JsonValueKind.Array) return false;
        var expected = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return workspaces.EnumerateArray().Any(workspace => workspace.ValueKind == JsonValueKind.Object &&
            workspace.TryGetProperty("trusted", out var trusted) && trusted.ValueKind == JsonValueKind.True &&
            workspace.TryGetProperty("path", out var path) && path.ValueKind == JsonValueKind.String &&
            MatchesCheckpointRoot(path.GetString(), expected));
    }

    private static bool MatchesCheckpointRoot(string? candidate, string expected)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return false;
        try
        {
            return string.Equals(Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                expected, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    private async Task<(string? Id, string? Error)> CaptureWorkspaceCheckpointAsync(string root)
    {
        if (string.IsNullOrWhiteSpace(root)) return (null, "未提供工作区路径，无法保存快照。");
        await _checkpointGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!IsTrustedCheckpointWorkspace(root)) return (null, "工作区未信任，无法保存快照。");
            return (await Task.Run(() => Checkpoints().Capture(root)).ConfigureAwait(false), null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or JsonException or NotSupportedException or System.Security.SecurityException or OverflowException)
        {
            return (null, "未能保存工作区快照：" + exception.Message);
        }
        finally { _checkpointGate.Release(); }
    }

    private async Task PreviewWorkspaceCheckpointAsync(JsonElement request)
    {
        var id = ReadString(request, "checkpointId");
        var root = ReadString(request, "workspaceRoot");
        var requestId = ReadString(request, "requestId");
        await _checkpointGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!IsTrustedCheckpointWorkspace(root)) throw new IOException("工作区未信任或已移除。");
            var preview = await Task.Run(() => Checkpoints().Inspect(root, id)).ConfigureAwait(false);
            PostToUi(new { type = "checkpoint-preview", requestId, checkpointId = id, success = true,
                fingerprint = preview.Fingerprint, added = preview.Added, changed = preview.Changed, removed = preview.Removed,
                directoryChanges = preview.DirectoryChanges, changes = preview.Changes });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or JsonException or InvalidDataException or NotSupportedException or System.Security.SecurityException or OverflowException)
        {
            PostToUi(new { type = "checkpoint-preview", requestId, checkpointId = id, success = false, error = exception.Message });
        }
        finally { _checkpointGate.Release(); }
    }

    private async Task RestoreWorkspaceCheckpointAsync(JsonElement request)
    {
        var id = ReadString(request, "checkpointId");
        var root = ReadString(request, "workspaceRoot");
        var requestId = ReadString(request, "requestId");
        var fingerprint = ReadString(request, "fingerprint");
        if (Interlocked.CompareExchange(ref _controllerModelRequestActive, 1, 0) != 0)
        {
            PostToUi(new { type = "checkpoint-restored", requestId, success = false, error = "模型请求正在运行，无法回退工作区。" });
            return;
        }
        await _checkpointGate.WaitAsync().ConfigureAwait(false);
        string? backupId = null;
        try
        {
            if (!IsTrustedCheckpointWorkspace(root)) throw new IOException("工作区未信任或已移除。");
            backupId = await Task.Run(() => Checkpoints().Restore(root, id, fingerprint, value => backupId = value)).ConfigureAwait(false);
            PostToUi(new { type = "checkpoint-restored", requestId, success = true, backupId });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or JsonException or InvalidDataException or NotSupportedException or System.Security.SecurityException or OverflowException)
        {
            PostToUi(new { type = "checkpoint-restored", requestId, success = false, backupId, error = exception.Message });
        }
        finally { _checkpointGate.Release(); Interlocked.Exchange(ref _controllerModelRequestActive, 0); }
    }
}
