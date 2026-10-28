using Common.Protocol;

namespace Common.Security;

public sealed class SessionCommandAuthorizer
{
    private const int MaximumRememberedCommandIds = 4096;
    private readonly object _sync = new();
    private readonly Guid _sessionId;
    private RemotePermission _permissions;
    private readonly DateTimeOffset _expiresAt;
    private readonly HashSet<Guid> _seenCommandIds = [];
    private readonly Queue<Guid> _recentCommandIds = new();
    private long _lastSequence;
    private bool _closed;

    public SessionCommandAuthorizer(
        Guid sessionId,
        RemotePermission approvedPermissions,
        DateTimeOffset expiresAt)
    {
        if (sessionId == Guid.Empty)
        {
            throw new ArgumentException("Session identifier must not be empty.", nameof(sessionId));
        }

        const RemotePermission all = RemotePermission.SessionRead | RemotePermission.CommandExecute |
            RemotePermission.CommandCancel | RemotePermission.ControllerModelUse;
        if ((approvedPermissions & ~all) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(approvedPermissions));
        }

        _sessionId = sessionId;
        _permissions = approvedPermissions;
        _expiresAt = expiresAt;
    }

    public bool TryAuthorizeCommand(RemoteCommandRequest request, DateTimeOffset now, out string errorCode)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_sync)
        {
            if (_closed)
            {
                errorCode = "session_closed";
                return false;
            }

            if (now >= _expiresAt)
            {
                errorCode = "session_expired";
                return false;
            }

            if ((_permissions & RemotePermission.CommandExecute) == 0)
            {
                if (request.ProtocolVersion == ProtocolConstants.CurrentVersion && request.SessionId == _sessionId &&
                    request.CommandId != Guid.Empty && request.Sequence == _lastSequence + 1) _lastSequence = request.Sequence;
                errorCode = "permission_denied";
                return false;
            }

            if (request.ProtocolVersion != ProtocolConstants.CurrentVersion || request.SessionId != _sessionId)
            {
                errorCode = "session_mismatch";
                return false;
            }

            if (request.CommandId == Guid.Empty || request.Sequence != _lastSequence + 1)
            {
                errorCode = "replay_or_invalid_sequence";
                return false;
            }

            if (string.IsNullOrWhiteSpace(request.CommandText) || request.CommandText.Length > ProtocolConstants.MaximumCommandCharacters)
            {
                errorCode = "invalid_command";
                return false;
            }

            if (request.TimeoutSeconds is < 1 or > 86_400)
            {
                errorCode = "invalid_timeout";
                return false;
            }

            if (!_seenCommandIds.Add(request.CommandId))
            {
                errorCode = "duplicate_command";
                return false;
            }

            _recentCommandIds.Enqueue(request.CommandId);
            if (_recentCommandIds.Count > MaximumRememberedCommandIds) _seenCommandIds.Remove(_recentCommandIds.Dequeue());

            _lastSequence = request.Sequence;
            errorCode = string.Empty;
            return true;
        }
    }

    public bool CanCancel(Guid sessionId, DateTimeOffset now)
    {
        lock (_sync)
        {
            return !_closed && now < _expiresAt && sessionId == _sessionId && (_permissions & RemotePermission.CommandCancel) != 0;
        }
    }

    public bool CanReadStatus(Guid sessionId, DateTimeOffset now)
    {
        lock (_sync)
        {
            return !_closed && now < _expiresAt && sessionId == _sessionId && (_permissions & RemotePermission.SessionRead) != 0;
        }
    }

    public bool CanExecute(Guid sessionId, DateTimeOffset now)
    {
        lock (_sync)
        {
            return !_closed && now < _expiresAt && sessionId == _sessionId && (_permissions & RemotePermission.CommandExecute) != 0;
        }
    }

    public void Close()
    {
        lock (_sync)
        {
            _closed = true;
        }
    }

    public void UpdatePermissions(RemotePermission permissions) { lock (_sync) _permissions = permissions; }
}
