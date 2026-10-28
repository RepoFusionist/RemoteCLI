using Common.Protocol;
using RemoteCli.Local;

namespace RemoteCli.UnitTests;

public sealed class ConnectionMcpAdapterTests
{
    [Fact]
    public void OfflineToolListContainsOnlyConnectionControls()
    {
        var tools = ConnectionMcpAdapter.BuildToolsForStatus(
            LocalConnectionStatus.Unavailable("The local service is stopped."));
        var names = tools.Select(tool => tool.Name).Order(StringComparer.Ordinal).ToArray();

        Assert.Equal(
            [
                "remotecli_connection_auth",
                "remotecli_connection_close",
                "remotecli_connection_connect",
                "remotecli_connection_discover",
                "remotecli_connection_reconnect",
                "remotecli_connection_status",
                "remotecli_connection_wait"
            ],
            names);
    }

    [Fact]
    public void ConnectedToolListFollowsApprovedPermissions()
    {
        var machine = new LocalConnectionStateMachine();
        var start = machine.BeginAuthentication();
        var authRequestId = start.Status.AuthRequestId!.Value;
        Assert.True(machine.TrySetPhase(authRequestId, LocalConnectionState.AwaitingAuthentication));
        Assert.True(machine.TrySetPhase(authRequestId, LocalConnectionState.Authenticating));
        Assert.True(machine.TrySetConnected(
            authRequestId,
            Guid.NewGuid(),
            "remote-host",
            "VPS Relay",
            RemotePermission.SessionRead | RemotePermission.CommandExecute,
            DateTimeOffset.UtcNow.AddMinutes(5)));

        var names = ConnectionMcpAdapter.BuildToolsForStatus(machine.GetSnapshot())
            .Select(tool => tool.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains("remotecli_connection_status", names);
        Assert.Contains("remotecli_connection_auth", names);
        Assert.Contains("remotecli_connection_connect", names);
        Assert.Contains("remotecli_connection_reconnect", names);
        Assert.Contains("remotecli_connection_close", names);
        Assert.Contains("remotecli_remote_command_start", names);
        Assert.Contains("remotecli_remote_command_receive", names);
        Assert.DoesNotContain("remotecli_remote_command_cancel", names);
        Assert.DoesNotContain("remotecli_remote_tool_cancel", names);
        Assert.Contains("remotecli_remote_tools_list", names);
        Assert.Contains("remotecli_remote_tool_call", names);
        Assert.Contains("remotecli_remote_workspace_read", names);
        Assert.Contains("remotecli_remote_tool_receive", names);
    }

    [Fact]
    public void ReadOnlyConnectionAndWorkspaceToolsAreAnnotatedWhileGenericCallsAreNot()
    {
        var machine = new LocalConnectionStateMachine();
        var start = machine.BeginAuthentication();
        var authRequestId = start.Status.AuthRequestId!.Value;
        Assert.True(machine.TrySetPhase(authRequestId, LocalConnectionState.AwaitingAuthentication));
        Assert.True(machine.TrySetPhase(authRequestId, LocalConnectionState.Authenticating));
        Assert.True(machine.TrySetConnected(
            authRequestId,
            Guid.NewGuid(),
            "remote-host",
            "VPS Relay",
            RemotePermission.SessionRead | RemotePermission.CommandExecute,
            DateTimeOffset.UtcNow.AddMinutes(5)));

        var tools = ConnectionMcpAdapter.BuildToolsForStatus(machine.GetSnapshot())
            .ToDictionary(tool => tool.Name, StringComparer.Ordinal);

        Assert.True(tools["remotecli_connection_status"].Annotations?.ReadOnlyHint);
        Assert.True(tools["remotecli_remote_tools_list"].Annotations?.ReadOnlyHint);
        Assert.True(tools["remotecli_remote_workspace_read"].Annotations?.ReadOnlyHint);
        Assert.True(tools["remotecli_remote_tool_receive"].Annotations?.ReadOnlyHint);
        Assert.Null(tools["remotecli_remote_tool_call"].Annotations?.ReadOnlyHint);

        var serializedStatus = System.Text.Json.JsonSerializer.SerializeToElement(
            tools["remotecli_connection_status"],
            ProtocolJson.Options);
        Assert.True(serializedStatus.GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean());
    }

    [Theory]
    [InlineData("remote.directory_list", true)]
    [InlineData("remote.file_read", true)]
    [InlineData("remote.file_search", true)]
    [InlineData("remote.text_search", true)]
    [InlineData("remote.file_info", true)]
    [InlineData("remote.file_write", false)]
    [InlineData("remote.file_delete", false)]
    [InlineData("user.custom_tool", false)]
    public void WorkspaceReadWrapperAllowsOnlyBuiltInReadTools(string toolName, bool expected)
    {
        Assert.Equal(expected, ConnectionMcpAdapter.IsReadOnlyWorkspaceToolName(toolName));
    }

    [Fact]
    public void RemoteToolCancellationIsListedOnlyWithApprovedCancelPermission()
    {
        var machine = new LocalConnectionStateMachine();
        var start = machine.BeginAuthentication();
        var authRequestId = start.Status.AuthRequestId!.Value;
        Assert.True(machine.TrySetPhase(authRequestId, LocalConnectionState.AwaitingAuthentication));
        Assert.True(machine.TrySetPhase(authRequestId, LocalConnectionState.Authenticating));
        Assert.True(machine.TrySetConnected(
            authRequestId,
            Guid.NewGuid(),
            "remote-host",
            "VPS Relay",
            RemotePermission.SessionRead | RemotePermission.CommandExecute | RemotePermission.CommandCancel,
            DateTimeOffset.UtcNow.AddMinutes(5)));

        var names = ConnectionMcpAdapter.BuildToolsForStatus(machine.GetSnapshot())
            .Select(tool => tool.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains("remotecli_remote_tool_cancel", names);
    }

    [Fact]
    public void ConnectionViewDistinguishesOfflinePendingAndConnectedStates()
    {
        var offline = ConnectionMcpAdapter.BuildConnectionView(
            LocalConnectionStatus.Unavailable("The desktop is not running."));
        Assert.False(offline.GetProperty("connected").GetBoolean());
        Assert.False(offline.GetProperty("canRequestConnection").GetBoolean());
        Assert.Equal("start_desktop", offline.GetProperty("nextAction").GetString());

        var machine = new LocalConnectionStateMachine();
        var disconnected = ConnectionMcpAdapter.BuildConnectionView(machine.GetSnapshot());
        Assert.True(disconnected.GetProperty("canRequestConnection").GetBoolean());
        Assert.Equal("connect", disconnected.GetProperty("nextAction").GetString());

        var authRequestId = machine.BeginAuthentication().Status.AuthRequestId!.Value;
        var pending = ConnectionMcpAdapter.BuildConnectionView(machine.GetSnapshot());
        Assert.False(pending.GetProperty("connected").GetBoolean());
        Assert.Equal("complete_authentication_in_desktop", pending.GetProperty("nextAction").GetString());

        Assert.True(machine.TrySetPhase(authRequestId, LocalConnectionState.AwaitingAuthentication));
        Assert.True(machine.TrySetPhase(authRequestId, LocalConnectionState.Authenticating));
        Assert.True(machine.TrySetConnected(
            authRequestId,
            Guid.NewGuid(),
            "remote-host",
            "VPS Relay",
            RemotePermission.CommandExecute,
            DateTimeOffset.UtcNow.AddMinutes(5)));
        var connected = ConnectionMcpAdapter.BuildConnectionView(machine.GetSnapshot());
        Assert.True(connected.GetProperty("connected").GetBoolean());
        Assert.Equal("use_remote_tools", connected.GetProperty("nextAction").GetString());

        machine.TrySetDisconnected();
        var dropped = ConnectionMcpAdapter.BuildConnectionView(machine.GetSnapshot());
        Assert.False(dropped.GetProperty("connected").GetBoolean());
        Assert.Equal("connect", dropped.GetProperty("nextAction").GetString());
    }

    [Theory]
    [InlineData("remote.command.start", "{\"state\":\"Rejected\"}", true)]
    [InlineData("remote.command.receive", "{\"done\":true,\"state\":\"Succeeded\"}", false)]
    [InlineData("remote.command.receive", "{\"done\":true,\"state\":\"Failed\",\"exitCode\":1}", true)]
    [InlineData("remote.command.receive", "{\"done\":true,\"state\":\"TimedOut\"}", true)]
    [InlineData("remote.command.receive", "{\"done\":false,\"state\":\"Running\"}", false)]
    [InlineData("remote.tool.receive", "{\"done\":true,\"state\":\"Cancelled\"}", true)]
    public void CompletedOperationFailuresAreMcpErrors(string requestType, string json, bool expected)
    {
        using var document = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal(expected, ConnectionMcpAdapter.IsCompletedOperationFailure(requestType, document.RootElement));
    }
}
