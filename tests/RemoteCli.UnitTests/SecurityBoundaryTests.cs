using System.Text;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using Common.Protocol;
using Common.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RemoteCli.Vps;
using RemoteCli.Local;

namespace RemoteCli.UnitTests;

public sealed class SecurityBoundaryTests
{
    [Fact]
    public void InvitationSecretHas256BitsAndExpiresByDefaultInTenMinutes()
    {
        var now = DateTimeOffset.Parse("2026-10-25T00:00:00Z");
        var invitation = OneTimeInvitationFactory.Create(
            "remote-host",
            RemotePermission.SessionRead | RemotePermission.CommandExecute,
            now: now);

        Assert.Equal(43, invitation.OneTimeSecret.Length);
        Assert.Equal(now.AddMinutes(10), invitation.ExpiresAt);
        Assert.Equal(32, Convert.FromBase64String(invitation.OneTimeSecret.Replace('-', '+').Replace('_', '/') + "=").Length);
    }

    [Fact]
    public void InvitationSecretCanOnlyBeConsumedOnceAndLocksAfterFiveFailures()
    {
        var now = DateTimeOffset.UtcNow;
        var invitation = OneTimeInvitationFactory.Create("remote-host", RemotePermission.SessionRead, now: now);
        var gate = new InvitationAttemptGate(invitation.OneTimeSecret, invitation.ExpiresAt);

        for (var attempt = 0; attempt < 5; attempt++)
        {
            Assert.False(gate.TryConsume("not-a-valid-secret", now));
        }

        Assert.Equal(5, gate.FailedAttempts);
        Assert.False(gate.TryConsume(invitation.OneTimeSecret, now));

        var freshGate = new InvitationAttemptGate(invitation.OneTimeSecret, invitation.ExpiresAt);
        Assert.True(freshGate.TryConsume(invitation.OneTimeSecret, now));
        Assert.True(freshGate.IsConsumed);
        Assert.False(freshGate.TryConsume(invitation.OneTimeSecret, now));
    }

    [Fact]
    public void ApprovalProofBindsChallengeSessionIdentityAndPermissions()
    {
        var invitation = OneTimeInvitationFactory.Create("remote-host", RemotePermission.SessionRead | RemotePermission.CommandExecute);
        var challenge = Encoding.UTF8.GetBytes("fresh-random-challenge-value");
        var proof = OneTimeInvitationFactory.CreateApprovalProof(
            invitation.OneTimeSecret,
            invitation.SessionId,
            "local-operator",
            RemotePermission.SessionRead,
            challenge);

        Assert.True(OneTimeInvitationFactory.VerifyApprovalProof(
            invitation.OneTimeSecret,
            invitation.SessionId,
            "local-operator",
            RemotePermission.SessionRead,
            challenge,
            proof));
        Assert.False(OneTimeInvitationFactory.VerifyApprovalProof(
            invitation.OneTimeSecret,
            invitation.SessionId,
            "different-operator",
            RemotePermission.SessionRead,
            challenge,
            proof));
        Assert.False(OneTimeInvitationFactory.VerifyApprovalProof(
            invitation.OneTimeSecret,
            Guid.NewGuid(),
            "local-operator",
            RemotePermission.SessionRead,
            challenge,
            proof));
    }

    [Fact]
    public void ApprovedPermissionsAreTheIntersectionOfRequestedAndApproved()
    {
        var result = OneTimeInvitationFactory.RestrictPermissions(
            RemotePermission.SessionRead | RemotePermission.CommandExecute,
            RemotePermission.CommandExecute | RemotePermission.CommandCancel);

        Assert.Equal(RemotePermission.CommandExecute, result);
    }

