using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;
using Common.Protocol;
using Common.Security;

namespace RemoteCli.Vps;

public sealed class RelayRoomService : IAsyncDisposable
{
    private const int MaximumPendingInvitations = 256;
    private const long MaximumSessionBytes = 256L * 1024 * 1024;
    private readonly object _capacityLock = new();
    private readonly ConcurrentDictionary<Guid, RelayRoom> _rooms = new();
    private readonly ConcurrentDictionary<string, AuthorizationOwner> _authorizations =
        new(StringComparer.Ordinal);
    private readonly Action<ReadOnlyMemory<byte>>? _frameObserver;

    public RelayRoomService()
    {
    }

    internal RelayRoomService(Action<ReadOnlyMemory<byte>> frameObserver)
    {
        _frameObserver = frameObserver ?? throw new ArgumentNullException(nameof(frameObserver));
    }

    public bool TryRegisterAuthorization(ConnectionAuthorizationRegistration registration, out string errorCode)
    {
        ArgumentNullException.ThrowIfNull(registration);
        string userName;
        byte[] ownerDigest;
        try
        {
            userName = ConnectionAuthorizationCredential.NormalizeUserName(registration.UserName);
            ownerDigest = OneTimeInvitationFactory.HashSecret(registration.OwnerToken);
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        {
            errorCode = "invalid_authorization";
            return false;
        }

        var request = registration.Invitation;
        if (!string.Equals(request.UserName, userName, StringComparison.Ordinal) ||
            request.CredentialExpiresAt is not { } credentialExpiresAt ||
            credentialExpiresAt <= DateTimeOffset.UtcNow ||
            credentialExpiresAt > DateTimeOffset.UtcNow.AddDays(30))
        {
            CryptographicOperations.ZeroMemory(ownerDigest);
            errorCode = "invalid_authorization";
            return false;
        }

        if (!TryValidateInvitation(request, DateTimeOffset.UtcNow, out errorCode))
        {
            CryptographicOperations.ZeroMemory(ownerDigest);
            return false;
        }

        RelayRoom? replacedRoom = null;
        lock (_capacityLock)
        {
            var now = DateTimeOffset.UtcNow;
            PruneExpired(now);
            if (_authorizations.TryGetValue(userName, out var existing))
            {
                if (!CryptographicOperations.FixedTimeEquals(existing.OwnerDigest, ownerDigest))
                {
                    CryptographicOperations.ZeroMemory(ownerDigest);
                    errorCode = "username_in_use";
                    return false;
                }

            }

            if (_rooms.ContainsKey(request.SessionId))
            {
                CryptographicOperations.ZeroMemory(ownerDigest);
                errorCode = "invitation_exists";
                return false;
            }

            var replacingExisting = existing is not null;
            if (!replacingExisting && _rooms.Count >= MaximumPendingInvitations)
            {
                CryptographicOperations.ZeroMemory(ownerDigest);
                errorCode = "relay_capacity";
                return false;
            }

            if (existing is not null)
            {
                _authorizations.TryRemove(userName, out _);
                CryptographicOperations.ZeroMemory(existing.OwnerDigest);
                _rooms.TryRemove(existing.SessionId, out replacedRoom);
            }

            var room = new RelayRoom(request, MaximumSessionBytes, RemoveRoom, _frameObserver);
            if (!_rooms.TryAdd(request.SessionId, room))
            {
                CryptographicOperations.ZeroMemory(ownerDigest);
                errorCode = "invitation_exists";
                return false;
            }

            _authorizations[userName] = new AuthorizationOwner(request.SessionId, ownerDigest, request.ExpiresAt);
        }

        if (replacedRoom is not null)
        {
            _ = replacedRoom.DisposeAsync();
        }

        errorCode = string.Empty;
        return true;
    }

    public bool TryGetAuthorization(string userName, out InvitationRequest? request)
    {
        request = null;
        try
        {
            userName = ConnectionAuthorizationCredential.NormalizeUserName(userName);
        }
        catch (ArgumentException)
        {
            request = null;
            return false;
        }

        return _authorizations.TryGetValue(userName, out var authorization) &&
            authorization.LeaseExpiresAt > DateTimeOffset.UtcNow &&
            TryGetInvitation(authorization.SessionId, out request);
    }

    public bool TryRevokeAuthorization(
        string userName,
        ConnectionAuthorizationRevocation revocation,
        out string errorCode)
    {
        ArgumentNullException.ThrowIfNull(revocation);
        byte[] ownerDigest;
        try
        {
            userName = ConnectionAuthorizationCredential.NormalizeUserName(userName);
            ownerDigest = OneTimeInvitationFactory.HashSecret(revocation.OwnerToken);
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        {
            errorCode = "invalid_authorization";
            return false;
        }

        RelayRoom? room = null;
        lock (_capacityLock)
        {
            if (!_authorizations.TryGetValue(userName, out var existing) ||
                !CryptographicOperations.FixedTimeEquals(existing.OwnerDigest, ownerDigest))
            {
                CryptographicOperations.ZeroMemory(ownerDigest);
                errorCode = "authorization_not_found";
                return false;
            }

            CryptographicOperations.ZeroMemory(ownerDigest);
            _authorizations.TryRemove(userName, out _);
            CryptographicOperations.ZeroMemory(existing.OwnerDigest);
            _rooms.TryRemove(existing.SessionId, out room);
        }

        if (room is not null)
        {
            _ = room.DisposeAsync();
        }

        errorCode = string.Empty;
        return true;
    }

    public bool TryRegister(InvitationRequest request, out string errorCode)
    {
        ArgumentNullException.ThrowIfNull(request);
        var now = DateTimeOffset.UtcNow;

        if (!TryValidateInvitation(request, now, out errorCode))
        {
            return false;
        }

        lock (_capacityLock)
        {
            PruneExpired(now);
            if (_rooms.Count >= MaximumPendingInvitations)
            {
                errorCode = "relay_capacity";
                return false;
            }

            var room = new RelayRoom(request, MaximumSessionBytes, RemoveRoom, _frameObserver);
            if (!_rooms.TryAdd(request.SessionId, room))
            {
                errorCode = "invitation_exists";
                return false;
            }
        }

        errorCode = string.Empty;
        return true;
    }

    public bool TryGetInvitation(Guid sessionId, out InvitationRequest? request)
    {
        if (_rooms.TryGetValue(sessionId, out var room) && !room.IsExpired)
        {
            request = room.Request;
            return true;
        }

        request = null;
        return false;
    }

    public async Task JoinAsync(Guid sessionId, string role, WebSocket socket, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(socket);
        if (!_rooms.TryGetValue(sessionId, out var room) || room.IsExpired)
        {
            throw new RelayJoinException("invitation_not_found");
        }

        var relayRole = role switch
        {
            "remote" => RelayRole.Remote,
            "local" => RelayRole.Local,
            _ => throw new RelayJoinException("invalid_role")
        };

        await room.JoinAsync(relayRole, socket, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var pair in _rooms)
        {
            if (_rooms.TryRemove(pair.Key, out var room))
            {
                await room.DisposeAsync().ConfigureAwait(false);
            }
        }

        foreach (var pair in _authorizations)
        {
            if (_authorizations.TryRemove(pair.Key, out var authorization))
            {
                CryptographicOperations.ZeroMemory(authorization.OwnerDigest);
            }
        }
    }

    private void PruneExpired(DateTimeOffset now)
    {
        foreach (var pair in _rooms)
        {
            if (pair.Value.IsExpired && _rooms.TryRemove(pair.Key, out var room))
            {
                _ = room.DisposeAsync();
            }
        }

        foreach (var pair in _authorizations)
        {
            if (pair.Value.LeaseExpiresAt <= now &&
                (!_rooms.TryGetValue(pair.Value.SessionId, out var activeRoom) || activeRoom.IsExpired) &&
                _authorizations.TryRemove(pair.Key, out var authorization))
            {
                CryptographicOperations.ZeroMemory(authorization.OwnerDigest);
            }
        }
    }

    private void RemoveRoom(Guid sessionId)
    {
        _rooms.TryRemove(sessionId, out _);
    }

    private static bool TryValidateInvitation(InvitationRequest request, DateTimeOffset now, out string errorCode)
    {
        if (request.ProtocolVersion != ProtocolConstants.CurrentVersion || request.SessionId == Guid.Empty ||
            string.IsNullOrWhiteSpace(request.RemoteIdentity) || request.RemoteIdentity.Length > 128 ||
            request.RemoteIdentity.Any(char.IsControl))
        {
            errorCode = "invalid_invitation";
            return false;
        }

        if (request.ExpiresAt <= now || request.ExpiresAt > now.AddMinutes(10))
        {
            errorCode = "invalid_expiry";
            return false;
        }

        const RemotePermission all = RemotePermission.SessionRead | RemotePermission.CommandExecute |
            RemotePermission.CommandCancel | RemotePermission.ControllerModelUse;
        if ((request.RequestedPermissions & ~all) != 0)
        {
            errorCode = "invalid_permissions";
            return false;
        }

        errorCode = string.Empty;
        return true;
    }

    private sealed record AuthorizationOwner(Guid SessionId, byte[] OwnerDigest, DateTimeOffset LeaseExpiresAt);

    private enum RelayRole
    {
        Remote,
        Local
    }

    private sealed class RelayRoom : IAsyncDisposable
    {
        private const int MaximumFrameBytes = 1024 * 1024;
        private const int ReceiveBufferBytes = 16 * 1024;
        private static readonly TimeSpan CloseHandshakeTimeout = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan ActiveIdleTimeout = TimeSpan.FromSeconds(90);
        private readonly object _sync = new();
        private readonly long _maximumSessionBytes;
        private readonly Action<Guid> _onClosed;
        private readonly Action<ReadOnlyMemory<byte>>? _frameObserver;
        private readonly CancellationTokenSource _expires;
        private readonly TaskCompletionSource<bool> _relayTask = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private WebSocket? _remote;
        private WebSocket? _local;
        private long _bytesTransferred;
        private bool _started;
        private bool _disposed;

        public RelayRoom(
            InvitationRequest request,
            long maximumSessionBytes,
            Action<Guid> onClosed,
            Action<ReadOnlyMemory<byte>>? frameObserver)
        {
            Request = request;
            _maximumSessionBytes = maximumSessionBytes;
            _onClosed = onClosed;
            _frameObserver = frameObserver;
            _expires = new CancellationTokenSource(request.ExpiresAt - DateTimeOffset.UtcNow);
            _ = ExpireAsync();
        }

        public InvitationRequest Request { get; }
        public bool IsExpired => (!_started && DateTimeOffset.UtcNow >= Request.ExpiresAt) || _disposed;

        public async Task JoinAsync(RelayRole role, WebSocket socket, CancellationToken cancellationToken)
        {
            Task relayTask;
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (role == RelayRole.Remote)
                {
                    if (_remote is not null)
                    {
                        throw new RelayJoinException("remote_already_connected");
                    }

                    _remote = socket;
                }
                else
                {
                    if (_local is not null)
                    {
                        throw new RelayJoinException("local_already_connected");
                    }

                    _local = socket;
                }

                if (_remote is not null && _local is not null && !_started)
                {
                    _started = true;
                    // Pairing deadlines do not limit established transports. Encrypted peer
                    // heartbeats keep live sessions open; abandoned pairs still expire.
                    _expires.CancelAfter(ActiveIdleTimeout);
                    _ = RelayAndCompleteAsync(_remote, _local, _expires.Token);
                }

                relayTask = _relayTask.Task;
            }

            try
            {
                await relayTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            WebSocket? remote;
            WebSocket? local;
            lock (_sync)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                remote = _remote;
                local = _local;
            }

            _expires.Cancel();
            _relayTask.TrySetResult(false);
            remote?.Abort();
            local?.Abort();
            if (remote is not null)
            {
                remote.Dispose();
            }

            if (local is not null)
            {
                local.Dispose();
            }

            _expires.Dispose();
            _onClosed(Request.SessionId);
            await Task.CompletedTask.ConfigureAwait(false);
        }

        private async Task ExpireAsync()
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, _expires.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await DisposeAsync().ConfigureAwait(false);
            }
        }

