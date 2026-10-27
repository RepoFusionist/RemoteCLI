using Common.Protocol;

namespace RemoteCli.Local;

public sealed class LocalConnectionStateMachine
{
    private readonly object _sync = new();
    private LocalConnectionStatus _snapshot;

    public LocalConnectionStateMachine(DateTimeOffset? now = null)
    {
        _snapshot = new LocalConnectionStatus(
            LocalIpcConstants.CurrentVersion,
            "Running",
            LocalConnectionState.Disconnected,
            false,
            null,
            null,
            RemotePermission.None,
            null,
            0,
            null,
            null,
            0,
            now ?? DateTimeOffset.UtcNow,
            null,
            null);
    }

    public event Action<LocalConnectionStatus>? StateChanged;

    public async Task<LocalConnectionStatus> WaitForChangeAsync(
        long afterStateVersion,
        TimeSpan maximumWait,
        CancellationToken cancellationToken)
    {
        if (afterStateVersion < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(afterStateVersion));
        }

        if (maximumWait < TimeSpan.Zero || maximumWait > TimeSpan.FromSeconds(20))
        {
            throw new ArgumentOutOfRangeException(nameof(maximumWait));
        }

        var current = GetSnapshot();
        if (current.StateVersion != afterStateVersion || maximumWait == TimeSpan.Zero)
        {
            return current;
        }

        if (current.ExpiresAt is { } expiresAt)
        {
            var untilExpiry = expiresAt - DateTimeOffset.UtcNow;
            if (untilExpiry <= TimeSpan.Zero)
            {
                return GetSnapshot();
            }

            if (untilExpiry < maximumWait)
            {
                maximumWait = untilExpiry;
            }
        }

        var completion = new TaskCompletionSource<LocalConnectionStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnChanged(LocalConnectionStatus changed)
        {
            if (changed.StateVersion != afterStateVersion)
            {
                completion.TrySetResult(changed);
            }
        }

        StateChanged += OnChanged;
        try
        {
            current = GetSnapshot();
            if (current.StateVersion != afterStateVersion)
            {
                return current;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(maximumWait);
            try
            {
                return await completion.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return GetSnapshot();
            }
        }
        finally
        {
            StateChanged -= OnChanged;
        }
    }

    public LocalConnectionStatus GetSnapshot(DateTimeOffset? now = null)
    {
        LocalConnectionStatus? expired = null;
        LocalConnectionStatus result;
        lock (_sync)
        {
            var instant = now ?? DateTimeOffset.UtcNow;
            if (_snapshot.ConnectionState == LocalConnectionState.Connected &&
                _snapshot.ExpiresAt is { } expiresAt && expiresAt <= instant)
            {
                _snapshot = CreateSnapshot(
                    LocalConnectionState.Expired,
                    authRequestId: null,
                    error: new LocalConnectionError("session", "session_expired", "The authenticated session has expired.", true, 0),
                    serverTime: instant);
                expired = _snapshot;
            }
            else if (_snapshot.ServerTime != instant)
            {
                _snapshot = _snapshot with { ServerTime = instant };
            }

            result = _snapshot;
        }

        if (expired is not null) Publish(expired);
        return result;
    }

    public (LocalConnectionStatus Status, bool Started) BeginAuthentication(
        Guid? requestedAuthRequestId = null,
        DateTimeOffset? now = null)
    {
        LocalConnectionStatus result;
        bool started;
        lock (_sync)
        {
            var instant = now ?? DateTimeOffset.UtcNow;
            if (_snapshot.ConnectionState is LocalConnectionState.Connecting or LocalConnectionState.AwaitingAuthentication or LocalConnectionState.Authenticating)
            {
                return (_snapshot with { ServerTime = instant }, false);
            }

            if (_snapshot.ConnectionState == LocalConnectionState.Connected && _snapshot.IsUsable(instant))
            {
                return (_snapshot with { ServerTime = instant }, false);
            }

            var authRequestId = requestedAuthRequestId.GetValueOrDefault();
            if (authRequestId == Guid.Empty)
            {
                authRequestId = Guid.NewGuid();
            }

            _snapshot = CreateSnapshot(
                LocalConnectionState.Connecting,
                authRequestId,
                error: null,
                serverTime: instant);
            result = _snapshot;
            started = true;
        }

        Publish(result);
        return (result, started);
    }

