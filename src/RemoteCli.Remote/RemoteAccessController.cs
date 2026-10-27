using Common.Protocol;

namespace RemoteCli.Remote;

/// <summary>The remote user owns this policy. Changes revoke all leases issued by the previous policy.</summary>
public sealed class RemoteAccessController : IDisposable
{
    private readonly object _sync = new();
    private CancellationTokenSource _generation = new();
    private RemoteSessionPolicy _policy;

    public RemoteAccessController(RemotePermission permissions = RemotePermission.SessionRead,
        string? workspaceRoot = null, bool workspaceRestricted = true, bool autoApproveTools = false)
    {
        _policy = new(1, permissions, NormalizeRoot(workspaceRoot), workspaceRestricted, autoApproveTools);
    }

    public event Action<RemoteSessionPolicy>? Changed;
    public RemoteSessionPolicy Snapshot { get { lock (_sync) return _policy; } }

    public (RemoteSessionPolicy Policy, CancellationToken Revoked) Acquire()
    {
        lock (_sync) return (_policy, _generation.Token);
    }

    public void Update(RemotePermission permissions, string? workspaceRoot, bool workspaceRestricted)
        => Update(permissions, workspaceRoot, workspaceRestricted, Snapshot.AutoApproveTools);

    public void Update(RemotePermission permissions, string? workspaceRoot, bool workspaceRestricted, bool autoApproveTools)
    {
        const RemotePermission all = RemotePermission.SessionRead | RemotePermission.CommandExecute | RemotePermission.CommandCancel;
        if ((permissions & ~all) != 0) throw new ArgumentOutOfRangeException(nameof(permissions));
        var root = NormalizeRoot(workspaceRoot);
        CancellationTokenSource previous;
        RemoteSessionPolicy changed;
        lock (_sync)
        {
            if (_policy.Permissions == permissions && _policy.WorkspaceRoot == root &&
                _policy.WorkspaceRestricted == workspaceRestricted && _policy.AutoApproveTools == autoApproveTools) return;
            previous = _generation;
            _generation = new();
            changed = _policy = new(checked(_policy.Version + 1), permissions, root, workspaceRestricted, autoApproveTools);
        }
        previous.Cancel();
        previous.Dispose();
        Changed?.Invoke(changed);
    }

    public static string? NormalizeRoot(string? root)
    {
        if (string.IsNullOrWhiteSpace(root)) return null;
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (!Directory.Exists(full)) throw new DirectoryNotFoundException("远程工作区不存在。");
        for (var current = new DirectoryInfo(full); current is not null; current = current.Parent)
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("工作区不能经过符号链接或目录联接。");
        return full;
    }

    public void Dispose()
    {
        lock (_sync) { _generation.Cancel(); _generation.Dispose(); }
    }

    public void Refresh()
    {
        CancellationTokenSource previous;
        RemoteSessionPolicy changed;
        lock (_sync)
        {
            previous = _generation;
            _generation = new();
            changed = _policy = _policy with { Version = checked(_policy.Version + 1) };
        }
        previous.Cancel(); previous.Dispose();
        Changed?.Invoke(changed);
    }
}

public sealed class DynamicRemoteToolRegistry(
    RemoteAccessController access,
    Func<RemoteSessionPolicy, IRemoteToolRegistry> createRegistry) : IRemoteToolRegistry
{
    private IRemoteToolRegistry? _registry;
    private long _version;
    private readonly object _sync = new();

    private IRemoteToolRegistry GetRegistry(RemoteSessionPolicy policy)
    {
        lock (_sync)
        {
            if (_registry is null || _version != policy.Version)
            {
                _registry = createRegistry(policy);
                _version = policy.Version;
            }
            return _registry;
        }
    }

    public string CapabilitiesVersion => GetRegistry(access.Snapshot).CapabilitiesVersion + ".policy-" + access.Snapshot.Version;
    public IReadOnlyList<RemoteToolDescriptor> ListTools() => GetRegistry(access.Snapshot).ListTools();
    public bool TryValidateCall(string name, System.Text.Json.JsonElement arguments, out string error) =>
        GetRegistry(access.Snapshot).TryValidateCall(name, arguments, out error);
    public async Task<System.Text.Json.JsonElement> ExecuteAsync(string name, System.Text.Json.JsonElement arguments, CancellationToken cancellationToken)
    {
        var lease = access.Acquire();
        using var revoked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lease.Revoked);
        revoked.Token.ThrowIfCancellationRequested();
        if ((lease.Policy.Permissions & RemotePermission.CommandExecute) == 0)
            throw new RemoteToolException("permission_denied", "被控端已关闭执行权限。");
        return await GetRegistry(lease.Policy).ExecuteAsync(name, arguments, revoked.Token).ConfigureAwait(false);
    }
}