    [Fact]
    public void RemoteAuthorizationRejectsWrongSessionMissingPermissionAndReplayedSequence()
    {
        var sessionId = Guid.NewGuid();
        var authorizer = new SessionCommandAuthorizer(
            sessionId,
            RemotePermission.CommandExecute,
            DateTimeOffset.UtcNow.AddMinutes(5));
        var now = DateTimeOffset.UtcNow;
        var first = Request(sessionId, Guid.NewGuid(), 1);

        Assert.True(authorizer.TryAuthorizeCommand(first, now, out var firstError));
        Assert.Equal(string.Empty, firstError);
        Assert.False(authorizer.TryAuthorizeCommand(first, now, out var replayError));
        Assert.Equal("replay_or_invalid_sequence", replayError);

        var next = Request(Guid.NewGuid(), Guid.NewGuid(), 2);
        Assert.False(authorizer.TryAuthorizeCommand(next, now, out var sessionError));
        Assert.Equal("session_mismatch", sessionError);

        var noExecute = new SessionCommandAuthorizer(Guid.NewGuid(), RemotePermission.SessionRead, now.AddMinutes(1));
        Assert.False(noExecute.TryAuthorizeCommand(Request(Guid.NewGuid(), Guid.NewGuid(), 1), now, out var permissionError));
        Assert.Equal("permission_denied", permissionError);
    }

    [Fact]
    public void ClosedOrExpiredSessionsCannotRunCommands()
    {
        var sessionId = Guid.NewGuid();
        var expired = new SessionCommandAuthorizer(sessionId, RemotePermission.CommandExecute, DateTimeOffset.UtcNow.AddSeconds(-1));
        Assert.False(expired.TryAuthorizeCommand(Request(sessionId, Guid.NewGuid(), 1), DateTimeOffset.UtcNow, out var expiredError));
        Assert.Equal("session_expired", expiredError);

        var closed = new SessionCommandAuthorizer(sessionId, RemotePermission.CommandExecute, DateTimeOffset.UtcNow.AddMinutes(1));
        closed.Close();
        Assert.False(closed.TryAuthorizeCommand(Request(sessionId, Guid.NewGuid(), 1), DateTimeOffset.UtcNow, out var closedError));
        Assert.Equal("session_closed", closedError);
    }

    [Fact]
    public void ProtocolFramesRejectUnknownTypesVersionsAndOversizePayloads()
    {
        var sessionId = Guid.NewGuid();
        var unknownType = Encoding.UTF8.GetBytes($"{{\"protocolVersion\":1,\"type\":\"local.exec\",\"sessionId\":\"{sessionId:D}\",\"sequence\":1,\"payload\":{{}}}}");
        Assert.Throws<InvalidDataException>(() => ProtocolJson.DeserializeEnvelope(unknownType));

        var unknownVersion = Encoding.UTF8.GetBytes($"{{\"protocolVersion\":99,\"type\":\"session.status\",\"sessionId\":\"{sessionId:D}\",\"sequence\":1,\"payload\":{{}}}}");
        Assert.Throws<InvalidDataException>(() => ProtocolJson.DeserializeEnvelope(unknownVersion));

        Assert.Throws<InvalidDataException>(() => ProtocolJson.DeserializeEnvelope(new byte[ProtocolConstants.MaximumFrameBytes + 1]));
    }

    [Fact]
    public async Task LengthPrefixedFramesRoundTripAndRejectOversizeHeader()
    {
        var request = Request(Guid.NewGuid(), Guid.NewGuid(), 1);
        using var stream = new MemoryStream();
        using var writer = new SemaphoreSlim(1, 1);
        await FramedJsonStream.WriteAsync(stream, request, writer, CancellationToken.None);
        stream.Position = 0;

        var decoded = await FramedJsonStream.ReadAsync<RemoteCommandRequest>(stream, CancellationToken.None);
        Assert.Equal(request, decoded);

        var invalidHeader = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(invalidHeader, ProtocolConstants.MaximumFrameBytes + 1);
        using var invalidStream = new MemoryStream(invalidHeader);
        await Assert.ThrowsAsync<InvalidDataException>(() => FramedJsonStream.ReadAsync<RemoteCommandRequest>(invalidStream, CancellationToken.None));
    }