    public bool TrySetPhase(Guid authRequestId, LocalConnectionState state, DateTimeOffset? now = null)
    {
        if (state is not (LocalConnectionState.AwaitingAuthentication or LocalConnectionState.Authenticating))
        {
            throw new ArgumentOutOfRangeException(nameof(state), "Only authentication phases can be set through this method.");
        }

        LocalConnectionStatus result;
        lock (_sync)
        {
            if (_snapshot.AuthRequestId != authRequestId ||
                _snapshot.ConnectionState is not (LocalConnectionState.Connecting or LocalConnectionState.AwaitingAuthentication or LocalConnectionState.Authenticating))
            {
                return false;
            }

            if (state == LocalConnectionState.AwaitingAuthentication && _snapshot.ConnectionState == LocalConnectionState.Authenticating)
            {
                return false;
            }

            if (_snapshot.ConnectionState == state)
            {
                return true;
            }

            _snapshot = CreateSnapshot(state, authRequestId, error: null, serverTime: now ?? DateTimeOffset.UtcNow);
            result = _snapshot;
        }

        Publish(result);
        return true;
    }

    public void ApplyRemotePolicy(Guid sessionId, RemoteSessionPolicy policy)
    {
        LocalConnectionStatus? changed = null;
        lock (_sync)
        {
            if (_snapshot.SessionId != sessionId || _snapshot.ConnectionState != LocalConnectionState.Connected ||
                policy.Version <= _snapshot.RemotePolicyVersion) return;
            changed = _snapshot = _snapshot with
            {
                GrantedPermissions = policy.Permissions,
                RemoteWorkspace = policy.WorkspaceRoot,
                WorkspaceRestricted = policy.WorkspaceRestricted,
                RemotePolicyVersion = policy.Version,
                StateVersion = _snapshot.StateVersion + 1
            };
        }
        Publish(changed);
    }

    public bool TrySetConnected(
        Guid authRequestId,
        Guid sessionId,
        string remoteIdentity,
        string transport,
        RemotePermission permissions,
        DateTimeOffset expiresAt,
        string? remoteCapabilitiesVersion = null,
        DateTimeOffset? now = null)
    {
        if (authRequestId == Guid.Empty || sessionId == Guid.Empty || string.IsNullOrWhiteSpace(remoteIdentity) ||
            string.IsNullOrWhiteSpace(transport) || !IsValidPermissions(permissions))
        {
            throw new ArgumentException("A connected state requires authenticated identity, session, transport, and approved permissions.");
        }

        var instant = now ?? DateTimeOffset.UtcNow;
        if (expiresAt <= instant)
        {
            throw new ArgumentOutOfRangeException(nameof(expiresAt), "A connected session must not already be expired.");
        }

        if ((expiresAt != DateTimeOffset.MaxValue && expiresAt > instant.AddHours(24)) || remoteIdentity.Trim().Length > 256 || transport.Trim().Length > 64 ||
            remoteIdentity.Any(char.IsControl) || transport.Any(char.IsControl))
        {
            throw new ArgumentException("Connected session metadata exceeds its limits.");
        }

        LocalConnectionStatus result;
        lock (_sync)
        {
            if (_snapshot.AuthRequestId != authRequestId ||
                _snapshot.ConnectionState != LocalConnectionState.Authenticating)
            {
                return false;
            }

            _snapshot = new LocalConnectionStatus(
                LocalIpcConstants.CurrentVersion,
                "Running",
                LocalConnectionState.Connected,
                true,
                sessionId,
                transport,
                permissions,
                remoteCapabilitiesVersion,
                _snapshot.StateVersion + 1,
                authRequestId,
                null,
                0,
                instant,
                expiresAt,
                remoteIdentity.Trim());
            result = _snapshot;
        }

        Publish(result);
        return true;
    }

