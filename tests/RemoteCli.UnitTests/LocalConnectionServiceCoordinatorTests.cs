using System.Collections.Concurrent;
using System.Text.Json;
using Common.Protocol;
using RemoteCli.Local;

namespace RemoteCli.UnitTests;

public sealed class LocalConnectionServiceCoordinatorTests
{
    private static readonly TimeSpan CompletionLimit = TimeSpan.FromSeconds(3);

    [Fact]
    public async Task ConcurrentAuthenticationRequestsShareOneAuthenticationFlow()
    {
        const int requestCount = 32;
        var coordinator = CreateCoordinator(new LocalConnectionStateMachine());
        const string requestedUserName = "test.user";
        var events = new ConcurrentBag<(Guid AuthRequestId, string? UserName)>();
        coordinator.AuthenticationRequested += (authRequestId, userName) => events.Add((authRequestId, userName));

        var tasks = Enumerable.Range(0, requestCount)
            .Select(_ => coordinator.HandleAsync(
                CreateRequest("connection.auth", new AuthenticationRequestInput(requestedUserName)),
                CancellationToken.None));
        var responses = await Task.WhenAll(tasks).WaitAsync(CompletionLimit);
        var results = responses
            .Select(response => response.Payload.Deserialize<AuthenticationRequestResult>(ProtocolJson.Options)!)
            .ToArray();

        Assert.All(responses, response => Assert.True(response.Success));
        Assert.Single(events);
        Assert.All(events, item => Assert.Equal(requestedUserName, item.UserName));
        Assert.Single(results.Select(result => result.AuthRequestId).Distinct());
        Assert.Equal(events.Single().AuthRequestId, results[0].AuthRequestId);
    }

    [Fact]
    public async Task ConnectionStatusReturnsImmediatelyWithoutBrokerIo()
    {
        var machine = new LocalConnectionStateMachine();
        var coordinator = CreateCoordinator(machine);

        var responseTask = coordinator.HandleAsync(
            CreateRequest("connection.status", new { }),
            CancellationToken.None);

        Assert.True(responseTask.IsCompletedSuccessfully);
        var response = await responseTask;
        Assert.True(response.Success);
        var status = response.Payload.Deserialize<LocalConnectionStatus>(ProtocolJson.Options);
        Assert.NotNull(status);
        Assert.Equal(LocalConnectionState.Disconnected, status.ConnectionState);
        Assert.Equal(LocalIpcConstants.CurrentVersion, status.ProtocolVersion);
    }

    [Fact]
    public async Task McpPresenceDoesNotGrantARemoteSessionAndCanDisconnect()
    {
        var machine = new LocalConnectionStateMachine();
        var coordinator = CreateCoordinator(machine);
        var clientId = Guid.NewGuid();

        var heartbeat = await coordinator.HandleAsync(
            CreateRequest("connection.mcp.heartbeat", new McpPresenceInput(clientId)),
            CancellationToken.None);
        Assert.True(heartbeat.Success);
        Assert.True(coordinator.HasActiveMcpClient());
        Assert.Equal(LocalConnectionState.Disconnected, machine.GetSnapshot().ConnectionState);

        var disconnect = await coordinator.HandleAsync(
            CreateRequest("connection.mcp.disconnect", new McpPresenceInput(clientId)),
            CancellationToken.None);
        Assert.True(disconnect.Success);
        Assert.False(coordinator.HasActiveMcpClient());
    }

    [Fact]
    public void McpPresenceExpiresIfTheClientDisappears()
    {
        var tracker = new McpPresenceTracker();
        var now = DateTimeOffset.UtcNow;
        tracker.Refresh(Guid.NewGuid(), now);
        Assert.True(tracker.IsConnected(now.AddSeconds(7)));
        Assert.False(tracker.IsConnected(now.AddSeconds(8)));
    }