    [Fact]
    public void RemoteCommandAuthorizationSupportsLongSessionsAndStillRejectsReplayedSequences()
    {
        var sessionId = Guid.NewGuid();
        var authorizer = new SessionCommandAuthorizer(
            sessionId,
            RemotePermission.CommandExecute,
            DateTimeOffset.UtcNow.AddMinutes(5));
        var now = DateTimeOffset.UtcNow;

        for (var sequence = 1; sequence <= 4200; sequence++)
        {
            Assert.True(authorizer.TryAuthorizeCommand(Request(sessionId, Guid.NewGuid(), sequence), now, out var error), error);
        }

        Assert.False(authorizer.TryAuthorizeCommand(Request(sessionId, Guid.NewGuid(), 1), now, out var replayError));
        Assert.Equal("replay_or_invalid_sequence", replayError);
        authorizer.UpdatePermissions(RemotePermission.None);
        Assert.False(authorizer.TryAuthorizeCommand(Request(sessionId, Guid.NewGuid(), 4201), now, out var denied));
        Assert.Equal("permission_denied", denied);
        authorizer.UpdatePermissions(RemotePermission.CommandExecute);
        Assert.True(authorizer.TryAuthorizeCommand(Request(sessionId, Guid.NewGuid(), 4202), now, out _));
    }

