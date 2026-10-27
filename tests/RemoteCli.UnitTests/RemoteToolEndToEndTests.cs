using System.Net.WebSockets;
using System.Text.Json;
using Common.Protocol;
using Common.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RemoteCli.Local;
using RemoteCli.Remote;
using RemoteCli.Vps;

namespace RemoteCli.UnitTests;

public sealed class RemoteToolEndToEndTests
{
    private static readonly RemotePermission AllToolPermissions =
        RemotePermission.SessionRead | RemotePermission.CommandExecute | RemotePermission.CommandCancel;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemoteToolListCallReceiveAndCancelUseAuthenticatedRemoteSession(bool blockSynchronously)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = cancellation.Token;
        var invitation = CreateInvitation();
        var (app, remoteSocketUri, localSocketUri) = await StartRelayAsync(invitation.SessionId, token);
        var relay = app.Services.GetRequiredService<RelayRoomService>();
        Assert.True(relay.TryRegister(new InvitationRequest(
            ProtocolConstants.CurrentVersion,
            invitation.SessionId,
            invitation.RemoteIdentity,
            invitation.ExpiresAt,
            invitation.RequestedPermissions), out var registerError), registerError);

        using var remoteSocket = new ClientWebSocket();
        await remoteSocket.ConnectAsync(remoteSocketUri, token);
        var registry = new FakeRemoteToolRegistry(blockSynchronously);
        var remoteTask = Task.Run(async () =>
        {
            await using var remoteChannel = await SecurePeerChannel.AcceptRemoteAsync(
                remoteSocket,
                invitation.SessionId,
                invitation.OneTimeSecret,
                invitation.RemoteIdentity,
                invitation.ExpiresAt,
                invitation.RequestedPermissions,
                token);
            await RemoteCommandSession.RunAsync(remoteChannel, token, registry);
        }, token);

        using var localSocket = new ClientWebSocket();
        await localSocket.ConnectAsync(localSocketUri, token);
        var localChannel = await SecurePeerChannel.ConnectLocalAsync(
            localSocket,
            invitation.SessionId,
            invitation.OneTimeSecret,
            "local-operator",
            (_, _) => Task.FromResult(AllToolPermissions),
            token);
        await using var localChannelOwner = localChannel;

