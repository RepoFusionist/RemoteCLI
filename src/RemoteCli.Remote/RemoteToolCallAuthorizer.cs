using Common.Protocol;

namespace RemoteCli.Remote;

public sealed class RemoteToolCallAuthorizer
{
    private const int MaximumRememberedOperationIds = 4096;
    private readonly object _sync = new();
    private readonly Guid _sessionId;
    private RemotePermission _permissions;
    private readonly DateTimeOffset _expiresAt;
    private readonly HashSet<Guid> _seenOperationIds = [];
    private readonly Queue<Guid> _recentOperationIds = new();
    private long _lastSequence;
    private bool _closed;

    public RemoteToolCallAuthorizer(Guid sessionId, RemotePermission permissions, DateTimeOffset expiresAt)
    {
        if (sessionId == Guid.Empty)
        {
            throw new ArgumentException("Session identifier must not be empty.", nameof(sessionId));
        }

        _sessionId = sessionId;
        _permissions = permissions;
        _expiresAt = expiresAt;
    }

    public bool TryAuthorize(
        RemoteToolCallRequest request,
        IRemoteToolRegistry registry,
        DateTimeOffset now,
        out string errorCode)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(registry);
        errorCode = string.Empty;
        lock (_sync)
        {
            if (_closed || now >= _expiresAt || request.SessionId != _sessionId || request.ProtocolVersion != ProtocolConstants.CurrentVersion)
            {
                errorCode = "session_unavailable";
                return false;
            }

            if ((_permissions & RemotePermission.CommandExecute) == 0)
            {
                if (request.OperationId != Guid.Empty && request.Sequence == _lastSequence + 1) _lastSequence = request.Sequence;
                errorCode = "permission_denied";
                return false;
            }

            if (request.OperationId == Guid.Empty || request.Sequence != _lastSequence + 1 || _seenOperationIds.Contains(request.OperationId))
            {
                errorCode = "replay_or_invalid_sequence";
                return false;
            }

            // A valid attempt consumes its sequence even if a changing catalog rejects the call.
            // Otherwise one stale schema would poison every subsequent call in a long session.
            _lastSequence = request.Sequence;

            if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 128 || request.Name.Any(char.IsControl))
            {
                errorCode = "invalid_tool_name";
                return false;
            }

            if (!registry.TryValidateCall(request.Name, request.Arguments, out errorCode))
            {
                if (string.IsNullOrEmpty(errorCode))
                {
                    errorCode = "invalid_tool_call";
                }

                return false;
            }

            _seenOperationIds.Add(request.OperationId);
            _recentOperationIds.Enqueue(request.OperationId);
            if (_recentOperationIds.Count > MaximumRememberedOperationIds) _seenOperationIds.Remove(_recentOperationIds.Dequeue());
            _lastSequence = request.Sequence;
            errorCode = string.Empty;
            return true;
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
