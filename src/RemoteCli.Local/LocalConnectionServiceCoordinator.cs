using System.Text.Json;
using Common.Protocol;
using Common.Security;

namespace RemoteCli.Local;

/// <summary>
/// Owns the local connection state exposed over IPC and gates every remote operation against it.
/// The desktop UI subscribes to AuthenticationRequested and remains responsible for collecting
/// the connection password and permission approval.
/// </summary>
public sealed class LocalConnectionServiceCoordinator(
    LocalConnectionStateMachine stateMachine,
    LocalSessionBrokerClient brokerClient)
{
    private readonly McpPresenceTracker _mcpPresence = new();

    public event Action<Guid, string?>? AuthenticationRequested;
    public event Action? CloseRequested;

    public bool HasActiveMcpClient(DateTimeOffset? now = null) => _mcpPresence.IsConnected(now);

    public Task CloseFromUserAsync(CancellationToken cancellationToken)
    {
        var request = new LocalServiceRequest(
            LocalIpcConstants.CurrentVersion,
            Guid.NewGuid(),
            "connection.close",
            LocalIpcConstants.DefaultRequestTimeoutMilliseconds,
            JsonSerializer.SerializeToElement(new { }, ProtocolJson.Options),
            null,
            null);
        return CloseAsync(request, stateMachine.GetSnapshot(), cancellationToken);
    }

    public async Task<LocalServiceResponse> HandleAsync(
        LocalServiceRequest request,
        CancellationToken cancellationToken)
    {
        var status = stateMachine.GetSnapshot();
        if (request.Type == "connection.status")
        {
            return Success(request, status);
        }

        if (request.Type == "connection.wait")
        {
            var input = ReadPayload<ConnectionWaitInput>(request.Payload);
            if (input.AfterStateVersion < 0 || input.WaitMilliseconds is < 0 or > 20_000)
            {
                return Failure(request, "invalid_wait", "The state version or wait duration is invalid.", false, status.StateVersion);
            }

            var changed = await stateMachine.WaitForChangeAsync(
                input.AfterStateVersion,
                TimeSpan.FromMilliseconds(input.WaitMilliseconds),
                cancellationToken).ConfigureAwait(false);
            return Success(request, changed);
        }

        if (request.ExpectedSessionId is not null || request.ExpectedStateVersion is not null)
        {
            if (!stateMachine.MatchesPrecondition(request.ExpectedSessionId, request.ExpectedStateVersion))
            {
                return Failure(request, "stale_session", "The connection changed after the caller read its status.", true, status.StateVersion);
            }
        }

        switch (request.Type)
        {
            case "connection.mcp.heartbeat":
            case "connection.mcp.disconnect":
            {
                var input = ReadPayload<McpPresenceInput>(request.Payload);
                if (input.ClientId == Guid.Empty)
                {
                    return Failure(request, "invalid_mcp_client", "The MCP client ID is invalid.", false, status.StateVersion);
                }

                if (request.Type == "connection.mcp.heartbeat")
                {
                    _mcpPresence.Refresh(input.ClientId);
                }
                else
                {
                    _mcpPresence.Remove(input.ClientId);
                }

                return Success(request, new { acknowledged = true }, status.StateVersion);
            }
            case "connection.auth":
                return StartAuthentication(request);
            case "connection.close":
                return await CloseAsync(request, status, cancellationToken).ConfigureAwait(false);
            case "remote.command.start":
            case "remote.command.receive":
            case "remote.command.cancel":
                return await HandleRemoteCommandAsync(request, status, cancellationToken).ConfigureAwait(false);
            case "remote.tools.list":
            case "remote.tool.call":
            case "remote.tool.cancel":
            case "remote.tool.receive":
                return await HandleRemoteToolAsync(request, status, cancellationToken).ConfigureAwait(false);
            default:
                return Failure(request, "unsupported", "The local connection service does not support this request.", false, status.StateVersion);
        }
    }

    private LocalServiceResponse StartAuthentication(LocalServiceRequest request)
    {
        var input = ReadPayload<AuthenticationRequestInput>(request.Payload);
        string? userName = null;
        if (!string.IsNullOrWhiteSpace(input.UserName))
        {
            try
            {
                userName = ConnectionAuthorizationCredential.NormalizeUserName(input.UserName);
            }
            catch (ArgumentException exception)
            {
                return Failure(request, "invalid_user_name", exception.Message, false, stateMachine.GetSnapshot().StateVersion);
            }
        }

        if (AuthenticationRequested is null)
        {
            return Failure(request, "approval_ui_unavailable", "The local authentication UI is unavailable.", true, stateMachine.GetSnapshot().StateVersion);
        }

        var (status, started) = stateMachine.BeginAuthentication();
        var authRequestId = status.AuthRequestId ?? Guid.NewGuid();
        if (started)
        {
            try
            {
                AuthenticationRequested?.Invoke(authRequestId, userName);
            }
            catch
            {
                stateMachine.TrySetFaulted(authRequestId, "ui", "approval_ui_unavailable", "The local UI could not start authentication.", true);
                status = stateMachine.GetSnapshot();
            }
        }

        var result = new AuthenticationRequestResult(
            authRequestId,
            status.ConnectionState,
            started,
            status.LastError?.Retryable ?? false,
            status.RetryAfterMilliseconds,
            started ? "Authentication was requested from the local UI." : "An authentication request is already active or the session is connected.");
        return Success(request, result, stateMachine.GetSnapshot().StateVersion);
    }

    private async Task<LocalServiceResponse> CloseAsync(
        LocalServiceRequest request,
        LocalConnectionStatus status,
        CancellationToken cancellationToken)
    {
        // Revoke the local session before performing network cleanup so concurrent MCP calls fail
        // their live-state check immediately.
        stateMachine.Close();
        if (status.ConnectionState == LocalConnectionState.Connected && status.SessionId is not null)
        {
            try
            {
                await brokerClient.SendAsync<object, JsonElement>("session.close", new { }, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException or TimeoutException)
            {
                // The session is closed locally below even if the peer has already disappeared.
            }
        }

        try
        {
            CloseRequested?.Invoke();
        }
        catch
        {
            // A UI listener cannot veto closing the local session.
        }

        return Success(request, new { closed = true }, stateMachine.GetSnapshot().StateVersion);
    }

    private async Task<LocalServiceResponse> HandleRemoteCommandAsync(
        LocalServiceRequest request,
        LocalConnectionStatus status,
        CancellationToken cancellationToken)
    {
        if (request.ExpectedSessionId is not { } expectedSessionId ||
            request.ExpectedStateVersion is not { } expectedStateVersion ||
            expectedSessionId != status.SessionId || expectedStateVersion != status.StateVersion ||
            !status.IsUsable(DateTimeOffset.UtcNow))
        {
            return Failure(request, "session_unavailable", "An authenticated remote session is not available for this request.", true, status.StateVersion);
        }

        var requiredPermission = request.Type switch
        {
            "remote.command.start" or "remote.command.receive" => RemotePermission.CommandExecute,
            "remote.command.cancel" => RemotePermission.CommandCancel,
            _ => RemotePermission.None
        };
        if (requiredPermission == RemotePermission.None ||
            !stateMachine.IsAuthorized(expectedSessionId, requiredPermission, status.RemoteIdentity))
        {
            return Failure(request, "permission_denied", "The current session does not grant this operation.", false, status.StateVersion);
        }

        try
        {
            switch (request.Type)
            {
                case "remote.command.start":
                {
                    var input = ReadPayload<LocalRemoteCommandStartInput>(request.Payload);
                    var result = await brokerClient.SendAsync<StartCommandInput, StartCommandResult>(
                        "command.start",
                        new StartCommandInput(input.CommandText, input.WorkingDirectory, input.TimeoutSeconds),
                        cancellationToken).ConfigureAwait(false);
                    return Success(request, new LocalRemoteCommandStartResult(
                        result.CommandId,
                        expectedSessionId,
                        result.Accepted ? "Running" : "Rejected",
                        result.TimeoutSeconds),
                        status.StateVersion);
                }
                case "remote.command.receive":
                {
                    var input = ReadPayload<LocalRemoteCommandReceiveInput>(request.Payload);
                    var waitSeconds = (int)Math.Ceiling(input.WaitMilliseconds / 1000d);
                    var result = await brokerClient.SendAsync<CommandOutputQuery, CommandPollResult>(
                        "command.output",
                        new CommandOutputQuery(input.OperationId, input.Cursor, waitSeconds),
                        cancellationToken).ConfigureAwait(false);
                    var error = result.Result?.ErrorCode is { } errorCode
                        ? new LocalConnectionError(
                            "remote_command",
                            IsSafeErrorCode(errorCode) ? errorCode : "remote_execution_failed",
                            "The remote command failed.",
                            false,
                            0,
                            request.RequestId)
                        : null;
                    return Success(request, new LocalRemoteCommandReceiveResult(
                        result.Outputs,
                        result.NextCursor,
                        result.Result is not null,
                        result.Result?.State ?? RemoteCommandState.Running,
                        result.Result?.ExitCode,
                        result.OutputTruncated,
                        error),
                        status.StateVersion);
                }
                case "remote.command.cancel":
                {
                    var input = ReadPayload<LocalRemoteCommandCancelInput>(request.Payload);
                    var result = await brokerClient.SendAsync<CancelCommandInput, JsonElement>(
                        "command.cancel",
                        new CancelCommandInput(input.OperationId),
                        cancellationToken).ConfigureAwait(false);
                    return Success(request, new LocalRemoteCommandCancelResult(input.OperationId, true), status.StateVersion);
                }
                default:
                    return Failure(request, "unsupported", "The remote command operation is unsupported.", false, status.StateVersion);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or TimeoutException or JsonException or ArgumentException)
        {
            var code = exception is InvalidOperationException && IsSafeErrorCode(exception.Message)
                ? exception.Message
                : "remote_session_unavailable";
            return Failure(request, code, "The authenticated remote command request failed.", true, stateMachine.GetSnapshot().StateVersion, "remote_command");
        }
    }

    private async Task<LocalServiceResponse> HandleRemoteToolAsync(
        LocalServiceRequest request,
        LocalConnectionStatus status,
        CancellationToken cancellationToken)
    {
        if (request.ExpectedSessionId is not { } expectedSessionId ||
            request.ExpectedStateVersion is not { } expectedStateVersion ||
            expectedSessionId != status.SessionId || expectedStateVersion != status.StateVersion ||
            !status.IsUsable(DateTimeOffset.UtcNow))
        {
            return Failure(request, "session_unavailable", "An authenticated remote session is not available for this request.", true, status.StateVersion);
        }

        var requiredPermission = request.Type switch
        {
            "remote.tools.list" => RemotePermission.SessionRead,
            "remote.tool.cancel" => RemotePermission.CommandCancel,
            _ => RemotePermission.CommandExecute
        };
        if (!stateMachine.IsAuthorized(expectedSessionId, requiredPermission, status.RemoteIdentity))
        {
            return Failure(request, "permission_denied", "The current session does not grant this operation.", false, status.StateVersion);
        }

        try
        {
            switch (request.Type)
            {
                case "remote.tools.list":
                {
                    if (request.Payload.ValueKind != JsonValueKind.Object || request.Payload.EnumerateObject().Any())
                    {
                        return Failure(request, "invalid_tool_list_request", "The tool list request must be an empty object.", false, status.StateVersion);
                    }

                    var result = await brokerClient.SendAsync<object, RemoteToolsListResult>(
                        "remote.tools.list", new { }, cancellationToken).ConfigureAwait(false);
                    return Success(request, result, status.StateVersion);
                }
                case "remote.tool.call":
                {
                    var input = ReadPayload<LocalRemoteToolCallInput>(request.Payload);
                    if (string.IsNullOrWhiteSpace(input.Name) || input.Name.Length > 128 || input.Name.Any(char.IsControl) ||
                        input.Arguments.ValueKind != JsonValueKind.Object ||
                        JsonSerializer.SerializeToUtf8Bytes(input.Arguments, ProtocolJson.Options).Length > 16 * 1024)
                    {
                        return Failure(request, "invalid_tool_arguments", "The remote tool name or arguments exceed the allowed bounds.", false, status.StateVersion);
                    }

                    var result = await brokerClient.SendAsync<LocalRemoteToolCallInput, StartRemoteToolResult>(
                        "remote.tool.call", input, cancellationToken).ConfigureAwait(false);
                    return Success(request, new LocalRemoteToolCallResult(result.OperationId, result.SessionId, result.State), status.StateVersion);
                }
                case "remote.tool.cancel":
                {
                    var input = ReadPayload<LocalRemoteToolCancelInput>(request.Payload);
                    if (input.OperationId == Guid.Empty)
                    {
                        return Failure(request, "invalid_tool_operation", "The operation ID is invalid.", false, status.StateVersion);
                    }

                    var result = await brokerClient.SendAsync<LocalRemoteToolCancelInput, LocalRemoteToolCancelResult>(
                        "remote.tool.cancel", input, cancellationToken).ConfigureAwait(false);
                    return Success(request, result, status.StateVersion);
                }
                case "remote.tool.receive":
                {
                    var input = ReadPayload<LocalRemoteToolReceiveInput>(request.Payload);
                    if (input.OperationId == Guid.Empty || input.Cursor < 0 || input.WaitMilliseconds is < 0 or > 30_000)
                    {
                        return Failure(request, "invalid_tool_receive_query", "The operation ID, cursor, or wait exceeds the allowed bounds.", false, status.StateVersion);
                    }

                    var result = await brokerClient.SendAsync<LocalRemoteToolReceiveInput, RemoteToolPollResult>(
                        "remote.tool.receive", input, cancellationToken).ConfigureAwait(false);
                    var error = result.ErrorCode is null
                        ? null
                        : new LocalConnectionError("remote_tool", result.ErrorCode, result.ErrorMessage ?? "The remote tool failed.", false, 0, request.RequestId);
                    return Success(request, new LocalRemoteToolReceiveResult(
                        result.OperationId,
                        result.NextCursor,
                        result.Done,
                        result.State,
                        result.Result,
                        error), status.StateVersion);
                }
                default:
                    return Failure(request, "unsupported", "The remote tool operation is unsupported.", false, status.StateVersion);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or TimeoutException or JsonException or ArgumentException or InvalidDataException)
        {
            var code = exception switch
            {
                InvalidOperationException invalidOperation when IsSafeErrorCode(invalidOperation.Message) => invalidOperation.Message,
                InvalidDataException => "remote_tool_invalid_response",
                JsonException => "remote_tool_invalid_response",
                TimeoutException => "remote_tool_timeout",
                IOException io when IsSafeErrorCode(io.Message) => io.Message,
                IOException => "remote_tool_ipc_unavailable",
                ArgumentException => "remote_tool_invalid_request",
                _ => "remote_tool_unavailable"
            };
            var retryable = code is "remote_tool_timeout" or "remote_tool_ipc_unavailable";
            return Failure(request, code, "The authenticated remote tool request failed.", retryable, stateMachine.GetSnapshot().StateVersion, "remote_tool");
        }
    }

    private static bool IsSafeErrorCode(string value) =>
        value.Length is > 0 and <= 64 && value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.');

    private static T ReadPayload<T>(JsonElement payload) =>
        payload.Deserialize<T>(ProtocolJson.Options)
        ?? throw new InvalidDataException("Local service request payload was empty.");

    private static LocalServiceResponse Success<T>(LocalServiceRequest request, T payload, long? stateVersion = null) =>
        LocalConnectionServicePipeServer.Success(
            request,
            stateVersion ?? 0,
            JsonSerializer.SerializeToElement(payload, ProtocolJson.Options));

    private static LocalServiceResponse Failure(
        LocalServiceRequest request,
        string code,
        string message,
        bool retryable,
        long stateVersion,
        string stage = "service") => LocalConnectionServicePipeServer.Failure(
            request, code, message, retryable, stateVersion, stage);
}
