using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Common.Protocol;
using Common.Security;

namespace RemoteCli.Local;

internal sealed record BrokerRequest(Guid RequestId, string Type, JsonElement Payload);
internal sealed record BrokerResponse(Guid RequestId, bool Success, JsonElement Payload, string? Error);
internal sealed record EmptyBrokerInput;
internal sealed record StartCommandInput(string CommandText, string? WorkingDirectory, int TimeoutSeconds);
internal sealed record CommandOutputQuery(Guid CommandId, long AfterCursor, int WaitSeconds);
internal sealed record CancelCommandInput(Guid CommandId);
internal sealed record StartCommandResult(Guid CommandId, bool Accepted, int TimeoutSeconds);
internal sealed record CommandPollResult(IReadOnlyList<RemoteCommandOutput> Outputs, long NextCursor, RemoteCommandResult? Result, bool OutputTruncated);
internal sealed record StartRemoteToolResult(Guid OperationId, string SessionId, RemoteToolOperationState State);
internal sealed record RemoteToolPollResult(Guid OperationId, long NextCursor, bool Done, RemoteToolOperationState State, JsonElement? Result, string? ErrorCode, string? ErrorMessage);

public sealed class LocalSessionBroker : IAsyncDisposable
{
    private const int MaximumRetainedCommands = 256;
    private const int MaximumConcurrentPipeRequests = 16;
    private const int MaximumSessionOutputCharacters = 32 * 1024 * 1024;
    private const int MaximumPollCharacters = 64 * 1024;
    private readonly string _pipeName;
    private readonly SecurePeerChannel _channel;
    private readonly CancellationTokenSource _stop;
    private readonly SemaphoreSlim _wireWriter = new(1, 1);
    private readonly SemaphoreSlim _wireSendOrder = new(1, 1);
    private readonly SemaphoreSlim _toolCallWriter = new(1, 1);
    private readonly ConcurrentDictionary<Guid, CommandBuffer> _commands = new();
    private readonly ConcurrentDictionary<Guid, ToolBuffer> _tools = new();
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _toolCancellationSources = new();
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<RemoteToolsListResponse>> _pendingToolLists = new();
    private readonly ConcurrentDictionary<Guid, Task> _pendingToolDispatches = new();
    private readonly object _outputBudgetLock = new();
    private long _outputCharacters;
    private long _commandSequence;
    private long _toolSequence;
    private long _wireSequence;
    private bool _closeAfterResponse;
    private int _currentPermissions;
    private long _policyVersion;
    private long _lastPongTicks = DateTimeOffset.UtcNow.UtcTicks;

    public event Action<string, string>? SessionFaulted;
    public event Action<string>? PipeRequestFaulted;
    public event Action<RemoteSessionPolicy>? PolicyChanged;
    private RemotePermission CurrentPermissions => (RemotePermission)Volatile.Read(ref _currentPermissions);

    public LocalSessionBroker(SecurePeerChannel channel, CancellationToken cancellationToken, string? pipeName = null)
    {
        _channel = channel;
        _pipeName = pipeName ?? PipeName;
        _stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _currentPermissions = (int)channel.ApprovedPermissions;
    }

    public async Task RunAsync()
    {
        var expiresIn = _channel.SessionExpiresAt - DateTimeOffset.UtcNow;
        if (expiresIn <= TimeSpan.Zero)
        {
            return;
        }

        using var expiry = new CancellationTokenSource();
        if (_channel.SessionExpiresAt != DateTimeOffset.MaxValue) expiry.CancelAfter(expiresIn);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, expiry.Token);
        var readerTask = ReadRemoteAsync(linked.Token);
        var brokerTask = RunPipeServerAsync(linked.Token);
        var heartbeatTask = SendHeartbeatsAsync(linked.Token);
        await Task.WhenAny(readerTask, brokerTask, heartbeatTask, Task.Delay(Timeout.InfiniteTimeSpan, linked.Token)).ConfigureAwait(false);
        linked.Cancel();
        _stop.Cancel();
        try
        {
            await Task.WhenAll(readerTask, brokerTask, heartbeatTask).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException or ObjectDisposedException or TimeoutException)
        {
            var remoteFailure = readerTask.Exception?.GetBaseException();
            var brokerFailure = brokerTask.Exception?.GetBaseException();
            var failure = remoteFailure ?? brokerFailure ?? exception;
            var stage = remoteFailure is not null ? "remote_read" : brokerFailure is not null ? "local_ipc" : "session";
            var code = failure switch
            {
                InvalidDataException => "invalid_remote_message",
                TimeoutException => "session_timeout",
                IOException => "transport_closed",
                ObjectDisposedException => "session_disposed",
                _ => "session_stopped"
            };
            try
            {
                SessionFaulted?.Invoke(stage, code);
            }
            catch
            {
                // Diagnostics cannot delay session cleanup.
            }
        }

