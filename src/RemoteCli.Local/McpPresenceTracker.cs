namespace RemoteCli.Local;

/// <summary>
/// Tracks MCP clients that recently reached the desktop service. This is display-only state;
/// remote authorization always comes from LocalConnectionStateMachine.
/// </summary>
public sealed class McpPresenceTracker
{
    private static readonly TimeSpan Lease = TimeSpan.FromSeconds(8);
    private readonly object _sync = new();
    private readonly Dictionary<Guid, DateTimeOffset> _clients = new();

    public void Refresh(Guid clientId, DateTimeOffset? now = null)
    {
        if (clientId == Guid.Empty)
        {
            throw new ArgumentException("An MCP client ID must be non-empty.", nameof(clientId));
        }

        lock (_sync)
        {
            var instant = now ?? DateTimeOffset.UtcNow;
            Prune(instant);
            _clients[clientId] = instant;
        }
    }

    public void Remove(Guid clientId)
    {
        lock (_sync)
        {
            _clients.Remove(clientId);
        }
    }

    public bool IsConnected(DateTimeOffset? now = null)
    {
        lock (_sync)
        {
            Prune(now ?? DateTimeOffset.UtcNow);
            return _clients.Count > 0;
        }
    }

    private void Prune(DateTimeOffset now)
    {
        foreach (var client in _clients.Where(item => now - item.Value >= Lease).ToArray())
        {
            _clients.Remove(client.Key);
        }
    }
}
