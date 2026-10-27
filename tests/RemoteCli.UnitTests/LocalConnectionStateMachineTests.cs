using Common.Protocol;
using RemoteCli.Local;

namespace RemoteCli.UnitTests;

public sealed class LocalConnectionStateMachineTests
{
    [Fact]
    public async Task WaitForChangeReturnsWhenStateVersionAdvances()
    {
        var state = new LocalConnectionStateMachine();
        var initial = state.GetSnapshot();
        var wait = state.WaitForChangeAsync(initial.StateVersion, TimeSpan.FromSeconds(2), CancellationToken.None);

        state.BeginAuthentication();

        var changed = await wait;
        Assert.True(changed.StateVersion > initial.StateVersion);
        Assert.Equal(LocalConnectionState.Connecting, changed.ConnectionState);
    }

    [Fact]
    public async Task WaitForChangeReturnsCurrentSnapshotAtTimeout()
    {
        var state = new LocalConnectionStateMachine();
        var initial = state.GetSnapshot();

        var result = await state.WaitForChangeAsync(
            initial.StateVersion,
            TimeSpan.FromMilliseconds(20),
            CancellationToken.None);

        Assert.Equal(initial.StateVersion, result.StateVersion);
        Assert.Equal(LocalConnectionState.Disconnected, result.ConnectionState);
    }

    [Fact]
    public void ConcurrentAuthenticationRequestsShareOneRequestId()
    {
        var machine = new LocalConnectionStateMachine();
        var results = Enumerable.Range(0, 64)
            .AsParallel()
            .Select(_ => machine.BeginAuthentication())
            .ToArray();

        Assert.Equal(1, results.Count(result => result.Started));
        Assert.Single(results.Select(result => result.Status.AuthRequestId).Distinct());
        Assert.Equal(LocalConnectionState.Connecting, machine.GetSnapshot().ConnectionState);
    }

    [Fact]
    public void ConnectedStateRequiresAuthenticatingPhaseAndCurrentRequestId()
    {
        var machine = new LocalConnectionStateMachine();
        var start = machine.BeginAuthentication();
        var sessionId = Guid.NewGuid();
        var expiresAt = DateTimeOffset.UtcNow.AddHours(1);

        Assert.False(machine.TrySetConnected(start.Status.AuthRequestId!.Value, sessionId, "remote", "VPS Relay", RemotePermission.CommandExecute, expiresAt));
        Assert.True(machine.TrySetPhase(start.Status.AuthRequestId.Value, LocalConnectionState.AwaitingAuthentication));
        Assert.True(machine.TrySetPhase(start.Status.AuthRequestId.Value, LocalConnectionState.Authenticating));
        Assert.False(machine.TrySetConnected(Guid.NewGuid(), sessionId, "remote", "VPS Relay", RemotePermission.CommandExecute, expiresAt));
        Assert.True(machine.TrySetConnected(start.Status.AuthRequestId.Value, sessionId, "remote", "VPS Relay", RemotePermission.CommandExecute, expiresAt));

        var connected = machine.GetSnapshot();
        Assert.Equal(LocalConnectionState.Connected, connected.ConnectionState);
        Assert.True(connected.Authenticated);
        Assert.True(connected.IsUsable(DateTimeOffset.UtcNow));
    }

    [Fact]
    public void ConnectedStateAuthorizesOnlyMatchingSessionPermissionAndIdentity()
    {
        var machine = CreateConnectedMachine(out var sessionId, out var expiresAt);

        Assert.True(machine.IsAuthorized(sessionId, RemotePermission.CommandExecute, "remote-host"));
        Assert.False(machine.IsAuthorized(sessionId, RemotePermission.SessionRead, "remote-host"));
        Assert.False(machine.IsAuthorized(Guid.NewGuid(), RemotePermission.CommandExecute, "remote-host"));
        Assert.False(machine.IsAuthorized(sessionId, RemotePermission.CommandExecute, "other-host"));
        Assert.False(machine.IsAuthorized(sessionId, RemotePermission.None, "remote-host"));
        Assert.False(machine.IsAuthorized(sessionId, RemotePermission.CommandExecute, "remote-host", expiresAt));

        var expired = machine.GetSnapshot(expiresAt);
        Assert.Equal(LocalConnectionState.Expired, expired.ConnectionState);
        Assert.False(expired.Authenticated);
        Assert.Null(expired.SessionId);
        Assert.Equal("session_expired", expired.LastError?.Code);
        Assert.False(machine.IsAuthorized(sessionId, RemotePermission.CommandExecute, "remote-host", expiresAt));
    }

    [Fact]
    public void StaleAuthenticationRequestCannotOverwriteFaultedState()
    {
        var machine = new LocalConnectionStateMachine();
        var start = machine.BeginAuthentication();
        Assert.True(machine.TrySetFaulted(start.Status.AuthRequestId, "hello", "peer_rejected", "Remote rejected the invitation.", false));
        Assert.False(machine.TrySetPhase(start.Status.AuthRequestId!.Value, LocalConnectionState.Authenticating));
        Assert.False(machine.TrySetConnected(start.Status.AuthRequestId.Value, Guid.NewGuid(), "remote", "VPS Relay", RemotePermission.CommandExecute, DateTimeOffset.UtcNow.AddMinutes(1)));

        var faulted = machine.GetSnapshot();
        Assert.Equal(LocalConnectionState.Faulted, faulted.ConnectionState);
        Assert.False(faulted.Authenticated);
        Assert.NotNull(faulted.LastError);
        Assert.False(faulted.LastError!.Retryable);
    }

    [Fact]
    public void EveryStateTransitionIncrementsVersionAndCloseIsIdempotent()
    {
        var machine = new LocalConnectionStateMachine();
        var initial = machine.GetSnapshot();
        var start = machine.BeginAuthentication();
        var waiting = machine.GetSnapshot();
        Assert.True(machine.TrySetPhase(start.Status.AuthRequestId!.Value, LocalConnectionState.AwaitingAuthentication));
        var awaiting = machine.GetSnapshot();
        machine.Close();
        var closed = machine.GetSnapshot();
        machine.Close();
        var closedAgain = machine.GetSnapshot();

        Assert.True(start.Status.StateVersion > initial.StateVersion);
        Assert.True(awaiting.StateVersion > waiting.StateVersion);
        Assert.True(closed.StateVersion > awaiting.StateVersion);
        Assert.Equal(closed.StateVersion, closedAgain.StateVersion);
        Assert.Equal(LocalConnectionState.Closed, closedAgain.ConnectionState);
        Assert.False(closedAgain.Authenticated);
    }

    private static LocalConnectionStateMachine CreateConnectedMachine(out Guid sessionId, out DateTimeOffset expiresAt)
    {
        var machine = new LocalConnectionStateMachine();
        var start = machine.BeginAuthentication();
        machine.TrySetPhase(start.Status.AuthRequestId!.Value, LocalConnectionState.AwaitingAuthentication);
        machine.TrySetPhase(start.Status.AuthRequestId.Value, LocalConnectionState.Authenticating);
        sessionId = Guid.NewGuid();
        expiresAt = DateTimeOffset.UtcNow.AddMinutes(10);
        machine.TrySetConnected(
            start.Status.AuthRequestId.Value,
            sessionId,
            "remote-host",
            "VPS Relay",
            RemotePermission.CommandExecute,
            expiresAt);
        return machine;
    }
}