        try
        {
            await Task.WhenAll(_pendingToolDispatches.Values).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException or TimeoutException or IOException or ObjectDisposedException)
        {
        }
    }

    public ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _stop.Dispose();
        _wireWriter.Dispose();
        _wireSendOrder.Dispose();
        _toolCallWriter.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task RunPipeServerAsync(CancellationToken cancellationToken)
    {
        using var requestSlots = new SemaphoreSlim(MaximumConcurrentPipeRequests, MaximumConcurrentPipeRequests);
        var handlers = new ConcurrentDictionary<int, Task>();
        var nextHandlerId = 0;
        var firstPipeInstance = true;
        while (!cancellationToken.IsCancellationRequested)
        {
            await requestSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
            NamedPipeServerStream? pipe = null;
            try
            {
                var options = PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly;
                if (firstPipeInstance)
                {
                    options |= PipeOptions.FirstPipeInstance;
                }

                pipe = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.InOut,
                    MaximumConcurrentPipeRequests,
                    PipeTransmissionMode.Byte,
                    options,
                    64 * 1024,
                    64 * 1024);
                firstPipeInstance = false;
                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                pipe?.Dispose();
                requestSlots.Release();
                break;
            }
            catch
            {
                pipe?.Dispose();
                requestSlots.Release();
                throw;
            }

            var handlerId = Interlocked.Increment(ref nextHandlerId);
            var handler = HandlePipeConnectionAsync(pipe, requestSlots, cancellationToken);
            handlers[handlerId] = handler;
            _ = handler.ContinueWith(
                _ => handlers.TryRemove(handlerId, out var removedHandler),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        try
        {
            await Task.WhenAll(handlers.Values).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or JsonException or ArgumentException or InvalidOperationException or OperationCanceledException or ObjectDisposedException)
        {
        }
    }

    private async Task HandlePipeConnectionAsync(
        NamedPipeServerStream pipe,
        SemaphoreSlim requestSlots,
        CancellationToken cancellationToken)
    {
        using (pipe)
        using (var pipeWriter = new SemaphoreSlim(1, 1))
        {
            try
            {
                var request = await FramedJsonStream.ReadAsync<BrokerRequest>(pipe, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidDataException("Local broker request was empty.");
                var (response, closeAfterResponse) = await HandleRequestAsync(request, cancellationToken).ConfigureAwait(false);
                await FramedJsonStream.WriteAsync(pipe, response, pipeWriter, cancellationToken).ConfigureAwait(false);
                if (closeAfterResponse)
                {
                    _stop.Cancel();
                }
            }
            catch (Exception exception) when (exception is IOException or JsonException or ArgumentException or InvalidOperationException or OperationCanceledException)
            {
                if (exception is OperationCanceledException && cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                var code = exception switch
                {
                    InvalidDataException => "invalid_request",
                    JsonException => "invalid_request",
                    IOException => "pipe_io",
                    ArgumentException => "invalid_arguments",
                    InvalidOperationException => "invalid_operation",
                    _ => "request_failed"
                };
                try
                {
                    PipeRequestFaulted?.Invoke(code);
                }
                catch
                {
                    // Diagnostics cannot prevent the broker from serving another local request.
                }
            }
            finally
            {
                requestSlots.Release();
            }
        }
    }

    private async Task<(BrokerResponse Response, bool Close)> HandleRequestAsync(BrokerRequest request, CancellationToken cancellationToken)
    {
        if (request.RequestId == Guid.Empty)
        {
            return (Fail(request.RequestId, "invalid_request"), false);
        }

        switch (request.Type)
        {
            case "session.status":
                return (Success(request.RequestId, new SessionStatus(
                    ProtocolConstants.CurrentVersion,
                    _channel.SessionId,
                    _channel.RemoteIdentity,
                    RemoteSessionState.Connected,
                    "VPS Relay",
                    CurrentPermissions,
                    _channel.SessionExpiresAt)), false);

            case "command.start":
                var startResponse = await StartCommandAsync(request, cancellationToken).ConfigureAwait(false);
                return (startResponse, _closeAfterResponse);

            case "command.output":
                return (await ReadOutputAsync(request, cancellationToken).ConfigureAwait(false), false);

            case "command.cancel":
                return (await CancelCommandAsync(request, cancellationToken).ConfigureAwait(false), false);

            case "remote.tools.list":
                return (await ListRemoteToolsAsync(request, cancellationToken).ConfigureAwait(false), false);

            case "remote.tool.call":
                return (StartRemoteTool(request, cancellationToken), false);

            case "remote.tool.cancel":
                return (await CancelRemoteToolAsync(request, cancellationToken).ConfigureAwait(false), false);

            case "remote.tool.receive":
                return (await ReadRemoteToolAsync(request, cancellationToken).ConfigureAwait(false), false);

            case "session.close":
                await SendEnvelopeAsync("session.close", new { }, cancellationToken).ConfigureAwait(false);
                return (Success(request.RequestId, new { closed = true }), true);

            default:
                return (Fail(request.RequestId, "unsupported_request"), false);
        }
    }

    private async Task<BrokerResponse> StartCommandAsync(BrokerRequest brokerRequest, CancellationToken cancellationToken)
    {
        if ((CurrentPermissions & RemotePermission.CommandExecute) == 0)
        {
            return Fail(brokerRequest.RequestId, "permission_denied");
        }

        var input = ReadPayload<StartCommandInput>(brokerRequest.Payload);
        if (string.IsNullOrWhiteSpace(input.CommandText) || input.CommandText.Length > ProtocolConstants.MaximumCommandCharacters ||
            input.TimeoutSeconds is < 1 or > 86_400 || (input.WorkingDirectory?.Length ?? 0) > 4096 || input.WorkingDirectory?.Contains('\0') == true)
        {
            return Fail(brokerRequest.RequestId, "invalid_command");
        }

        PruneCompletedCommands();
        if (_commands.Count >= MaximumRetainedCommands)
        {
            return Fail(brokerRequest.RequestId, "session_command_limit");
        }

        var commandId = Guid.NewGuid();
        var request = new RemoteCommandRequest(
            ProtocolConstants.CurrentVersion,
            _channel.SessionId,
            commandId,
            Interlocked.Increment(ref _commandSequence),
            input.CommandText,
            input.WorkingDirectory,
            input.TimeoutSeconds);
        var buffer = new CommandBuffer();
        if (!_commands.TryAdd(commandId, buffer))
        {
            return Fail(brokerRequest.RequestId, "command_id_collision");
        }

        try
        {
            await SendEnvelopeAsync("command.start", request, cancellationToken).ConfigureAwait(false);
            // Starting is an asynchronous operation: return its ID once the request is put on
            // the authenticated wire. The receive endpoint reports later remote rejection.
            return Success(brokerRequest.RequestId, new StartCommandResult(commandId, true, request.TimeoutSeconds));
        }
        catch (Exception exception) when (exception is TimeoutException or IOException or OperationCanceledException)
        {
            _commands.TryRemove(commandId, out _);
            _closeAfterResponse = true;
            return Fail(brokerRequest.RequestId, "remote_start_send_failed_session_closed");
        }
    }

    private async Task<BrokerResponse> ReadOutputAsync(BrokerRequest request, CancellationToken cancellationToken)
    {
        var query = ReadPayload<CommandOutputQuery>(request.Payload);
        if (query.CommandId == Guid.Empty || query.AfterCursor < 0 || query.WaitSeconds is < 0 or > 30)
        {
            return Fail(request.RequestId, "invalid_output_query");
        }

        if (!_commands.TryGetValue(query.CommandId, out var buffer))
        {
            return Fail(request.RequestId, "unknown_command");
        }

        var result = await buffer.ReadAsync(query.AfterCursor, MaximumPollCharacters, TimeSpan.FromSeconds(query.WaitSeconds), cancellationToken).ConfigureAwait(false);
        return Success(request.RequestId, result);
    }

    private async Task<BrokerResponse> CancelCommandAsync(BrokerRequest request, CancellationToken cancellationToken)
    {
        var input = ReadPayload<CancelCommandInput>(request.Payload);
        if ((CurrentPermissions & RemotePermission.CommandCancel) == 0)
        {
            return Fail(request.RequestId, "permission_denied");
        }

        if (input.CommandId == Guid.Empty || !_commands.ContainsKey(input.CommandId))
        {
            return Fail(request.RequestId, "unknown_command");
        }

        await SendEnvelopeAsync("command.cancel", new RemoteCommandCancelRequest(
            ProtocolConstants.CurrentVersion,
            _channel.SessionId,
            input.CommandId), cancellationToken).ConfigureAwait(false);
        return Success(request.RequestId, new { cancellationRequested = true, input.CommandId });
    }

    private async Task<BrokerResponse> ListRemoteToolsAsync(BrokerRequest brokerRequest, CancellationToken cancellationToken)
    {
        _ = ReadPayload<EmptyBrokerInput>(brokerRequest.Payload);
        var requestId = Guid.NewGuid();
        var remoteRequest = new RemoteToolsListRequest(ProtocolConstants.CurrentVersion, _channel.SessionId, requestId);
        var responseSource = new TaskCompletionSource<RemoteToolsListResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pendingToolLists.TryAdd(requestId, responseSource))
        {
            return Fail(brokerRequest.RequestId, "tool_request_id_collision");
        }

        try
        {
            await SendEnvelopeAsync("remote.tools.list", remoteRequest, cancellationToken).ConfigureAwait(false);
            var response = await responseSource.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            if (!response.Success)
            {
                return Fail(brokerRequest.RequestId, response.ErrorCode ?? "remote_tool_list_rejected");
            }

            if (!ValidateToolCatalog(response))
            {
                return Fail(brokerRequest.RequestId, "invalid_remote_tool_catalog");
            }

            return Success(brokerRequest.RequestId, new RemoteToolsListResult(response.CapabilitiesVersion, response.Tools));
        }
        catch (TimeoutException)
        {
            return Fail(brokerRequest.RequestId, "remote_tool_list_timeout");
        }
        finally
        {
            _pendingToolLists.TryRemove(requestId, out _);
        }
    }

    private BrokerResponse StartRemoteTool(BrokerRequest brokerRequest, CancellationToken cancellationToken)
    {
        if ((CurrentPermissions & RemotePermission.CommandExecute) == 0)
        {
            return Fail(brokerRequest.RequestId, "permission_denied");
        }

        var input = ReadPayload<LocalRemoteToolCallInput>(brokerRequest.Payload);
        if (string.IsNullOrWhiteSpace(input.Name) || input.Name.Length > 128 || input.Name.Any(char.IsControl) ||
            input.Arguments.ValueKind != JsonValueKind.Object ||
            JsonSerializer.SerializeToUtf8Bytes(input.Arguments, ProtocolJson.Options).Length > 16 * 1024)
        {
            return Fail(brokerRequest.RequestId, "invalid_tool_arguments");
        }

        PruneCompletedTools();
        if (_tools.Count >= MaximumRetainedCommands)
        {
            return Fail(brokerRequest.RequestId, "session_tool_limit");
        }

        var operationId = Guid.NewGuid();
        var buffer = new ToolBuffer(operationId);
        if (!_tools.TryAdd(operationId, buffer))
        {
            return Fail(brokerRequest.RequestId, "tool_operation_id_collision");
        }

        var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        if (!_toolCancellationSources.TryAdd(operationId, operationCancellation))
        {
            operationCancellation.Dispose();
            _tools.TryRemove(operationId, out _);
            return Fail(brokerRequest.RequestId, "tool_operation_id_collision");
        }

        var task = DispatchRemoteToolAsync(operationId, input, buffer, operationCancellation.Token);
        _pendingToolDispatches[operationId] = task;
        _ = task.ContinueWith(
            completedTask =>
            {
                _pendingToolDispatches.TryRemove(operationId, out var removedTask);
                if (_toolCancellationSources.TryRemove(operationId, out var finishedCancellation))
                {
                    finishedCancellation.Dispose();
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        return Success(brokerRequest.RequestId, new StartRemoteToolResult(operationId, _channel.SessionId.ToString("D"), RemoteToolOperationState.Running));
    }

    private async Task DispatchRemoteToolAsync(
        Guid operationId,
        LocalRemoteToolCallInput input,
        ToolBuffer buffer,
        CancellationToken cancellationToken)
    {
        try
        {
            var catalogResponse = await RequestRemoteToolsListAsync(cancellationToken).ConfigureAwait(false);
            if (!catalogResponse.Success || !ValidateToolCatalog(catalogResponse))
            {
                buffer.SetResult(new RemoteToolResult(
                    ProtocolConstants.CurrentVersion, _channel.SessionId, operationId, 1,
                    RemoteToolOperationState.Failed, null,
                    catalogResponse.ErrorCode ?? "invalid_remote_tool_catalog",
                    "The remote tool catalog could not be validated."));
                return;
            }

            var descriptor = catalogResponse.Tools.SingleOrDefault(tool => tool.Name == input.Name);
            var validationError = string.Empty;
            if (descriptor is null)
            {
                validationError = "unknown_remote_tool";
            }
            else if (!RemoteToolSchemaValidator.ValidateArguments(descriptor.InputSchema, input.Arguments, out validationError))
            {
                validationError = string.IsNullOrWhiteSpace(validationError) ? "invalid_tool_arguments" : validationError;
            }

            if (descriptor is null || validationError.Length != 0)
            {
                buffer.SetResult(new RemoteToolResult(
                    ProtocolConstants.CurrentVersion, _channel.SessionId, operationId, 1,
                    RemoteToolOperationState.Failed, null,
                    validationError,
                    "The remote tool name or arguments were rejected."));
                return;
            }

            buffer.SetOutputSchema(descriptor.OutputSchema);
            await _toolCallWriter.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var sequence = Interlocked.Increment(ref _toolSequence);
                await SendEnvelopeAsync("remote.tool.call", new RemoteToolCallRequest(
                    ProtocolConstants.CurrentVersion,
                    _channel.SessionId,
                    operationId,
                    sequence,
                    input.Name,
                    input.Arguments), cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _toolCallWriter.Release();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            buffer.SetResult(new RemoteToolResult(
                ProtocolConstants.CurrentVersion, _channel.SessionId, operationId, 1,
                RemoteToolOperationState.Cancelled, null, "cancelled", "The tool request was cancelled."));
        }
        catch (Exception exception) when (exception is IOException or TimeoutException or InvalidDataException or JsonException or InvalidOperationException)
        {
            buffer.SetResult(new RemoteToolResult(
                ProtocolConstants.CurrentVersion, _channel.SessionId, operationId, 1,
                RemoteToolOperationState.Failed, null, "remote_tool_unavailable", "The remote tool request failed."));
        }
    }

    private async Task<RemoteToolsListResponse> RequestRemoteToolsListAsync(CancellationToken cancellationToken)
    {
        var requestId = Guid.NewGuid();
        var source = new TaskCompletionSource<RemoteToolsListResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pendingToolLists.TryAdd(requestId, source))
        {
            throw new InvalidOperationException("tool_request_id_collision");
        }

        try
        {
            await SendEnvelopeAsync("remote.tools.list", new RemoteToolsListRequest(
                ProtocolConstants.CurrentVersion, _channel.SessionId, requestId), cancellationToken).ConfigureAwait(false);
            return await source.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _pendingToolLists.TryRemove(requestId, out _);
        }
    }

    private static bool ValidateToolCatalog(RemoteToolsListResponse response)
    {
        if (response.ProtocolVersion != ProtocolConstants.CurrentVersion || response.SessionId == Guid.Empty ||
            response.RequestId == Guid.Empty || string.IsNullOrWhiteSpace(response.CapabilitiesVersion) ||
            response.CapabilitiesVersion.Length > 64 || response.Tools is null || response.Tools.Count > 64)
        {
            return false;
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tool in response.Tools)
        {
            if (string.IsNullOrWhiteSpace(tool.Name) || tool.Name.Length > 128 || tool.Name.Any(char.IsControl) ||
                string.IsNullOrWhiteSpace(tool.Version) || tool.Version.Length > 64 ||
                tool.Description is null || tool.Description.Length > 2048 || !names.Add(tool.Name) ||
                !RemoteToolSchemaValidator.ValidateSchema(tool.InputSchema, out _))
            {
                return false;
            }

            if (tool.OutputSchema is { } outputSchema && !RemoteToolSchemaValidator.ValidateSchema(outputSchema, out _))
            {
                return false;
            }
        }

        return true;
    }

    private async Task<BrokerResponse> ReadRemoteToolAsync(BrokerRequest request, CancellationToken cancellationToken)
    {
        var query = ReadPayload<LocalRemoteToolReceiveInput>(request.Payload);
        if (query.OperationId == Guid.Empty || query.Cursor < 0 || query.WaitMilliseconds is < 0 or > 30_000)
        {
            return Fail(request.RequestId, "invalid_tool_receive_query");
        }

        if (!_tools.TryGetValue(query.OperationId, out var buffer))
        {
            return Fail(request.RequestId, "unknown_tool_operation");
        }

        var result = await buffer.ReadAsync(query.Cursor, TimeSpan.FromMilliseconds(query.WaitMilliseconds), cancellationToken).ConfigureAwait(false);
        return Success(request.RequestId, result);
    }

    private async Task<BrokerResponse> CancelRemoteToolAsync(BrokerRequest request, CancellationToken cancellationToken)
    {
        if ((CurrentPermissions & RemotePermission.CommandCancel) == 0)
        {
            return Fail(request.RequestId, "permission_denied");
        }

        var input = ReadPayload<LocalRemoteToolCancelInput>(request.Payload);
        if (input.OperationId == Guid.Empty || !_tools.TryGetValue(input.OperationId, out var buffer))
        {
            return Fail(request.RequestId, "unknown_tool_operation");
        }

        if (buffer.IsComplete)
        {
            return Fail(request.RequestId, "tool_operation_complete");
        }

        if (_toolCancellationSources.TryGetValue(input.OperationId, out var localCancellation))
        {
            try
            {
                localCancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        await SendEnvelopeAsync("remote.tool.cancel", new RemoteToolCancelRequest(
            ProtocolConstants.CurrentVersion,
            _channel.SessionId,
            input.OperationId), cancellationToken).ConfigureAwait(false);
        return Success(request.RequestId, new LocalRemoteToolCancelResult(input.OperationId, true));
    }

    private void PruneCompletedTools()
    {
        foreach (var item in _tools.OrderBy(pair => pair.Key).Take(Math.Max(0, _tools.Count - MaximumRetainedCommands + 1)))
        {
            if (item.Value.IsComplete)
            {
                _tools.TryRemove(item.Key, out _);
            }
        }
    }

    private async Task ReadRemoteAsync(CancellationToken cancellationToken)
    {
        long lastRemoteSequence = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            var envelope = await FramedJsonStream.ReadAsync<ProtocolEnvelope>(_channel.Stream, cancellationToken).ConfigureAwait(false);
            if (envelope is null)
            {
                break;
            }

            if (envelope.ProtocolVersion != ProtocolConstants.CurrentVersion || envelope.SessionId != _channel.SessionId ||
                envelope.Sequence != lastRemoteSequence + 1)
            {
                throw new InvalidDataException("Remote frame did not match the authenticated session or sequence.");
            }

            lastRemoteSequence = envelope.Sequence;

            switch (envelope.Type)
            {
                case "session.policy":
                    var policy = ReadPayload<RemoteSessionPolicy>(envelope.Payload);
                    const RemotePermission all = RemotePermission.SessionRead | RemotePermission.CommandExecute | RemotePermission.CommandCancel;
                    if (policy.Version <= 0 || (policy.Permissions & ~all) != 0 ||
                        policy.WorkspaceRoot is { Length: > 4096 })
                        throw new InvalidDataException("Invalid or replayed remote policy.");
                    if (policy.Version <= _policyVersion) break;
                    _policyVersion = policy.Version;
                    Volatile.Write(ref _currentPermissions, (int)policy.Permissions);
                    PolicyChanged?.Invoke(policy);
                    break;
                case "session.pong":
                    Interlocked.Exchange(ref _lastPongTicks, DateTimeOffset.UtcNow.UtcTicks);
                    break;
                case "command.accepted":
                    var accepted = ReadPayload<RemoteCommandAccepted>(envelope.Payload);
                    if (accepted.ProtocolVersion == ProtocolConstants.CurrentVersion && accepted.SessionId == _channel.SessionId &&
                        accepted.CommandId != Guid.Empty && _commands.TryGetValue(accepted.CommandId, out var acceptedBuffer) && !accepted.Accepted)
                    {
                        acceptedBuffer.SetResult(new RemoteCommandResult(
                            ProtocolConstants.CurrentVersion,
                            _channel.SessionId,
                            accepted.CommandId,
                            RemoteCommandState.Failed,
                            null,
                            accepted.ErrorCode ?? "remote_rejected_command"));
                    }

                    break;
                case "command.output":
                    var output = ReadPayload<RemoteCommandOutput>(envelope.Payload);
                    if (output.ProtocolVersion == ProtocolConstants.CurrentVersion && output.SessionId == _channel.SessionId &&
                        output.CommandId != Guid.Empty && output.Cursor > 0 && _commands.TryGetValue(output.CommandId, out var outputBuffer))
                    {
                        StoreOutput(outputBuffer, output);
                    }

                    break;
                case "command.result":
                    var result = ReadPayload<RemoteCommandResult>(envelope.Payload);
                    if (result.ProtocolVersion == ProtocolConstants.CurrentVersion && result.SessionId == _channel.SessionId &&
                        result.CommandId != Guid.Empty && _commands.TryGetValue(result.CommandId, out var resultBuffer))
                    {
                        resultBuffer.SetResult(result);
                    }

                    break;
                case "remote.tools.list.result":
                    var toolsResponse = ReadPayload<RemoteToolsListResponse>(envelope.Payload);
                    if (toolsResponse.ProtocolVersion == ProtocolConstants.CurrentVersion &&
                        toolsResponse.SessionId == _channel.SessionId && toolsResponse.RequestId != Guid.Empty &&
                        _pendingToolLists.TryGetValue(toolsResponse.RequestId, out var pendingTools))
                    {
                        pendingTools.TrySetResult(toolsResponse);
                    }

                    break;
                case "remote.tool.accepted":
                    var toolAccepted = ReadPayload<RemoteToolCallAccepted>(envelope.Payload);
                    if (toolAccepted.ProtocolVersion == ProtocolConstants.CurrentVersion &&
                        toolAccepted.SessionId == _channel.SessionId && toolAccepted.OperationId != Guid.Empty &&
                        !toolAccepted.Accepted && _tools.TryGetValue(toolAccepted.OperationId, out var rejectedTool))
                    {
                        rejectedTool.SetResult(new RemoteToolResult(
                            ProtocolConstants.CurrentVersion,
                            _channel.SessionId,
                            toolAccepted.OperationId,
                            1,
                            RemoteToolOperationState.Failed,
                            null,
                            toolAccepted.ErrorCode ?? "remote_tool_rejected",
                            "The remote host rejected the tool request."));
                    }

                    break;
                case "remote.tool.result":
                    var toolResult = ReadPayload<RemoteToolResult>(envelope.Payload);
                    if (toolResult.ProtocolVersion == ProtocolConstants.CurrentVersion &&
                        toolResult.SessionId == _channel.SessionId && toolResult.OperationId != Guid.Empty &&
                        toolResult.Cursor == 1 &&
                        (toolResult.ErrorCode is null || toolResult.ErrorCode.Length <= 64) &&
                        (toolResult.ErrorMessage is null || toolResult.ErrorMessage.Length <= 1024) &&
                        _tools.TryGetValue(toolResult.OperationId, out var toolBuffer))
                    {
                        var resultError = toolBuffer.ValidateResult(toolResult);
                        if (resultError is not null)
                        {
                            toolBuffer.SetResult(new RemoteToolResult(
                                ProtocolConstants.CurrentVersion,
                                _channel.SessionId,
                                toolResult.OperationId,
                                1,
                                RemoteToolOperationState.Failed,
                                null,
                                resultError,
                                "The remote tool result did not match its bounded output contract."));
                        }
                        else
                        {
                            toolBuffer.SetResult(toolResult);
                        }
                    }

                    break;
                default:
                    throw new InvalidDataException("Unsupported remote response frame.");
            }
        }

        _stop.Cancel();
    }

    private async Task SendHeartbeatsAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(20));
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            if (DateTimeOffset.UtcNow.UtcTicks - Interlocked.Read(ref _lastPongTicks) > TimeSpan.FromSeconds(65).Ticks)
                throw new IOException("The encrypted peer stopped answering heartbeats.");
            await SendEnvelopeAsync("session.ping", new { }, cancellationToken).ConfigureAwait(false);
        }
    }

    private void StoreOutput(CommandBuffer command, RemoteCommandOutput output)
    {
        lock (_outputBudgetLock)
        {
            if (_outputCharacters + output.Text.Length > MaximumSessionOutputCharacters)
            {
                command.MarkTruncated();
                return;
            }

            _outputCharacters += output.Text.Length;
            command.AddOutput(output);
        }
    }

    private void PruneCompletedCommands()
    {
        foreach (var item in _commands.OrderBy(pair => pair.Key).Take(Math.Max(0, _commands.Count - MaximumRetainedCommands + 1)))
        {
            if (item.Value.IsComplete)
            {
                _commands.TryRemove(item.Key, out _);
            }
        }
    }

    private Task SendEnvelopeAsync<T>(string type, T payload, CancellationToken cancellationToken)
    {
        return SendEnvelopeOrderedAsync(type, payload, cancellationToken);
    }

    private async Task SendEnvelopeOrderedAsync<T>(string type, T payload, CancellationToken cancellationToken)
    {
        await _wireSendOrder.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var envelope = new ProtocolEnvelope(
                ProtocolConstants.CurrentVersion,
                type,
                _channel.SessionId,
                Interlocked.Increment(ref _wireSequence),
                JsonSerializer.SerializeToElement(payload, ProtocolJson.Options));
            await FramedJsonStream.WriteAsync(_channel.Stream, envelope, _wireWriter, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _wireSendOrder.Release();
        }
    }

    private static T ReadPayload<T>(JsonElement payload) =>
        payload.Deserialize<T>(ProtocolJson.Options) ?? throw new InvalidDataException("Protocol payload was empty.");

    private static BrokerResponse Success<T>(Guid requestId, T payload) => new(
        requestId,
        true,
        JsonSerializer.SerializeToElement(payload, ProtocolJson.Options),
        null);

    private static BrokerResponse Fail(Guid requestId, string error) => new(
        requestId,
        false,
        JsonSerializer.SerializeToElement(new { }, ProtocolJson.Options),
        error);

    public static string PipeNameForProcess(int processId) => PipeName + ".pid-" + processId;

    internal static string PipeName
    {
        get
        {
            var user = Environment.UserDomainName + "\\" + Environment.UserName;
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(user));
            try
            {
                return "RemoteCli." + Convert.ToHexString(hash.AsSpan(0, 12));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(hash);
            }
        }
    }

    private sealed class CommandBuffer
    {
        private readonly object _sync = new();
        private readonly List<RemoteCommandOutput> _outputs = [];
        private TaskCompletionSource _changed = NewSignal();
        private RemoteCommandResult? _result;
        private bool _truncated;

        public bool IsComplete
        {
            get
            {
                lock (_sync)
                {
                    return _result is not null;
                }
            }
        }

        public void AddOutput(RemoteCommandOutput output)
        {
            lock (_sync)
            {
                _outputs.Add(output);
                SignalChanged();
            }
        }

        public void MarkTruncated()
        {
            lock (_sync)
            {
                _truncated = true;
                SignalChanged();
            }
        }

        public void SetResult(RemoteCommandResult result)
        {
            lock (_sync)
            {
                _result = result;
                SignalChanged();
            }
        }

        public async Task<CommandPollResult> ReadAsync(long afterCursor, int maximumCharacters, TimeSpan wait, CancellationToken cancellationToken)
        {
            var deadline = DateTimeOffset.UtcNow + wait;
            while (true)
            {
                Task changed;
                CommandPollResult? snapshot;
                lock (_sync)
                {
                    var selected = new List<RemoteCommandOutput>();
                    var length = 0;
                    foreach (var output in _outputs)
                    {
                        if (output.Cursor <= afterCursor)
                        {
                            continue;
                        }

                        if (selected.Count > 0 && length + output.Text.Length > maximumCharacters)
                        {
                            break;
                        }

                        selected.Add(output);
                        length += output.Text.Length;
                        if (length >= maximumCharacters)
                        {
                            break;
                        }
                    }

                    var nextCursor = selected.Count == 0 ? afterCursor : selected[^1].Cursor;
                    snapshot = selected.Count > 0 || _result is not null || wait == TimeSpan.Zero
                        ? new CommandPollResult(selected, nextCursor, _result, _truncated)
                        : null;
                    if (snapshot is not null)
                    {
                        return snapshot;
                    }

                    changed = _changed.Task;
                }

                var remaining = deadline - DateTimeOffset.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    return new CommandPollResult([], afterCursor, null, _truncated);
                }

                try
                {
                    await changed.WaitAsync(remaining, cancellationToken).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    return new CommandPollResult([], afterCursor, null, _truncated);
                }
            }
        }

        private void SignalChanged()
        {
            var previous = _changed;
            _changed = NewSignal();
            previous.TrySetResult();
        }

        private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class ToolBuffer(Guid operationId)
    {
        private readonly object _sync = new();
        private TaskCompletionSource _changed = NewSignal();
        private RemoteToolResult? _result;
        private JsonElement? _outputSchema;

        public bool IsComplete
        {
            get
            {
                lock (_sync)
                {
                    return _result is not null;
                }
            }
        }

        public void SetOutputSchema(JsonElement? outputSchema)
        {
            lock (_sync)
            {
                _outputSchema = outputSchema?.Clone();
            }
        }

        public string? ValidateResult(RemoteToolResult result)
        {
            JsonElement? outputSchema;
            lock (_sync)
            {
                outputSchema = _outputSchema;
            }

            if (!Enum.IsDefined(result.State) || result.State == RemoteToolOperationState.Running)
            {
                return "invalid_tool_result_state";
            }

            if (result.Result is { } payload)
            {
                if (JsonSerializer.SerializeToUtf8Bytes(payload, ProtocolJson.Options).Length > 16 * 1024)
                {
                    return "tool_result_too_large";
                }

                if (outputSchema is { } schema && !RemoteToolSchemaValidator.ValidateArguments(schema, payload, out _))
                {
                    return "tool_result_schema_mismatch";
                }
            }
            else if (result.State == RemoteToolOperationState.Succeeded)
            {
                return "empty_tool_result";
            }

            return null;
        }

        public void SetResult(RemoteToolResult result)
        {
            lock (_sync)
            {
                if (result.OperationId != operationId || _result is not null)
                {
                    return;
                }

                _result = result;
                var previous = _changed;
                _changed = NewSignal();
                previous.TrySetResult();
            }
        }

        public async Task<RemoteToolPollResult> ReadAsync(long afterCursor, TimeSpan wait, CancellationToken cancellationToken)
        {
            var deadline = DateTimeOffset.UtcNow + wait;
            while (true)
            {
                Task changed;
                lock (_sync)
                {
                    if (_result is not null)
                    {
                        var includeResult = afterCursor < _result.Cursor;
                        return new RemoteToolPollResult(
                            operationId,
                            includeResult ? _result.Cursor : afterCursor,
                            true,
                            _result.State,
                            includeResult ? _result.Result : null,
                            _result.ErrorCode,
                            _result.ErrorMessage);
                    }

                    if (wait == TimeSpan.Zero)
                    {
                        return new RemoteToolPollResult(operationId, afterCursor, false, RemoteToolOperationState.Running, null, null, null);
                    }

                    changed = _changed.Task;
                }

                var remaining = deadline - DateTimeOffset.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    return new RemoteToolPollResult(operationId, afterCursor, false, RemoteToolOperationState.Running, null, null, null);
                }

                try
                {
                    await changed.WaitAsync(remaining, cancellationToken).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    return new RemoteToolPollResult(operationId, afterCursor, false, RemoteToolOperationState.Running, null, null, null);
                }
            }
        }

        private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

public sealed class LocalSessionBrokerClient
{
    private readonly string _pipeName;

    public LocalSessionBrokerClient(string? pipeName = null)
    {
        _pipeName = pipeName ?? LocalSessionBroker.PipeName;
    }

    internal async Task<T> SendAsync<TInput, T>(string type, TInput input, CancellationToken cancellationToken)
    {
        var result = await TrySendAsync<TInput, T>(type, input, cancellationToken).ConfigureAwait(false);
        return result ?? throw new IOException("No paired remote session is available.");
    }

    private async Task<T?> TrySendAsync<TInput, T>(string type, TInput input, CancellationToken cancellationToken)
    {
        using var pipe = new NamedPipeClientStream(
            ".",
            _pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await pipe.ConnectAsync(1000, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return default;
        }
        catch (IOException)
        {
            return default;
        }

        var requestId = Guid.NewGuid();
        var request = new BrokerRequest(requestId, type, JsonSerializer.SerializeToElement(input, ProtocolJson.Options));
        using var writeLock = new SemaphoreSlim(1, 1);
        try
        {
            await FramedJsonStream.WriteAsync(pipe, request, writeLock, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException exception)
        {
            throw new IOException("local_broker_write_failed", exception);
        }

        BrokerResponse? response;
        try
        {
            response = await FramedJsonStream.ReadAsync<BrokerResponse>(pipe, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException exception)
        {
            throw new IOException("local_broker_read_failed", exception);
        }

        if (response is null || response.RequestId != requestId)
        {
            throw new IOException("local_broker_invalid_response");
        }

        if (!response.Success)
        {
            throw new InvalidOperationException(response.Error ?? "local_broker_request_failed");
        }

        return response.Payload.Deserialize<T>(ProtocolJson.Options)
            ?? throw new InvalidDataException("Local broker returned an empty response.");
    }
}