    [Fact]
    public async Task ConversationStoreIsOptInAndDeduplicatesMessageIds()
    {
        var installDirectory = Path.Combine(Path.GetTempPath(), "RemoteCli.UnitTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(installDirectory);
        try
        {
            var disabledStore = new ConversationStore(installDirectory, enabled: false);
            await Assert.ThrowsAsync<InvalidOperationException>(() => disabledStore.SyncAsync(
                "conversation-1", "test-client", "client-instance-1", true, false,
                [Message("message-1", "first")], CancellationToken.None));

            var store = new ConversationStore(installDirectory, enabled: true);
            var firstCount = await store.SyncAsync(
                "conversation-1", "test-client", "client-instance-1", true, false,
                [Message("message-1", "one-time password: abc123; {\"oneTimeSecret\":\"token-value\",\"proof\":\"mac-value\"}")], CancellationToken.None);
            var retryCount = await store.SyncAsync(
                "conversation-1", "test-client", "client-instance-1", true, false,
                [Message("message-1", "duplicate should not be stored")], CancellationToken.None);

            Assert.Equal(1, firstCount);
            Assert.Equal(0, retryCount);
            var status = Assert.Single(await store.ListAsync(CancellationToken.None));
            Assert.Equal("FullReported", status.Mode);
            Assert.False(status.IsPossiblyIncomplete);

            var entries = await store.ReadAsync("conversation-1", CancellationToken.None);
            var storedMessage = Assert.Single(entries, entry => entry.Kind == "message");
            Assert.Equal("one-time password: [REDACTED]; {\"oneTimeSecret\":\"[REDACTED]\",\"proof\":\"[REDACTED]\"}", storedMessage.Content);
        }
        finally
        {
            Directory.Delete(installDirectory, recursive: true);
        }
    }

    [Theory]
    [InlineData(ConnectionCredentialMode.RotatingRandomPassword)]
    [InlineData(ConnectionCredentialMode.FixedPassword)]
    public async Task SecurePeerChannelAuthenticatesBothEndpointsAndEncryptsThroughBlindRelay(ConnectionCredentialMode mode)
    {
        const string userName = "test.user";
        const string fixedPassword = "  固定密码 !@#$%^&*()  ";
        var invitation = OneTimeInvitationFactory.Create(
            "remote-host",
            RemotePermission.SessionRead | RemotePermission.CommandExecute | RemotePermission.CommandCancel);
        if (mode == ConnectionCredentialMode.FixedPassword)
        {
            invitation = invitation with
            {
                OneTimeSecret = ConnectionAuthorizationCredential.DeriveFixedSecret(userName, fixedPassword)
            };
        }

        var password = mode == ConnectionCredentialMode.FixedPassword
            ? fixedPassword
            : " \r\n" + invitation.OneTimeSecret + "\t ";
        var invitationRequest = new InvitationRequest(
            ProtocolConstants.CurrentVersion,
            invitation.SessionId,
            invitation.RemoteIdentity,
            invitation.ExpiresAt,
            invitation.RequestedPermissions,
            userName,
            mode,
            invitation.ExpiresAt);
        var relayedFrames = new ConcurrentQueue<byte[]>();
        var relay = new RelayRoomService(frame => relayedFrames.Enqueue(frame.ToArray()));
        Assert.True(relay.TryRegister(invitationRequest, out var registerError), registerError);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(relay);
        await using var app = builder.Build();
        app.UseWebSockets();
        app.MapGet("/v1/authorizations/{userName}", () => Results.Json(invitationRequest, ProtocolJson.Options));
        app.MapGet("/v1/relay/{sessionId:guid}", async (HttpContext context, Guid sessionId, RelayRoomService relayService) =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            var socket = await context.WebSockets.AcceptWebSocketAsync();
            await using var socketOwner = new AsyncSocketOwner(socket);
            await relayService.JoinAsync(sessionId, context.Request.Query["role"].ToString(), socket, context.RequestAborted);
        });
        await app.StartAsync(cancellation.Token);
        var server = app.Services.GetRequiredService<IServer>();
        var address = server.Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        var remoteSocketUri = new UriBuilder(address)
        {
            Scheme = "ws",
            Path = $"/v1/relay/{invitation.SessionId:D}",
            Query = "role=remote"
        }.Uri;
        var privateCommand = "private-command-content-never-on-relay"u8.ToArray();
        var privateResult = "private-result-content-never-on-relay"u8.ToArray();

        using var remoteSocket = new ClientWebSocket();
        await remoteSocket.ConnectAsync(remoteSocketUri, cancellation.Token);
        var remoteTask = Task.Run(async () =>
        {
            await using var remoteChannel = await SecurePeerChannel.AcceptRemoteAsync(
                remoteSocket,
                invitation.SessionId,
                invitation.OneTimeSecret,
                invitation.RemoteIdentity,
                invitation.ExpiresAt,
                invitation.RequestedPermissions,
                cancellation.Token);
            var input = new byte[privateCommand.Length];
            await remoteChannel.Stream.ReadExactlyAsync(input, cancellation.Token);
            Assert.Equal(Encoding.UTF8.GetString(privateCommand), Encoding.UTF8.GetString(input));
            await remoteChannel.Stream.WriteAsync(privateResult, cancellation.Token);
            return (remoteChannel.RemoteIdentity, remoteChannel.ApprovedPermissions);
        }, cancellation.Token);

        using var httpClient = new HttpClient();
        using var connection = await RelayAuthorizationClient.ConnectLocalAsync(
            httpClient, new Uri(address + "/"), userName, password, cancellation.Token);
        var clientSocket = connection.Socket;

        SecurePeerChannel localChannel;
        try
        {
            localChannel = await SecurePeerChannel.ConnectLocalAsync(
                clientSocket,
                invitation.SessionId,
                connection.Secret,
                "local-operator",
                async (announcement, approvalToken) =>
                {
                    Assert.Equal(invitation.RemoteIdentity, announcement.RemoteIdentity);
                    Assert.Equal(invitation.RequestedPermissions, announcement.RequestedPermissions);
                    await Task.Delay(TimeSpan.FromSeconds(11), approvalToken);
                    return RemotePermission.SessionRead | RemotePermission.CommandExecute;
                },
                cancellation.Token);
        }
        catch when (remoteTask.IsFaulted)
        {
            throw new InvalidOperationException("Remote peer channel failed.", remoteTask.Exception);
        }

        await using var localChannelScope = localChannel;

        await localChannel.Stream.WriteAsync(privateCommand, cancellation.Token);
        var response = new byte[privateResult.Length];
        await localChannel.Stream.ReadExactlyAsync(response, cancellation.Token);
        Assert.Equal(Encoding.UTF8.GetString(privateResult), Encoding.UTF8.GetString(response));
        Assert.Equal("remote-host", localChannel.RemoteIdentity);
        Assert.Equal(RemotePermission.SessionRead | RemotePermission.CommandExecute, localChannel.ApprovedPermissions);

        await localChannelScope.DisposeAsync();
        var remoteResult = await remoteTask.WaitAsync(cancellation.Token);
        Assert.Equal("local-operator", remoteResult.RemoteIdentity);
        Assert.Equal(localChannel.ApprovedPermissions, remoteResult.ApprovedPermissions);
        Assert.Equal(WebSocketState.Closed, clientSocket.State);
        Assert.Equal(WebSocketState.Closed, remoteSocket.State);
        Assert.Equal(WebSocketCloseStatus.NormalClosure, clientSocket.CloseStatus);
        Assert.Equal(WebSocketCloseStatus.NormalClosure, remoteSocket.CloseStatus);
        Assert.NotEmpty(relayedFrames);
        Assert.DoesNotContain(relayedFrames, frame => frame.AsSpan().IndexOf(privateCommand) >= 0);
        Assert.DoesNotContain(relayedFrames, frame => frame.AsSpan().IndexOf(privateResult) >= 0);
        await app.StopAsync(cancellation.Token);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SecurePeerChannel_StopsWaitingForPermissionApprovalWhenInvitationExpires(bool approvalProviderFails)
    {
        var relay = new RelayRoomService();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(relay);
        await using var app = builder.Build();
        app.UseWebSockets();
        app.MapGet("/relay/{sessionId:guid}", async (HttpContext context, Guid sessionId, RelayRoomService relayService) =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            var socket = await context.WebSockets.AcceptWebSocketAsync();
            await using var socketOwner = new AsyncSocketOwner(socket);
            await relayService.JoinAsync(sessionId, context.Request.Query["role"].ToString(), socket, context.RequestAborted);
        });
        await app.StartAsync(cancellation.Token);

        var invitation = OneTimeInvitationFactory.Create(
            "remote-host",
            RemotePermission.SessionRead | RemotePermission.CommandExecute,
            lifetime: TimeSpan.FromSeconds(5));
        var invitationRequest = new InvitationRequest(
            ProtocolConstants.CurrentVersion,
            invitation.SessionId,
            invitation.RemoteIdentity,
            invitation.ExpiresAt,
            invitation.RequestedPermissions);
        Assert.True(relay.TryRegister(invitationRequest, out var registerError), registerError);

        var server = app.Services.GetRequiredService<IServer>();
        var address = server.Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        var remoteSocketUri = new UriBuilder(address)
        {
            Scheme = "ws",
            Path = $"/relay/{invitation.SessionId:D}",
            Query = "role=remote"
        }.Uri;
        var localSocketUri = new UriBuilder(remoteSocketUri) { Query = "role=local" }.Uri;

        using var remoteSocket = new ClientWebSocket();
        await remoteSocket.ConnectAsync(remoteSocketUri, cancellation.Token);
        var remoteTask = Task.Run(async () =>
        {
            await using var remoteChannel = await SecurePeerChannel.AcceptRemoteAsync(
                remoteSocket,
                invitation.SessionId,
                invitation.OneTimeSecret,
                invitation.RemoteIdentity,
                invitation.ExpiresAt,
                invitation.RequestedPermissions,
                cancellation.Token);
        }, cancellation.Token);

        using var localSocket = new ClientWebSocket();
        await localSocket.ConnectAsync(localSocketUri, cancellation.Token);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var providerFailure = new TimeoutException("Approval provider failed before the invitation expired.");
        var connectTask = SecurePeerChannel.ConnectLocalAsync(
                localSocket,
                invitation.SessionId,
                invitation.OneTimeSecret,
                "local-operator",
                async (_, token) =>
                {
                    if (approvalProviderFails)
                    {
                        throw providerFailure;
                    }

                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    return RemotePermission.None;
                },
                cancellation.Token);
        if (approvalProviderFails)
        {
            var exception = await Assert.ThrowsAsync<TimeoutException>(() => connectTask.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Same(providerFailure, exception);
        }
        else
        {
            var exception = await Assert.ThrowsAsync<System.Security.Authentication.AuthenticationException>(
                () => connectTask.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Contains("expired", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        stopwatch.Stop();

        if (!approvalProviderFails)
        {
            Assert.InRange(stopwatch.Elapsed, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(10));
        }
        try
        {
            await remoteTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception remoteFailure) when (remoteFailure is IOException or OperationCanceledException or TimeoutException or WebSocketException or ObjectDisposedException or System.Security.Authentication.AuthenticationException)
        {
            // The local endpoint aborts the transport after the invitation expires.
        }

        await app.StopAsync(cancellation.Token);
    }

    private sealed class AsyncSocketOwner(WebSocket socket) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            socket.Abort();
            socket.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private static RemoteCommandRequest Request(Guid sessionId, Guid commandId, long sequence) => new(
        ProtocolConstants.CurrentVersion,
        sessionId,
        commandId,
        sequence,
        "Write-Output 'remote-only'",
        null,
        60);

    private static ConversationMessageInput Message(string messageId, string content) => new(
        messageId,
        "assistant",
        DateTimeOffset.UtcNow,
        0,
        content,
        null,
        null);
}