        private async Task<bool> RelayAsync(WebSocket remote, WebSocket local, CancellationToken cancellationToken)
        {
            using var closeSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var remoteToLocal = ForwardAsync(remote, local, closeSource.Token);
            var localToRemote = ForwardAsync(local, remote, closeSource.Token);
            var completedCleanly = false;
            try
            {
                var firstCompleted = await Task.WhenAny(remoteToLocal, localToRemote).ConfigureAwait(false);
                if (!firstCompleted.IsFaulted && !firstCompleted.IsCanceled && !cancellationToken.IsCancellationRequested)
                {
                    var pumps = Task.WhenAll(remoteToLocal, localToRemote);
                    var closeTimeout = Task.Delay(CloseHandshakeTimeout, cancellationToken);
                    if (await Task.WhenAny(pumps, closeTimeout).ConfigureAwait(false) == pumps)
                    {
                        await pumps.ConfigureAwait(false);
                        completedCleanly = true;
                    }
                }
            }
            catch (Exception exception) when (exception is OperationCanceledException or WebSocketException or IOException or InvalidDataException or InvalidOperationException)
            {
            }
            finally
            {
                if (!completedCleanly)
                {
                    closeSource.Cancel();
                    remote.Abort();
                    local.Abort();
                    try
                    {
                        await Task.WhenAll(remoteToLocal, localToRemote).ConfigureAwait(false);
                    }
                    catch (Exception exception) when (exception is OperationCanceledException or WebSocketException or IOException or InvalidDataException or InvalidOperationException)
                    {
                    }
                }

                await DisposeAsync().ConfigureAwait(false);
            }

            return completedCleanly;
        }