    [Fact]
    public async Task RemoteCommandStartRejectsStaleSessionOrStateVersion()
    {
        var machine = CreateConnectedMachine(RemotePermission.CommandExecute, out var sessionId);
        var coordinator = CreateCoordinator(machine);
        var status = machine.GetSnapshot();
        var payload = new LocalRemoteCommandStartInput("Get-Date", null, 30);

        var staleSession = await coordinator.HandleAsync(
            CreateRequest("remote.command.start", payload, Guid.NewGuid(), status.StateVersion),
            CancellationToken.None);
        var staleVersion = await coordinator.HandleAsync(
            CreateRequest("remote.command.start", payload, sessionId, status.StateVersion - 1),
            CancellationToken.None);

        Assert.Equal("stale_session", staleSession.Error?.Code);
        Assert.Equal("stale_session", staleVersion.Error?.Code);
    }

    [Fact]
    public async Task ConnectedSessionWithoutCommandPermissionIsRejectedBeforeBrokerIo()
    {
        var machine = CreateConnectedMachine(RemotePermission.SessionRead, out var sessionId);
        var coordinator = CreateCoordinator(machine);
        var status = machine.GetSnapshot();

        var responseTask = coordinator.HandleAsync(
            CreateRequest(
                "remote.command.start",
                new LocalRemoteCommandStartInput("Get-Date", null, 30),
                sessionId,
                status.StateVersion),
            CancellationToken.None);

        Assert.True(responseTask.IsCompletedSuccessfully);
        var response = await responseTask;
        Assert.Equal("permission_denied", response.Error?.Code);
    }

    [Fact]
    public async Task RepeatedConnectionCloseIsIdempotent()
    {
        var machine = new LocalConnectionStateMachine();
        var coordinator = CreateCoordinator(machine);

        var first = await coordinator.HandleAsync(
            CreateRequest("connection.close", new { }),
            CancellationToken.None);
        var stateAfterFirstClose = machine.GetSnapshot();
        var second = await coordinator.HandleAsync(
            CreateRequest("connection.close", new { }),
            CancellationToken.None);

        Assert.True(first.Success);
        Assert.True(second.Success);
        Assert.True(first.Payload.GetProperty("closed").GetBoolean());
        Assert.True(second.Payload.GetProperty("closed").GetBoolean());
        Assert.Equal(LocalConnectionState.Closed, machine.GetSnapshot().ConnectionState);
        Assert.Equal(stateAfterFirstClose.StateVersion, machine.GetSnapshot().StateVersion);
        Assert.Equal(first.StateVersion, second.StateVersion);
    }

    private static LocalConnectionServiceCoordinator CreateCoordinator(LocalConnectionStateMachine machine) =>
        new(machine, new LocalSessionBrokerClient());

    private static LocalConnectionStateMachine CreateConnectedMachine(
        RemotePermission permissions,
        out Guid sessionId)
    {
        var machine = new LocalConnectionStateMachine();
        var authentication = machine.BeginAuthentication();
        var authRequestId = authentication.Status.AuthRequestId!.Value;
        Assert.True(machine.TrySetPhase(authRequestId, LocalConnectionState.AwaitingAuthentication));
        Assert.True(machine.TrySetPhase(authRequestId, LocalConnectionState.Authenticating));

        sessionId = Guid.NewGuid();
        Assert.True(machine.TrySetConnected(
            authRequestId,
            sessionId,
            "unit-test-remote",
            "Unit Test Transport",
            permissions,
            DateTimeOffset.UtcNow.AddHours(1)));
        return machine;
    }

    private static LocalServiceRequest CreateRequest(
        string type,
        object payload,
        Guid? expectedSessionId = null,
        long? expectedStateVersion = null) => new(
            LocalIpcConstants.CurrentVersion,
            Guid.NewGuid(),
            type,
            LocalIpcConstants.DefaultRequestTimeoutMilliseconds,
            JsonSerializer.SerializeToElement(payload, ProtocolJson.Options),
            expectedSessionId,
            expectedStateVersion);
}
