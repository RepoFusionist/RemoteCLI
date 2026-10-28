namespace RemoteCli.Desktop;

// Terminal sessions belong to a conversation, not an individual model round.
internal sealed class GatewayTerminalPool(Func<string, GatewayCommandSession> create, Func<DateTimeOffset>? clock = null) : IAsyncDisposable
{
    private sealed class Entry(GatewayCommandSession session, DateTimeOffset now) { internal GatewayCommandSession Session { get; } = session; internal DateTimeOffset LastUsed { get; set; } = now; }
    private readonly Dictionary<Guid, Entry> sessions = new();
    private readonly SemaphoreSlim gate = new(1, 1);
    private bool disposed;

    internal async Task<GatewayCommandSession> GetAsync(string conversationId, string workspace, CancellationToken token)
    {
        if (!Guid.TryParse(conversationId, out var id) || id == Guid.Empty) throw new ArgumentException("对话 ID 无效。");
        var path = Path.GetFullPath(workspace);
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (disposed) throw new InvalidOperationException("终端会话池已关闭。");
            var now = clock?.Invoke() ?? DateTimeOffset.UtcNow;
            foreach (var stale in sessions.Where(pair => now - pair.Value.LastUsed > TimeSpan.FromMinutes(5) && !pair.Value.Session.HasRunningCommands).Select(pair => pair.Key).ToArray())
            {
                var old = sessions[stale]; sessions.Remove(stale); await old.Session.DisposeAsync().ConfigureAwait(false);
            }
            if (sessions.TryGetValue(id, out var entry))
            {
                if (string.Equals(Path.GetFullPath(entry.Session.Workspace), path, StringComparison.OrdinalIgnoreCase))
                { entry.LastUsed = now; return entry.Session; }
                sessions.Remove(id); await entry.Session.DisposeAsync().ConfigureAwait(false);
            }
            if (sessions.Count >= 32) throw new InvalidOperationException("终端会话数量已达上限，请关闭不再需要的对话。");
            var session = create(path);
            sessions.Add(id, new Entry(session, now));
            return session;
        }
        finally { gate.Release(); }
    }

    internal async Task CloseAsync(string conversationId)
    {
        if (!Guid.TryParse(conversationId, out var id)) return;
        await gate.WaitAsync().ConfigureAwait(false);
        try { if (sessions.Remove(id, out var entry)) await entry.Session.DisposeAsync().ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    // Watching or interacting with an existing conversation must never create a new terminal.
    internal async Task<GatewayCommandSession?> FindAsync(string conversationId, CancellationToken token)
    {
        if (!Guid.TryParse(conversationId, out var id) || id == Guid.Empty) return null;
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (disposed || !sessions.TryGetValue(id, out var entry)) return null;
            entry.LastUsed = clock?.Invoke() ?? DateTimeOffset.UtcNow;
            return entry.Session;
        }
        finally { gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (disposed) return;
            disposed = true;
            await Task.WhenAll(sessions.Values.Select(entry => entry.Session.DisposeAsync().AsTask())).ConfigureAwait(false);
            sessions.Clear();
        }
        finally { gate.Release(); }
    }
}
