using System.Net.Security;
using System.Net.WebSockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Serialization;
using Common.Protocol;

namespace Common.Security;

public sealed record RemotePeerAnnouncement(
    Guid SessionId,
    string RemoteIdentity,
    DateTimeOffset ExpiresAt,
    RemotePermission RequestedPermissions);

public sealed class SecurePeerChannel : IAsyncDisposable
{
    private static readonly TimeSpan HelloPhaseTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan TlsPhaseTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MaximumPermissionApprovalTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(2);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 8,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        PropertyNameCaseInsensitive = false
    };

    private readonly X509Certificate2? _localCertificate;
    private readonly WebSocket _socket;
    private int _disposeStarted;

    private SecurePeerChannel(
        SslStream stream,
        WebSocket socket,
        X509Certificate2? localCertificate,
        Guid sessionId,
        string remoteIdentity,
        RemotePermission approvedPermissions,
        DateTimeOffset invitationExpiresAt,
        DateTimeOffset sessionExpiresAt)
    {
        Stream = stream;
        _socket = socket;
        _localCertificate = localCertificate;
        SessionId = sessionId;
        RemoteIdentity = remoteIdentity;
        ApprovedPermissions = approvedPermissions;
        InvitationExpiresAt = invitationExpiresAt;
        SessionExpiresAt = sessionExpiresAt;
    }

    public SslStream Stream { get; }
    public Guid SessionId { get; }
    public string RemoteIdentity { get; }
    public RemotePermission ApprovedPermissions { get; }
    public DateTimeOffset InvitationExpiresAt { get; }
    public DateTimeOffset SessionExpiresAt { get; }

    public static async Task<SecurePeerChannel> ConnectLocalAsync(
        WebSocket socket,
        Guid sessionId,
        string oneTimeSecret,
        string localIdentity,
        Func<RemotePeerAnnouncement, CancellationToken, Task<RemotePermission>> approvePermissions,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(socket);
        ArgumentNullException.ThrowIfNull(approvePermissions);
        ValidateIdentity(localIdentity);
        if (sessionId == Guid.Empty)
        {
            throw new ArgumentException("Session identifier must not be empty.", nameof(sessionId));
        }

        SslStream? sslStream = null;
        byte[]? remoteCertificateHash = null;
        try
        {
            var remoteHello = await RunBoundedPhaseAsync(
                "remote hello receive",
                HelloPhaseTimeout,
                cancellationToken,
                token => ReceiveHelloAsync(socket, token)).ConfigureAwait(false);
            ValidateHello(remoteHello, sessionId, "remote", oneTimeSecret);
            var expiresAt = DateTimeOffset.FromUnixTimeSeconds(remoteHello.ExpiresAtUnixSeconds);
            if (expiresAt <= DateTimeOffset.UtcNow || expiresAt > DateTimeOffset.UtcNow.AddHours(1))
            {
                throw new AuthenticationException("The invitation has expired or has an invalid lifetime.");
            }

            var announcement = new RemotePeerAnnouncement(sessionId, remoteHello.Identity, expiresAt, remoteHello.Permissions);
            var approvalTimeout = expiresAt - DateTimeOffset.UtcNow;
            if (approvalTimeout <= TimeSpan.Zero)
            {
                throw new AuthenticationException("The invitation expired before local permission approval.");
            }

            var invitationLimitsApproval = approvalTimeout <= MaximumPermissionApprovalTimeout;
            if (!invitationLimitsApproval)
            {
                approvalTimeout = MaximumPermissionApprovalTimeout;
            }

            // Classify our deadline by the budget selected above. Timer completion can precede
            // the wall-clock deadline by a fraction of a millisecond; callback errors must pass through.
            var approved = await RunBoundedPhaseAsync(
                "local permission approval",
                approvalTimeout,
                cancellationToken,
                token => approvePermissions(announcement, token),
                timeoutExceptionFactory: exception => invitationLimitsApproval || expiresAt <= DateTimeOffset.UtcNow
                    ? new AuthenticationException("The invitation expired while waiting for local permission approval.", exception)
                    : exception).ConfigureAwait(false);

            if (expiresAt <= DateTimeOffset.UtcNow)
            {
                throw new AuthenticationException("The invitation expired while waiting for local permission approval.");
            }

            if (OneTimeInvitationFactory.RestrictPermissions(remoteHello.Permissions, approved) != approved)
            {
                throw new AuthenticationException("The approved permissions exceed the remote request.");
            }

            var localHello = CreateHello(
                sessionId,
                "local",
                localIdentity,
                expiresAt,
                approved,
                certificate: null,
                remoteNonce: Convert.FromBase64String(remoteHello.Nonce),
                oneTimeSecret: oneTimeSecret);
            await RunBoundedPhaseAsync(
                "local approval hello send",
                HelloPhaseTimeout,
                cancellationToken,
                token => SendHelloAsync(socket, localHello, token)).ConfigureAwait(false);
            if (approved == RemotePermission.None)
            {
                throw new AuthenticationException("No remote permissions were approved.");
            }

            remoteCertificateHash = HashCertificate(remoteHello.Certificate);
            sslStream = new SslStream(
                new WebSocketByteStream(socket, ownsSocket: false),
                leaveInnerStreamOpen: false,
                (_, certificate, _, _) => CertificateMatches(certificate, remoteCertificateHash));
            await RunBoundedPhaseAsync(
                "TLS 1.3 client authentication",
                TlsPhaseTimeout,
                cancellationToken,
                token => sslStream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = "remotecli.session",
                    EnabledSslProtocols = SslProtocols.Tls13,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck
                }, token)).ConfigureAwait(false);

            var channel = new SecurePeerChannel(sslStream, socket, null, sessionId, remoteHello.Identity, approved, expiresAt, DateTimeOffset.MaxValue);
            sslStream = null;
            remoteCertificateHash = null;
            return channel;
        }
        catch
        {
            await AbortAndDisposeFailedHandshakeAsync(socket, sslStream).ConfigureAwait(false);
            throw;
        }
    }

    public static async Task<SecurePeerChannel> AcceptRemoteAsync(
        WebSocket socket,
        Guid sessionId,
        string oneTimeSecret,
        string remoteIdentity,
        DateTimeOffset invitationExpiresAt,
        RemotePermission requestedPermissions,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(socket);
        ValidateIdentity(remoteIdentity);
        if (sessionId == Guid.Empty || invitationExpiresAt <= DateTimeOffset.UtcNow)
        {
            throw new ArgumentException("The invitation is expired or invalid.");
        }

        X509Certificate2? remoteCertificate = null;
        SslStream? sslStream = null;
        try
        {
            remoteCertificate = CreateEphemeralCertificate();
            var remoteHello = CreateHello(
                sessionId,
                "remote",
                remoteIdentity,
                invitationExpiresAt,
                requestedPermissions,
                certificate: remoteCertificate,
                remoteNonce: null,
                oneTimeSecret: oneTimeSecret);
            await RunBoundedPhaseAsync(
                "remote hello send",
                HelloPhaseTimeout,
                cancellationToken,
                token => SendHelloAsync(socket, remoteHello, token)).ConfigureAwait(false);

            var localHelloTimeout = invitationExpiresAt - DateTimeOffset.UtcNow;
            if (localHelloTimeout <= TimeSpan.Zero)
            {
                throw new AuthenticationException("The invitation expired before local permission approval completed.");
            }

            if (localHelloTimeout > MaximumPermissionApprovalTimeout)
            {
                localHelloTimeout = MaximumPermissionApprovalTimeout;
            }

            var localHello = await RunBoundedPhaseAsync(
                "local hello receive",
                localHelloTimeout,
                cancellationToken,
                token => ReceiveHelloAsync(socket, token)).ConfigureAwait(false);
            ValidateHello(localHello, sessionId, "local", oneTimeSecret, Convert.FromBase64String(remoteHello.Nonce));

            var approved = OneTimeInvitationFactory.RestrictPermissions(requestedPermissions, localHello.Permissions);
            if (approved != localHello.Permissions || approved == RemotePermission.None)
            {
                throw new AuthenticationException("The local endpoint approved no permissions or approved an invalid set.");
            }

            sslStream = new SslStream(new WebSocketByteStream(socket, ownsSocket: false), leaveInnerStreamOpen: false);
            await RunBoundedPhaseAsync(
                "TLS 1.3 server authentication",
                TlsPhaseTimeout,
                cancellationToken,
                token => sslStream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = remoteCertificate,
                    ClientCertificateRequired = false,
                    EnabledSslProtocols = SslProtocols.Tls13,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck
                }, token)).ConfigureAwait(false);

            var channel = new SecurePeerChannel(sslStream, socket, remoteCertificate, sessionId, localHello.Identity, approved, invitationExpiresAt, DateTimeOffset.MaxValue);
            sslStream = null;
            remoteCertificate = null;
            return channel;
        }
        catch
        {
            await AbortAndDisposeFailedHandshakeAsync(socket, sslStream).ConfigureAwait(false);
            remoteCertificate?.Dispose();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
        {
            return;
        }

        try
        {
            await Stream.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            await CloseWebSocketAsync(_socket, WebSocketCloseStatus.NormalClosure, "session_closed").ConfigureAwait(false);
            _localCertificate?.Dispose();
        }
    }

    private static SecureHello CreateHello(
        Guid sessionId,
        string role,
        string identity,
        DateTimeOffset expiresAt,
        RemotePermission permissions,
        X509Certificate2? certificate,
        byte[]? remoteNonce,
        string oneTimeSecret)
    {
        var nonce = RandomNumberGenerator.GetBytes(32);
        var certificateBytes = certificate?.Export(X509ContentType.Cert) ?? [];
        byte[] challenge;
        if (role == "remote")
        {
            var certificateHash = SHA256.HashData(certificateBytes);
            challenge = new byte[64];
            nonce.CopyTo(challenge, 0);
            certificateHash.CopyTo(challenge, nonce.Length);
            CryptographicOperations.ZeroMemory(certificateHash);
        }
        else
        {
            if (remoteNonce is null || remoteNonce.Length != 32 || certificateBytes.Length != 0)
            {
                throw new ArgumentException("Local hello must bind to a remote nonce and carry no certificate.");
            }

            challenge = new byte[64];
            remoteNonce.CopyTo(challenge, 0);
            nonce.CopyTo(challenge, remoteNonce.Length);
        }

        var proof = OneTimeInvitationFactory.CreateApprovalProof(
            oneTimeSecret,
            sessionId,
            $"{role}:{identity}",
            permissions,
            challenge);
        try
        {
            return new SecureHello(
                ProtocolConstants.CurrentVersion,
                sessionId,
                role,
                identity,
                expiresAt.ToUnixTimeSeconds(),
                permissions,
                Convert.ToBase64String(nonce),
                certificateBytes.Length == 0 ? string.Empty : Convert.ToBase64String(certificateBytes),
                Convert.ToBase64String(proof));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(nonce);
            CryptographicOperations.ZeroMemory(certificateBytes);
            CryptographicOperations.ZeroMemory(challenge);
            CryptographicOperations.ZeroMemory(proof);
        }
    }

    private static void ValidateHello(
        SecureHello hello,
        Guid sessionId,
        string expectedRole,
        string oneTimeSecret,
        byte[]? remoteNonce = null)
    {
        if (hello.ProtocolVersion != ProtocolConstants.CurrentVersion || hello.SessionId != sessionId ||
            !string.Equals(hello.Role, expectedRole, StringComparison.Ordinal))
        {
            throw new AuthenticationException("Peer hello does not match this session or role.");
        }

        ValidateIdentity(hello.Identity);
        var nonce = Convert.FromBase64String(hello.Nonce);
        var proof = Convert.FromBase64String(hello.Proof);
        if (nonce.Length != 32 || proof.Length != 32)
        {
            throw new AuthenticationException("Peer hello has an invalid field length.");
        }

        byte[] certificate = [];
        byte[] challenge;
        if (expectedRole == "remote")
        {
            certificate = Convert.FromBase64String(hello.Certificate);
            if (certificate.Length is < 64 or > 16 * 1024)
            {
                throw new AuthenticationException("Peer certificate has an invalid size.");
            }

            var certificateHash = SHA256.HashData(certificate);
            challenge = new byte[64];
            nonce.CopyTo(challenge, 0);
            certificateHash.CopyTo(challenge, nonce.Length);
            CryptographicOperations.ZeroMemory(certificateHash);
        }
        else
        {
            if (hello.Certificate.Length != 0 || remoteNonce is null || remoteNonce.Length != 32)
            {
                throw new AuthenticationException("Local peer hello is not bound to the current remote challenge.");
            }

            challenge = new byte[64];
            remoteNonce.CopyTo(challenge, 0);
            nonce.CopyTo(challenge, remoteNonce.Length);
        }

        try
        {
            if (!OneTimeInvitationFactory.VerifyApprovalProof(
                oneTimeSecret,
                sessionId,
                $"{expectedRole}:{hello.Identity}",
                hello.Permissions,
                challenge,
                proof))
            {
                throw new AuthenticationException("Peer authentication failed.");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(nonce);
            if (certificate.Length > 0)
            {
                CryptographicOperations.ZeroMemory(certificate);
            }
            CryptographicOperations.ZeroMemory(proof);
            CryptographicOperations.ZeroMemory(challenge);
        }
    }

    private static async Task SendHelloAsync(WebSocket socket, SecureHello hello, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(hello, JsonOptions);
        if (bytes.Length > 24 * 1024)
        {
            throw new InvalidDataException("Peer hello exceeds its size limit.");
        }

        await socket.SendAsync(bytes, WebSocketMessageType.Binary, endOfMessage: true, cancellationToken).ConfigureAwait(false);
        CryptographicOperations.ZeroMemory(bytes);
    }

    private static async Task RunBoundedPhaseAsync(
        string phase,
        TimeSpan timeout,
        CancellationToken callerToken,
        Func<CancellationToken, Task> operation)
    {
        await RunBoundedPhaseAsync<object?>(
            phase,
            timeout,
            callerToken,
            async token =>
            {
                await operation(token).ConfigureAwait(false);
                return null;
            }).ConfigureAwait(false);
    }

    private static async Task<T> RunBoundedPhaseAsync<T>(
        string phase,
        TimeSpan timeout,
        CancellationToken callerToken,
        Func<CancellationToken, Task<T>> operation,
        Func<TimeoutException, Exception>? timeoutExceptionFactory = null)
    {
        using var phaseCancellation = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
        var operationTask = operation(phaseCancellation.Token);
        var timeoutTask = Task.Delay(timeout);
        var callerCancellation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var callerRegistration = callerToken.Register(
            static state => ((TaskCompletionSource<bool>)state!).TrySetResult(true),
            callerCancellation);
        var callerCancellationTask = callerCancellation.Task;
        var completedTask = await Task.WhenAny(operationTask, timeoutTask, callerCancellationTask).ConfigureAwait(false);

        if (callerToken.IsCancellationRequested || completedTask == callerCancellationTask)
        {
            CancelAndObserve(phaseCancellation, operationTask);
            throw new OperationCanceledException(callerToken);
        }

        if (completedTask == operationTask)
        {
            var result = await operationTask.ConfigureAwait(false);
            callerToken.ThrowIfCancellationRequested();
            return result;
        }

        CancelAndObserve(phaseCancellation, operationTask);
        if (callerToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(callerToken);
        }

        var timeoutException = new TimeoutException($"Timed out during {phase} after {timeout.TotalSeconds:0} seconds.");
        throw timeoutExceptionFactory?.Invoke(timeoutException) ?? timeoutException;
    }

    private static void CancelAndObserve(CancellationTokenSource cancellation, Task operationTask)
    {
        try
        {
            cancellation.Cancel();
        }
        catch
        {
            // Keep the original timeout/cancellation result when a callback fails.
        }

        if (!operationTask.IsCompleted)
        {
            _ = operationTask.ContinueWith(
                static task => _ = task.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    private static async Task AbortAndDisposeFailedHandshakeAsync(WebSocket socket, SslStream? sslStream)
    {
        if (sslStream is not null)
        {
            await RunBoundedCleanupAsync(async () => await sslStream.DisposeAsync().ConfigureAwait(false)).ConfigureAwait(false);
        }

        await CloseWebSocketAsync(socket, WebSocketCloseStatus.PolicyViolation, "handshake_failed").ConfigureAwait(false);
    }

    private static async Task CloseWebSocketAsync(
        WebSocket socket,
        WebSocketCloseStatus closeStatus,
        string closeDescription)
    {
        try
        {
            using var timeout = new CancellationTokenSource(CleanupTimeout);
            if (socket.State == WebSocketState.Open)
            {
                await socket.CloseOutputAsync(closeStatus, closeDescription, timeout.Token).ConfigureAwait(false);
            }
            else if (socket.State == WebSocketState.CloseReceived)
            {
                await socket.CloseOutputAsync(
                    socket.CloseStatus ?? closeStatus,
                    socket.CloseStatusDescription ?? closeDescription,
                    timeout.Token).ConfigureAwait(false);
            }

            var buffer = new byte[8 * 1024];
            var drainedBytes = 0;
            while (socket.State == WebSocketState.CloseSent)
            {
                var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), timeout.Token).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }

                drainedBytes += result.Count;
                if (drainedBytes > 1024 * 1024)
                {
                    throw new InvalidDataException("Too much data arrived while completing the WebSocket close handshake.");
                }
            }
        }
        catch
        {
            try
            {
                socket.Abort();
            }
            catch
            {
                // Continue disposing an already-failed transport.
            }
        }
        finally
        {
            try
            {
                socket.Dispose();
            }
            catch
            {
                // Disposal is best effort after a bounded close attempt.
            }
        }
    }

    private static async Task RunBoundedCleanupAsync(Func<Task> cleanup)
    {
        Task? cleanupTask = null;
        try
        {
            cleanupTask = Task.Run(cleanup);
            await cleanupTask.WaitAsync(CleanupTimeout).ConfigureAwait(false);
        }
        catch
        {
            if (cleanupTask is not null && !cleanupTask.IsCompleted)
            {
                _ = cleanupTask.ContinueWith(
                    static task => _ = task.Exception,
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }
    }

    private static async Task<SecureHello> ReceiveHelloAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var scratch = new byte[8 * 1024];
        while (true)
        {
            var result = await socket.ReceiveAsync(scratch.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (result.MessageType != WebSocketMessageType.Binary)
            {
                throw new AuthenticationException("Expected a binary peer authentication message.");
            }

            if (buffer.Length + result.Count > 24 * 1024)
            {
                throw new InvalidDataException("Peer hello exceeds its size limit.");
            }

            buffer.Write(scratch, 0, result.Count);
            if (result.EndOfMessage)
            {
                return JsonSerializer.Deserialize<SecureHello>(buffer.ToArray(), JsonOptions)
                    ?? throw new AuthenticationException("Peer hello was empty.");
            }
        }
    }

    private static X509Certificate2 CreateEphemeralCertificate()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=RemoteCli ephemeral session", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
        var pkcs12 = generated.Export(X509ContentType.Pkcs12);
        try
        {
            // Windows Schannel requires a named user key for SslStream certificate credentials.
            // Omitting PersistKeySet lets the key be removed when this certificate is disposed.
            return X509CertificateLoader.LoadPkcs12(pkcs12, password: null, X509KeyStorageFlags.UserKeySet);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pkcs12);
        }
    }

    private static byte[] HashCertificate(string base64Certificate)
    {
        var bytes = Convert.FromBase64String(base64Certificate);
        try
        {
            return SHA256.HashData(bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static bool CertificateMatches(X509Certificate? certificate, byte[] expectedHash)
    {
        if (certificate is null)
        {
            return false;
        }

        var raw = certificate.GetRawCertData();
        var actualHash = SHA256.HashData(raw);
        try
        {
            return CryptographicOperations.FixedTimeEquals(actualHash, expectedHash);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(raw);
            CryptographicOperations.ZeroMemory(actualHash);
        }
    }

    private static void ValidateIdentity(string identity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identity);
        if (identity.Length > 128 || identity.Any(char.IsControl))
        {
            throw new ArgumentException("Peer identity is too long or contains control characters.", nameof(identity));
        }
    }

    private sealed record SecureHello(
        int ProtocolVersion,
        Guid SessionId,
        string Role,
        string Identity,
        long ExpiresAtUnixSeconds,
        RemotePermission Permissions,
        string Nonce,
        string Certificate,
        string Proof);
}