        var brokerPipeName = "RemoteCli.Test." + Guid.NewGuid().ToString("N");
        var broker = new LocalSessionBroker(localChannel, token, brokerPipeName);
        string? brokerFailure = null;
        broker.SessionFaulted += (stage, code) => brokerFailure = stage + "/" + code;
        broker.PipeRequestFaulted += code => brokerFailure = "pipe_request/" + code;
        var brokerTask = broker.RunAsync();
        string FailureDetails() => $"sessionFailure={brokerFailure} broker={brokerTask.Exception?.GetBaseException()} remote={remoteTask.Exception?.GetBaseException()}";
        try
        {
            await Task.Delay(100, token);
            var state = new LocalConnectionStateMachine();
            var started = state.BeginAuthentication();
            Assert.True(state.TrySetPhase(started.Status.AuthRequestId!.Value, LocalConnectionState.AwaitingAuthentication));
            Assert.True(state.TrySetPhase(started.Status.AuthRequestId.Value, LocalConnectionState.Authenticating));
            Assert.True(state.TrySetConnected(
                started.Status.AuthRequestId.Value,
                localChannel.SessionId,
                localChannel.RemoteIdentity,
                "VPS Relay",
                localChannel.ApprovedPermissions,
                localChannel.SessionExpiresAt));
            var coordinator = new LocalConnectionServiceCoordinator(state, new LocalSessionBrokerClient(brokerPipeName));
            var snapshot = state.GetSnapshot();

            var listResponse = await coordinator.HandleAsync(CreateRequest(
                "remote.tools.list", new { }, snapshot), token);
            Assert.True(listResponse.Success, $"{listResponse.Error?.Code} {FailureDetails()}");
            var tools = listResponse.Payload.Deserialize<RemoteToolsListResult>(ProtocolJson.Options)!;
            Assert.Contains(tools.Tools, tool => tool.Name == "test.remote_echo");

            var callResponse = await coordinator.HandleAsync(CreateRequest(
                "remote.tool.call",
                new LocalRemoteToolCallInput("test.remote_echo", JsonSerializer.SerializeToElement(new { value = "hello" }, ProtocolJson.Options)),
                snapshot), token);
            Assert.True(callResponse.Success, $"{callResponse.Error?.Code} {FailureDetails()}");
            var call = callResponse.Payload.Deserialize<LocalRemoteToolCallResult>(ProtocolJson.Options)!;
            Assert.NotEqual(Guid.Empty, call.OperationId);

            var receiveResponse = await coordinator.HandleAsync(CreateRequest(
                "remote.tool.receive",
                new LocalRemoteToolReceiveInput(call.OperationId, 0, 5_000),
                snapshot), token);
            Assert.True(receiveResponse.Success, $"error={receiveResponse.Error?.Code} message={receiveResponse.Error?.Message} {FailureDetails()}");
            var received = receiveResponse.Payload.Deserialize<LocalRemoteToolReceiveResult>(ProtocolJson.Options)!;
            Assert.True(received.Done, $"state={received.State} error={received.Error?.Code} message={received.Error?.Message} lists={registry.ListCallCount} executions={registry.ExecutionCount}");
            Assert.Equal(RemoteToolOperationState.Succeeded, received.State);
            Assert.Equal("hello-remote-executed", received.Result!.Value.GetProperty("result").GetString());

            var invalidCallResponse = await coordinator.HandleAsync(CreateRequest(
                "remote.tool.call",
                new LocalRemoteToolCallInput("test.remote_echo", JsonSerializer.SerializeToElement(new { value = 42 }, ProtocolJson.Options)),
                snapshot), token);
            Assert.True(invalidCallResponse.Success, $"{invalidCallResponse.Error?.Code} {FailureDetails()}");
            var invalidCall = invalidCallResponse.Payload.Deserialize<LocalRemoteToolCallResult>(ProtocolJson.Options)!;
            var invalidReceiveResponse = await coordinator.HandleAsync(CreateRequest(
                "remote.tool.receive",
                new LocalRemoteToolReceiveInput(invalidCall.OperationId, 0, 5_000),
                snapshot), token);
            var invalidReceive = invalidReceiveResponse.Payload.Deserialize<LocalRemoteToolReceiveResult>(ProtocolJson.Options)!;
            Assert.True(invalidReceive.Done, $"state={invalidReceive.State} error={invalidReceive.Error?.Code} lists={registry.ListCallCount} executions={registry.ExecutionCount} {FailureDetails()}");
            Assert.Equal("invalid_tool_arguments", invalidReceive.Error?.Code);

            var badOutputCallResponse = await coordinator.HandleAsync(CreateRequest(
                "remote.tool.call",
                new LocalRemoteToolCallInput("test.bad_output", JsonSerializer.SerializeToElement(new { }, ProtocolJson.Options)),
                snapshot), token);
            Assert.True(badOutputCallResponse.Success, $"error={badOutputCallResponse.Error?.Code} message={badOutputCallResponse.Error?.Message} {FailureDetails()}");
            var badOutputCall = badOutputCallResponse.Payload.Deserialize<LocalRemoteToolCallResult>(ProtocolJson.Options)!;
            var badOutputReceiveResponse = await coordinator.HandleAsync(CreateRequest(
                "remote.tool.receive",
                new LocalRemoteToolReceiveInput(badOutputCall.OperationId, 0, 5_000),
                snapshot), token);
            Assert.True(badOutputReceiveResponse.Success, $"{badOutputReceiveResponse.Error?.Code} {FailureDetails()}");
            var badOutputReceive = badOutputReceiveResponse.Payload.Deserialize<LocalRemoteToolReceiveResult>(ProtocolJson.Options)!;
            Assert.True(badOutputReceive.Done);
            Assert.Equal(RemoteToolOperationState.Failed, badOutputReceive.State);
            Assert.Equal("tool_result_schema_mismatch", badOutputReceive.Error?.Code);

            var waitResponse = await coordinator.HandleAsync(CreateRequest(
                "remote.tool.call",
                new LocalRemoteToolCallInput("test.remote_wait", JsonSerializer.SerializeToElement(new { }, ProtocolJson.Options)),
                snapshot), token);
            Assert.True(waitResponse.Success, $"{waitResponse.Error?.Code} {FailureDetails()}");
            var waitCall = waitResponse.Payload.Deserialize<LocalRemoteToolCallResult>(ProtocolJson.Options)!;
            Assert.NotEqual(Guid.Empty, waitCall.OperationId);
            await registry.WaitStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
            var cancelResponse = await coordinator.HandleAsync(CreateRequest(
                "remote.tool.cancel",
                new LocalRemoteToolCancelInput(waitCall.OperationId),
                snapshot), token);
            Assert.True(cancelResponse.Success, $"{cancelResponse.Error?.Code} {FailureDetails()}");
            await registry.WaitCancelled.Task.WaitAsync(TimeSpan.FromSeconds(3), token);
            var cancelledResponse = await coordinator.HandleAsync(CreateRequest(
                "remote.tool.receive",
                new LocalRemoteToolReceiveInput(waitCall.OperationId, 0, 5_000),
                snapshot), token);
            var cancelled = cancelledResponse.Payload.Deserialize<LocalRemoteToolReceiveResult>(ProtocolJson.Options)!;
            Assert.True(cancelled.Done, $"state={cancelled.State} error={cancelled.Error?.Code} lists={registry.ListCallCount} executions={registry.ExecutionCount} {FailureDetails()}");
            Assert.Equal(RemoteToolOperationState.Cancelled, cancelled.State);
        }
        finally
        {
            cancellation.Cancel();
            await broker.DisposeAsync();
            try
            {
                await brokerTask.WaitAsync(TimeSpan.FromSeconds(3));
            }
            catch (Exception exception) when (exception is OperationCanceledException or TimeoutException or IOException or ObjectDisposedException)
            {
            }

            try
            {
                await remoteTask.WaitAsync(TimeSpan.FromSeconds(3));
            }
            catch (Exception exception) when (exception is OperationCanceledException or TimeoutException or IOException or ObjectDisposedException)
            {
            }

            await app.StopAsync(CancellationToken.None);
            await app.DisposeAsync();
        }
    }

    [Fact]
    public void RemoteToolAuthorizerRejectsPermissionReplayAndOutOfOrderSequence()
    {
        var sessionId = Guid.NewGuid();
        var registry = new FakeRemoteToolRegistry();
        var authorized = new RemoteToolCallAuthorizer(sessionId, RemotePermission.CommandExecute, DateTimeOffset.UtcNow.AddMinutes(5));
        var operationId = Guid.NewGuid();
        var first = CreateToolRequest(sessionId, operationId, 1);
        Assert.True(authorized.TryAuthorize(first, registry, DateTimeOffset.UtcNow, out _));
        Assert.False(authorized.TryAuthorize(CreateToolRequest(sessionId, operationId, 2), registry, DateTimeOffset.UtcNow, out var replayError));
        Assert.Equal("replay_or_invalid_sequence", replayError);
        Assert.False(authorized.TryAuthorize(CreateToolRequest(sessionId, Guid.NewGuid(), 3), registry, DateTimeOffset.UtcNow, out var sequenceError));
        Assert.Equal("replay_or_invalid_sequence", sequenceError);
        Assert.True(authorized.TryAuthorize(CreateToolRequest(sessionId, Guid.NewGuid(), 2), registry, DateTimeOffset.UtcNow, out _));

        var noPermission = new RemoteToolCallAuthorizer(sessionId, RemotePermission.SessionRead, DateTimeOffset.UtcNow.AddMinutes(5));
        Assert.False(noPermission.TryAuthorize(CreateToolRequest(sessionId, Guid.NewGuid(), 1), registry, DateTimeOffset.UtcNow, out var permissionError));
        Assert.Equal("permission_denied", permissionError);
    }

    [Fact]
    public void RejectedCatalogAttemptDoesNotPoisonLaterToolSequences()
    {
        var session = Guid.NewGuid();
        var registry = new FakeRemoteToolRegistry();
        var authorizer = new RemoteToolCallAuthorizer(session, RemotePermission.CommandExecute, DateTimeOffset.MaxValue);
        var stale = CreateToolRequest(session, Guid.NewGuid(), 1) with { Name = "test.removed_tool" };
        Assert.False(authorizer.TryAuthorize(stale, registry, DateTimeOffset.UtcNow, out var error));
        Assert.Equal("unknown_remote_tool", error);
        Assert.True(authorizer.TryAuthorize(CreateToolRequest(session, Guid.NewGuid(), 2), registry, DateTimeOffset.UtcNow, out _));
    }

    [Fact]
    public async Task ToolListNotificationFailureDoesNotDisableLaterPollingAttempt()
    {
        var attempts = 0;
        var first = await ConnectionMcpAdapter.TryNotifyToolsListChangedAsync(_ =>
        {
            attempts++;
            throw new IOException("simulated transient transport error");
        }, CancellationToken.None);
        var second = await ConnectionMcpAdapter.TryNotifyToolsListChangedAsync(_ =>
        {
            attempts++;
            return Task.CompletedTask;
        }, CancellationToken.None);

        Assert.False(first);
        Assert.True(second);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task BuiltInRemoteSystemInfoMatchesItsBoundedOutputContract()
    {
        var registry = RemoteToolRegistry.Default;
        var descriptor = Assert.Single(registry.ListTools());
        Assert.Equal("remote.system_info", descriptor.Name);
        Assert.True(RemoteToolSchemaValidator.ValidateSchema(descriptor.InputSchema, out var inputSchemaError), inputSchemaError);
        Assert.True(RemoteToolSchemaValidator.ValidateSchema(descriptor.OutputSchema!.Value, out var outputSchemaError), outputSchemaError);

        using var document = JsonDocument.Parse("{}");
        var arguments = document.RootElement.Clone();
        Assert.True(registry.TryValidateCall(descriptor.Name, arguments, out var argumentError), argumentError);
        var result = await registry.ExecuteAsync(descriptor.Name, arguments, CancellationToken.None);

        Assert.True(RemoteToolSchemaValidator.ValidateArguments(descriptor.OutputSchema.Value, result, out var resultError), resultError);
        Assert.Equal(JsonValueKind.Object, result.ValueKind);
        Assert.Equal(5, result.EnumerateObject().Count());
    }

    [Fact]
    public async Task AuthenticatedSessionOutlivesInvitationAndPublishesRemotePolicyChanges()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var token = cancellation.Token;
        var root = Path.Combine(Path.GetTempPath(), "longshell-live-policy-" + Guid.NewGuid().ToString("N"));
        var first = Path.Combine(root, "first"); var second = Path.Combine(root, "second");
        Directory.CreateDirectory(first); Directory.CreateDirectory(second);
        using var access = new RemoteAccessController(AllToolPermissions, first, true);
        var invitation = OneTimeInvitationFactory.Create("policy-test", AllToolPermissions, TimeSpan.FromSeconds(4));
        var (app, remoteUri, localUri) = await StartRelayAsync(invitation.SessionId, token);
        await using var appOwner = app;
        var relay = app.Services.GetRequiredService<RelayRoomService>();
        var owner = ConnectionAuthorizationCredential.CreateOwnerToken();
        var request = new InvitationRequest(ProtocolConstants.CurrentVersion, invitation.SessionId, invitation.RemoteIdentity,
            invitation.ExpiresAt, invitation.RequestedPermissions, "policy-user", ConnectionCredentialMode.RotatingRandomPassword, DateTimeOffset.UtcNow.AddMinutes(5));
        Assert.True(relay.TryRegisterAuthorization(new("policy-user", owner, request), out var registrationError), registrationError);
        using var remoteSocket = new ClientWebSocket(); using var localSocket = new ClientWebSocket();
        await remoteSocket.ConnectAsync(remoteUri, token);
        var remoteTask = Task.Run(async () =>
        {
            await using var channel = await SecurePeerChannel.AcceptRemoteAsync(remoteSocket, invitation.SessionId,
                invitation.OneTimeSecret, invitation.RemoteIdentity, invitation.ExpiresAt, invitation.RequestedPermissions, token);
            var registry = new DynamicRemoteToolRegistry(access, policy => RemoteToolRegistry.CreateForDesktop(policy.WorkspaceRoot, [],
                (_, _) => Task.FromResult(ToolApprovalDecision.AllowForSession), policy.WorkspaceRestricted, policy.AutoApproveTools));
            await RemoteCommandSession.RunAsync(channel, token, registry, access);
        }, token);
        await localSocket.ConnectAsync(localUri, token);
        await using var local = await SecurePeerChannel.ConnectLocalAsync(localSocket, invitation.SessionId, invitation.OneTimeSecret,
            "controller", (_, _) => Task.FromResult(AllToolPermissions), token);
        Assert.Equal(DateTimeOffset.MaxValue, local.SessionExpiresAt);
        var pipe = "RemoteCli.PolicyTest." + Guid.NewGuid().ToString("N");
        await using var broker = new LocalSessionBroker(local, token, pipe);
        var state = new LocalConnectionStateMachine();
        var auth = state.BeginAuthentication().Status.AuthRequestId!.Value;
        state.TrySetPhase(auth, LocalConnectionState.Authenticating);
        Assert.True(state.TrySetConnected(auth, local.SessionId, local.RemoteIdentity, "encrypted relay", AllToolPermissions, local.SessionExpiresAt));
        var observed = new System.Collections.Concurrent.ConcurrentDictionary<long, TaskCompletionSource>();
        Task WaitPolicy(long version) => observed.GetOrAdd(version, _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).Task.WaitAsync(TimeSpan.FromSeconds(5), token);
        broker.PolicyChanged += policy =>
        {
            state.ApplyRemotePolicy(local.SessionId, policy);
            observed.GetOrAdd(policy.Version, _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
        };
        var brokerTask = broker.RunAsync();
        var coordinator = new LocalConnectionServiceCoordinator(state, new LocalSessionBrokerClient(pipe));
        async Task CreateFile(string name)
        {
            var status = state.GetSnapshot();
            var call = await coordinator.HandleAsync(CreateRequest("remote.tool.call",
                new LocalRemoteToolCallInput("remote.file_create", JsonSerializer.SerializeToElement(new { path = name, content = "ok" })), status), token);
            Assert.True(call.Success, call.Error?.Code);
            var operation = call.Payload.Deserialize<LocalRemoteToolCallResult>(ProtocolJson.Options)!;
            var result = await coordinator.HandleAsync(CreateRequest("remote.tool.receive",
                new LocalRemoteToolReceiveInput(operation.OperationId, 0, 5000), status), token);
            Assert.True(result.Success, result.Error?.Code);
            var completed = result.Payload.Deserialize<LocalRemoteToolReceiveResult>(ProtocolJson.Options)!;
            Assert.True(completed.Done, completed.Error?.Code);
            Assert.Equal(RemoteToolOperationState.Succeeded, completed.State);
        }
        try
        {
            await WaitPolicy(1);
            await CreateFile("first.txt");
            access.Update(RemotePermission.SessionRead, first, true);
            await WaitPolicy(2);
            Assert.DoesNotContain(ConnectionMcpAdapter.BuildToolsForStatus(state.GetSnapshot()), tool => tool.Name == "longshell_remote_command_start");
            access.Update(AllToolPermissions, second, true);
            await WaitPolicy(3);
            Assert.Equal(second, state.GetSnapshot().RemoteWorkspace);
            await CreateFile("second.txt");
            Assert.True(File.Exists(Path.Combine(second, "second.txt")));
            Assert.False(File.Exists(Path.Combine(first, "second.txt")));
            access.Update(RemotePermission.None, second, true);
            await WaitPolicy(4);
            Assert.True(state.GetSnapshot().IsUsable(DateTimeOffset.UtcNow));
            access.Update(AllToolPermissions, second, true);
            await WaitPolicy(5);
            access.Update(RemotePermission.CommandExecute | RemotePermission.CommandCancel, second, true);
            await WaitPolicy(6);
            Assert.DoesNotContain(ConnectionMcpAdapter.BuildToolsForStatus(state.GetSnapshot()), tool => tool.Name == "longshell_remote_tools_list");
            await CreateFile("execution-without-view.txt");
            // Cross both the original invitation deadline and an actual encrypted heartbeat round trip.
            await Task.Delay(TimeSpan.FromSeconds(21), token);
            var takeover = request with { SessionId = Guid.NewGuid(), ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(1) };
            Assert.False(relay.TryRegisterAuthorization(new("policy-user", ConnectionAuthorizationCredential.CreateOwnerToken(), takeover), out var takeoverError));
            Assert.Equal("username_in_use", takeoverError);
            await CreateFile("after-heartbeat.txt");
            Assert.False(brokerTask.IsCompleted);
        }
        finally
        {
            cancellation.Cancel(); remoteSocket.Abort(); localSocket.Abort();
            try { await Task.WhenAll(remoteTask, brokerTask).WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception exception) when (exception is OperationCanceledException or IOException or WebSocketException or ObjectDisposedException) { }
            await app.StopAsync(CancellationToken.None);
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<(WebApplication App, Uri RemoteUri, Uri LocalUri)> StartRelayAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        var relay = new RelayRoomService();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(relay);
        var app = builder.Build();
        app.UseWebSockets();
        app.MapGet("/relay/{sessionId:guid}", async (HttpContext context, Guid sessionId, RelayRoomService relayService) =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            var socket = await context.WebSockets.AcceptWebSocketAsync();
            await using var socketOwner = new SocketOwner(socket);
            await relayService.JoinAsync(sessionId, context.Request.Query["role"].ToString(), socket, context.RequestAborted);
        });
        await app.StartAsync(cancellationToken);
        var server = app.Services.GetRequiredService<IServer>();
        var address = server.Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        var remoteUri = new UriBuilder(address) { Scheme = "ws", Path = $"/relay/{sessionId:D}", Query = "role=remote" }.Uri;
        var localUri = new UriBuilder(remoteUri) { Query = "role=local" }.Uri;
        return (app, remoteUri, localUri);
    }

    private static OneTimeInvitation CreateInvitation() => OneTimeInvitationFactory.Create(
        "remote-test-host",
        AllToolPermissions,
        lifetime: TimeSpan.FromMinutes(5));

    private static RemoteToolCallRequest CreateToolRequest(Guid sessionId, Guid operationId, long sequence) => new(
        ProtocolConstants.CurrentVersion,
        sessionId,
        operationId,
        sequence,
        "test.remote_echo",
        JsonSerializer.SerializeToElement(new { value = "ok" }, ProtocolJson.Options));

    private static LocalServiceRequest CreateRequest(string type, object payload, LocalConnectionStatus status) => new(
        LocalIpcConstants.CurrentVersion,
        Guid.NewGuid(),
        type,
        LocalIpcConstants.MaximumRequestTimeoutMilliseconds,
        JsonSerializer.SerializeToElement(payload, ProtocolJson.Options),
        status.SessionId,
        status.StateVersion);

    private sealed class FakeRemoteToolRegistry(bool blockSynchronously = false) : IRemoteToolRegistry
    {
        private readonly IReadOnlyList<RemoteToolDescriptor> _tools =
        [
            new("test.remote_echo", "1", "Returns a test value from the remote registry.", ParseSchema("{\"type\":\"object\",\"properties\":{\"value\":{\"type\":\"string\",\"maxLength\":64}},\"required\":[\"value\"],\"additionalProperties\":false}"), ParseSchema("{\"type\":\"object\",\"properties\":{\"result\":{\"type\":\"string\",\"maxLength\":128}},\"required\":[\"result\"],\"additionalProperties\":false}")),
            new("test.remote_wait", "1", "A cancellable test operation running on the remote registry.", ParseSchema("{\"type\":\"object\",\"properties\":{},\"required\":[],\"additionalProperties\":false}"), ParseSchema("{\"type\":\"object\",\"properties\":{\"status\":{\"type\":\"string\",\"maxLength\":16}},\"required\":[\"status\"],\"additionalProperties\":false}")),
            new("test.bad_output", "1", "Returns an invalid test result.", ParseSchema("{\"type\":\"object\",\"properties\":{},\"required\":[],\"additionalProperties\":false}"), ParseSchema("{\"type\":\"object\",\"properties\":{\"result\":{\"type\":\"string\",\"maxLength\":32}},\"required\":[\"result\"],\"additionalProperties\":false}"))
        ];
        private int _listCallCount;
        private int _executionCount;

        public TaskCompletionSource WaitStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource WaitCancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int ListCallCount => Volatile.Read(ref _listCallCount);
        public int ExecutionCount => Volatile.Read(ref _executionCount);
        public string CapabilitiesVersion => "test-v1";
        public IReadOnlyList<RemoteToolDescriptor> ListTools()
        {
            Interlocked.Increment(ref _listCallCount);
            return _tools;
        }

        public bool TryValidateCall(string name, JsonElement arguments, out string error)
        {
            var tool = _tools.SingleOrDefault(item => item.Name == name);
            if (tool is null)
            {
                error = "unknown_remote_tool";
                return false;
            }

            return RemoteToolSchemaValidator.ValidateArguments(tool.InputSchema, arguments, out error);
        }

        public async Task<JsonElement> ExecuteAsync(string name, JsonElement arguments, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _executionCount);
            switch (name)
            {
                case "test.remote_echo":
                    return JsonSerializer.SerializeToElement(new { result = arguments.GetProperty("value").GetString() + "-remote-executed" }, ProtocolJson.Options);
                case "test.remote_wait":
                    WaitStarted.TrySetResult();
                    try
                    {
                        if (blockSynchronously)
                        {
                            cancellationToken.WaitHandle.WaitOne();
                            cancellationToken.ThrowIfCancellationRequested();
                        }
                        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        WaitCancelled.TrySetResult();
                        throw;
                    }

                    return JsonSerializer.SerializeToElement(new { status = "finished" }, ProtocolJson.Options);
                case "test.bad_output":
                    return JsonSerializer.SerializeToElement(new { result = 42 }, ProtocolJson.Options);
                default:
                    throw new InvalidOperationException("unknown_remote_tool");
            }
        }

        private static JsonElement ParseSchema(string json)
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }
    }

    private sealed class SocketOwner(WebSocket socket) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            socket.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