    public bool TrySetFaulted(
        Guid? authRequestId,
        string stage,
        string code,
        string message,
        bool retryable,
        int retryAfterMilliseconds = 0,
        DateTimeOffset? now = null)
    {
        LocalConnectionStatus result;
        lock (_sync)
        {
            if (authRequestId is { } expected && expected != _snapshot.AuthRequestId)
            {
                return false;
            }

            var error = new LocalConnectionError(
                Normalize(stage, nameof(stage)),
                Normalize(code, nameof(code)),
                Normalize(message, nameof(message)),
                retryable,
                Math.Clamp(retryAfterMilliseconds, 0, 30_000),
                authRequestId);
            _snapshot = CreateSnapshot(LocalConnectionState.Faulted, null, error, now ?? DateTimeOffset.UtcNow);
            result = _snapshot;
        }

        Publish(result);
        return true;
    }

    public bool TrySetDisconnected(Guid? sessionId = null, DateTimeOffset? now = null)
    {
        LocalConnectionStatus result;
        lock (_sync)
        {
            if (sessionId is { } expected && expected != _snapshot.SessionId)
            {
                return false;
            }

            if (_snapshot.ConnectionState == LocalConnectionState.Disconnected && _snapshot.SessionId is null && _snapshot.AuthRequestId is null)
            {
                return true;
            }

            _snapshot = CreateSnapshot(LocalConnectionState.Disconnected, null, null, now ?? DateTimeOffset.UtcNow);
            result = _snapshot;
        }

        Publish(result);
        return true;
    }

    public void Close(DateTimeOffset? now = null)
    {
        LocalConnectionStatus result;
        lock (_sync)
        {
            if (_snapshot.ConnectionState == LocalConnectionState.Closed && _snapshot.SessionId is null && _snapshot.AuthRequestId is null)
            {
                return;
            }

            _snapshot = CreateSnapshot(LocalConnectionState.Closed, null, null, now ?? DateTimeOffset.UtcNow);
            result = _snapshot;
        }

        Publish(result);
    }

    public bool IsAuthorized(Guid sessionId, RemotePermission permission, string? remoteIdentity = null, DateTimeOffset? now = null)
    {
        if (permission == RemotePermission.None || sessionId == Guid.Empty)
        {
            return false;
        }

        var snapshot = GetSnapshot(now);
        return snapshot.IsUsable(now ?? DateTimeOffset.UtcNow) &&
               snapshot.SessionId == sessionId &&
               (snapshot.GrantedPermissions & permission) == permission &&
               (remoteIdentity is null || string.Equals(snapshot.RemoteIdentity, remoteIdentity, StringComparison.Ordinal));
    }

    public bool MatchesPrecondition(Guid? expectedSessionId, long? expectedStateVersion)
    {
        var snapshot = GetSnapshot();
        return (expectedSessionId is null || snapshot.SessionId == expectedSessionId) &&
               (expectedStateVersion is null || snapshot.StateVersion == expectedStateVersion);
    }

    private LocalConnectionStatus CreateSnapshot(
        LocalConnectionState state,
        Guid? authRequestId,
        LocalConnectionError? error,
        DateTimeOffset serverTime)
    {
        return new LocalConnectionStatus(
            LocalIpcConstants.CurrentVersion,
            "Running",
            state,
            false,
            null,
            null,
            RemotePermission.None,
            null,
            _snapshot.StateVersion + 1,
            authRequestId,
            error,
            error?.RetryAfterMilliseconds ?? (state is LocalConnectionState.Connecting or LocalConnectionState.AwaitingAuthentication or LocalConnectionState.Authenticating ? 1_000 : 0),
            serverTime,
            null,
            null);
    }

    private void Publish(LocalConnectionStatus status)
    {
        try
        {
            StateChanged?.Invoke(status);
        }
        catch
        {
            // Observers are diagnostic/UI listeners and cannot veto a service state transition.
        }
    }

    private static string Normalize(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Value must not be empty.", name);
        }

        var normalized = value.Trim();
        if (normalized.Length > 256 || normalized.Any(char.IsControl))
        {
            throw new ArgumentException("Value is too long or contains control characters.", name);
        }

        return normalized;
    }

    private static bool IsValidPermissions(RemotePermission permissions)
    {
        const RemotePermission all = RemotePermission.SessionRead | RemotePermission.CommandExecute | RemotePermission.CommandCancel;
        return permissions != RemotePermission.None && (permissions & ~all) == 0;
    }
}