        private async Task RelayAndCompleteAsync(WebSocket remote, WebSocket local, CancellationToken cancellationToken)
        {
            try
            {
                var completedCleanly = await RelayAsync(remote, local, cancellationToken).ConfigureAwait(false);
                _relayTask.TrySetResult(completedCleanly);
            }
            catch (Exception exception) when (exception is OperationCanceledException or WebSocketException or IOException or InvalidDataException or InvalidOperationException)
            {
                _relayTask.TrySetResult(false);
            }
        }

        private async Task ForwardAsync(WebSocket source, WebSocket destination, CancellationToken cancellationToken)
        {
            var receiveBuffer = new byte[ReceiveBufferBytes];
            while (!cancellationToken.IsCancellationRequested)
            {
                using var message = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await source.ReceiveAsync(receiveBuffer, cancellationToken).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        var closeStatus = result.CloseStatus ?? WebSocketCloseStatus.NormalClosure;
                        if (destination.State is WebSocketState.Open or WebSocketState.CloseReceived)
                        {
                            await destination.CloseOutputAsync(
                                closeStatus,
                                result.CloseStatusDescription,
                                cancellationToken).ConfigureAwait(false);
                        }

                        return;
                    }

                    if (result.MessageType != WebSocketMessageType.Binary || message.Length + result.Count > MaximumFrameBytes)
                    {
                        throw new InvalidDataException("Relay accepts only bounded binary frames.");
                    }

                    message.Write(receiveBuffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                var length = message.Length;
                if (Interlocked.Add(ref _bytesTransferred, length) > _maximumSessionBytes)
                {
                    throw new InvalidDataException("Relay session exceeded its byte limit.");
                }

                var bytes = message.ToArray();
                try
                {
                    _frameObserver?.Invoke(bytes);
                    await destination.SendAsync(bytes, WebSocketMessageType.Binary, endOfMessage: true, cancellationToken).ConfigureAwait(false);
                    lock (_sync)
                    {
                        if (!_disposed) _expires.CancelAfter(ActiveIdleTimeout);
                    }
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(bytes);
                }
            }
        }
    }
}

public sealed class RelayJoinException(string errorCode) : Exception(errorCode)
{
    public string ErrorCode { get; } = errorCode;
}
