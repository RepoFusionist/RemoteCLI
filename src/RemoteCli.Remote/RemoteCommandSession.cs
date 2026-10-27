using System.Collections.Concurrent;
using System.Text.Json;
using Common.Protocol;
using Common.Security;

namespace RemoteCli.Remote;

public static class RemoteCommandSession
{
    public static async Task RunAsync(
        SecurePeerChannel channel,
        CancellationToken cancellationToken,
        IRemoteToolRegistry? toolRegistry = null,
        RemoteAccessController? access = null)
    {
        toolRegistry ??= RemoteToolRegistry.Default;
        var authorizer = new SessionCommandAuthorizer(channel.SessionId, channel.ApprovedPermissions, channel.SessionExpiresAt);
        using var sessionStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var writer = new SemaphoreSlim(1, 1);
        using var sendOrder = new SemaphoreSlim(1, 1);
        var activeCommands = new ConcurrentDictionary<Guid, CancellationTokenSource>();
        var activeTools = new ConcurrentDictionary<Guid, CancellationTokenSource>();
        var runner = new PowerShellCommandRunner();
        var pendingTasks = new ConcurrentDictionary<Guid, Task>();
        var pendingToolTasks = new ConcurrentDictionary<Guid, Task>();
        var toolAuthorizer = new RemoteToolCallAuthorizer(channel.SessionId, channel.ApprovedPermissions, channel.SessionExpiresAt);
        long responseSequence = 0;
        long lastRemoteSequence = 0;

        void OnPolicyChanged(RemoteSessionPolicy policy)
        {
            authorizer.UpdatePermissions(policy.Permissions);
            toolAuthorizer.UpdatePermissions(policy.Permissions);
            foreach (var active in activeCommands.Values.Concat(activeTools.Values))
                try { active.Cancel(); } catch (ObjectDisposedException) { }
            _ = PublishPolicyAsync(policy);
        }

        async Task PublishPolicyAsync(RemoteSessionPolicy policy)
        {
            try { await SendAsync("session.policy", policy, sessionStop.Token).ConfigureAwait(false); }
            catch (Exception exception) when (exception is IOException or OperationCanceledException or ObjectDisposedException)
            { sessionStop.Cancel(); }
        }

        async Task SendAsync<T>(string type, T payload, CancellationToken token)
        {
            await sendOrder.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var envelope = new ProtocolEnvelope(
                    ProtocolConstants.CurrentVersion,
                    type,
                    channel.SessionId,
                    Interlocked.Increment(ref responseSequence),
                    JsonSerializer.SerializeToElement(payload, ProtocolJson.Options));
                await FramedJsonStream.WriteAsync(channel.Stream, envelope, writer, token).ConfigureAwait(false);
            }
            finally
            {
                sendOrder.Release();
            }
        }

        async Task ExecuteCommandAsync(RemoteCommandRequest request, CancellationTokenSource commandCancellation)
        {
            var lease = access?.Acquire();
            using var policyCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                commandCancellation.Token, lease?.Revoked ?? CancellationToken.None);
            long cursor = 0;
            try
            {
                var result = await runner.ExecuteAsync(
                    request.CommandText,
                    request.WorkingDirectory,
                    TimeSpan.FromSeconds(request.TimeoutSeconds),
                    async (stream, text) =>
                    {
                        var output = new RemoteCommandOutput(
                            ProtocolConstants.CurrentVersion,
                            channel.SessionId,
                            request.CommandId,
                            Interlocked.Increment(ref cursor),
                            DateTimeOffset.UtcNow,
                            stream,
                            text);
                        await SendAsync("command.output", output, sessionStop.Token).ConfigureAwait(false);
                    },
                    policyCancellation.Token, lease?.Policy).ConfigureAwait(false);

                await SendAsync("command.result", new RemoteCommandResult(
                    ProtocolConstants.CurrentVersion,
                    channel.SessionId,
                    request.CommandId,
                    result.State,
                    result.ExitCode,
                    result.ErrorCode)
                {
                    PowerShellPath = result.PowerShellPath,
                    PowerShellVersion = result.PowerShellVersion
                }, sessionStop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (sessionStop.IsCancellationRequested)
            {
            }
            catch (OperationCanceledException) when (policyCancellation.IsCancellationRequested)
            {
                await SendAsync("command.result", new RemoteCommandResult(ProtocolConstants.CurrentVersion,
                    channel.SessionId, request.CommandId, RemoteCommandState.Cancelled, null, "policy_changed_or_cancelled"),
                    sessionStop.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException or ArgumentException)
            {
                try
                {
                    await SendAsync("command.result", new RemoteCommandResult(
                        ProtocolConstants.CurrentVersion,
                        channel.SessionId,
                        request.CommandId,
                        RemoteCommandState.Failed,
                        null,
                        "remote_execution_failed"), sessionStop.Token).ConfigureAwait(false);
                }
                catch (Exception sendException) when (sendException is IOException or OperationCanceledException or ObjectDisposedException)
                {
                }
            }
            finally
            {
                activeCommands.TryRemove(request.CommandId, out _);
                commandCancellation.Dispose();
                pendingTasks.TryRemove(request.CommandId, out _);
            }
        }

        async Task ExecuteRemoteToolAsync(RemoteToolCallRequest request, CancellationTokenSource toolCancellation)
        {
            try
            {
                JsonElement result;
                try
                {
                    // Synchronous filesystem/plugin implementations must not block the wire reader or UI thread.
                    result = await Task.Run(() => toolRegistry.ExecuteAsync(request.Name, request.Arguments, toolCancellation.Token), toolCancellation.Token)
                        .WaitAsync(TimeSpan.FromMinutes(2), toolCancellation.Token)
                        .ConfigureAwait(false);
                    if (JsonSerializer.SerializeToUtf8Bytes(result, ProtocolJson.Options).Length > 16 * 1024)
                    {
                        throw new InvalidDataException("Remote tool result exceeded its size limit.");
                    }
                }
                catch (TimeoutException)
                {
                    toolCancellation.Cancel();
                    await SendAsync("remote.tool.result", new RemoteToolResult(
                        ProtocolConstants.CurrentVersion,
                        channel.SessionId,
                        request.OperationId,
                        1,
                        RemoteToolOperationState.Failed,
                        null,
                        "remote_tool_timeout",
                        "The remote tool exceeded its execution deadline."), sessionStop.Token).ConfigureAwait(false);
                    return;
                }
                catch (RemoteToolException exception)
                {
                    await SendAsync("remote.tool.result", new RemoteToolResult(
                        ProtocolConstants.CurrentVersion,
                        channel.SessionId,
                        request.OperationId,
                        1,
                        RemoteToolOperationState.Failed,
                        null,
                        exception.Code,
                        exception.Message), sessionStop.Token).ConfigureAwait(false);
                    return;
                }

                await SendAsync("remote.tool.result", new RemoteToolResult(
                    ProtocolConstants.CurrentVersion,
                    channel.SessionId,
                    request.OperationId,
                    1,
                    RemoteToolOperationState.Succeeded,
                    result,
                    null,
                    null), sessionStop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (toolCancellation.IsCancellationRequested || sessionStop.IsCancellationRequested)
            {
                if (!sessionStop.IsCancellationRequested)
                {
                    try
                    {
                        await SendAsync("remote.tool.result", new RemoteToolResult(
                            ProtocolConstants.CurrentVersion,
                            channel.SessionId,
                            request.OperationId,
                            1,
                            RemoteToolOperationState.Cancelled,
                            null,
                            "cancelled",
                            "The remote tool operation was cancelled."), sessionStop.Token).ConfigureAwait(false);
                    }
                    catch (Exception sendException) when (sendException is IOException or OperationCanceledException or ObjectDisposedException)
                    {
                    }
                }
            }
            catch (Exception)
            {
                try
                {
                    await SendAsync("remote.tool.result", new RemoteToolResult(
                        ProtocolConstants.CurrentVersion,
                        channel.SessionId,
                        request.OperationId,
                        1,
                        RemoteToolOperationState.Failed,
                        null,
                        "remote_tool_failed",
                        "The remote tool could not complete the request."), sessionStop.Token).ConfigureAwait(false);
                }
                catch (Exception sendException) when (sendException is IOException or OperationCanceledException or ObjectDisposedException)
                {
                }
            }
            finally
            {
                activeTools.TryRemove(request.OperationId, out _);
                toolCancellation.Dispose();
                pendingToolTasks.TryRemove(request.OperationId, out _);
            }
        }

        try
        {
            if (access is not null)
            {
                access.Changed += OnPolicyChanged;
                var initialPolicy = access.Snapshot;
                authorizer.UpdatePermissions(initialPolicy.Permissions);
                toolAuthorizer.UpdatePermissions(initialPolicy.Permissions);
                await SendAsync("session.policy", initialPolicy, sessionStop.Token).ConfigureAwait(false);
            }
            while (!sessionStop.IsCancellationRequested)
            {
                var envelope = await FramedJsonStream.ReadAsync<ProtocolEnvelope>(channel.Stream, sessionStop.Token).ConfigureAwait(false);
                if (envelope is null)
                {
                    break;
                }

                if (envelope.ProtocolVersion != ProtocolConstants.CurrentVersion || envelope.SessionId != channel.SessionId ||
                    envelope.Sequence != lastRemoteSequence + 1)
                {
                    throw new InvalidDataException("Remote command frame did not match the session or sequence.");
                }

                lastRemoteSequence = envelope.Sequence;

                if (envelope.Type == "session.close")
                {
                    break;
                }

                if (envelope.Type == "session.ping")
                {
                    await SendAsync("session.pong", new { }, sessionStop.Token).ConfigureAwait(false);
                    continue;
                }

                if (envelope.Type == "command.start")
                {
                    var request = envelope.Payload.Deserialize<RemoteCommandRequest>(ProtocolJson.Options)
                        ?? throw new InvalidDataException("Remote command request was empty.");
                    var accepted = authorizer.TryAuthorizeCommand(request, DateTimeOffset.UtcNow, out var errorCode);
                    await SendAsync("command.accepted", new RemoteCommandAccepted(
                        ProtocolConstants.CurrentVersion,
                        channel.SessionId,
                        request.CommandId,
                        accepted,
                        accepted ? null : errorCode), sessionStop.Token).ConfigureAwait(false);
                    if (!accepted)
                    {
                        continue;
                    }

                    var commandCancellation = CancellationTokenSource.CreateLinkedTokenSource(sessionStop.Token);
                    if (!activeCommands.TryAdd(request.CommandId, commandCancellation))
                    {
                        commandCancellation.Dispose();
                        continue;
                    }

                    var task = ExecuteCommandAsync(request, commandCancellation);
                    pendingTasks[request.CommandId] = task;
                    _ = task.ContinueWith(
                        _ =>
                        {
                            pendingTasks.TryRemove(request.CommandId, out var removedTask);
                        },
                        CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);
                    continue;
                }

                if (envelope.Type == "remote.tools.list")
                {
                    var request = envelope.Payload.Deserialize<RemoteToolsListRequest>(ProtocolJson.Options)
                        ?? throw new InvalidDataException("Remote tools list request was empty.");
                    var registryTools = toolRegistry.ListTools();
                    var catalogValid = registryTools.Count <= 64 && registryTools.All(tool =>
                        !string.IsNullOrWhiteSpace(tool.Name) && tool.Name.Length <= 128 && !tool.Name.Any(char.IsControl) &&
                        !string.IsNullOrWhiteSpace(tool.Version) && tool.Version.Length <= 64 &&
                        tool.Description is not null && tool.Description.Length <= 2048 &&
                        RemoteToolSchemaValidator.ValidateSchema(tool.InputSchema, out _) &&
                        (tool.OutputSchema is null || RemoteToolSchemaValidator.ValidateSchema(tool.OutputSchema.Value, out _)));
                    var valid = request.ProtocolVersion == ProtocolConstants.CurrentVersion &&
                        request.SessionId == channel.SessionId && request.RequestId != Guid.Empty &&
                        (authorizer.CanReadStatus(request.SessionId, DateTimeOffset.UtcNow) ||
                         authorizer.CanExecute(request.SessionId, DateTimeOffset.UtcNow)) && catalogValid;
                    await SendAsync("remote.tools.list.result", new RemoteToolsListResponse(
                        ProtocolConstants.CurrentVersion,
                        channel.SessionId,
                        request.RequestId,
                        valid,
                        valid ? toolRegistry.CapabilitiesVersion : string.Empty,
                        valid ? registryTools : [],
                        valid ? null : catalogValid ? "permission_denied_or_session_unavailable" : "invalid_remote_tool_catalog"), sessionStop.Token).ConfigureAwait(false);
                    continue;
                }

                if (envelope.Type == "remote.tool.call")
                {
                    var request = envelope.Payload.Deserialize<RemoteToolCallRequest>(ProtocolJson.Options)
                        ?? throw new InvalidDataException("Remote tool call request was empty.");
                    if (!toolAuthorizer.TryAuthorize(request, toolRegistry, DateTimeOffset.UtcNow, out var errorCode))
                    {
                        await SendAsync("remote.tool.accepted", new RemoteToolCallAccepted(
                            ProtocolConstants.CurrentVersion,
                            channel.SessionId,
                            request.OperationId,
                            false,
                            errorCode), sessionStop.Token).ConfigureAwait(false);
                        continue;
                    }

                    var toolCancellation = CancellationTokenSource.CreateLinkedTokenSource(sessionStop.Token);
                    if (!activeTools.TryAdd(request.OperationId, toolCancellation))
                    {
                        toolCancellation.Dispose();
                        await SendAsync("remote.tool.accepted", new RemoteToolCallAccepted(
                            ProtocolConstants.CurrentVersion,
                            channel.SessionId,
                            request.OperationId,
                            false,
                            "duplicate_operation"), sessionStop.Token).ConfigureAwait(false);
                        continue;
                    }

                    await SendAsync("remote.tool.accepted", new RemoteToolCallAccepted(
                        ProtocolConstants.CurrentVersion,
                        channel.SessionId,
                        request.OperationId,
                        true,
                        null), sessionStop.Token).ConfigureAwait(false);
                    var task = ExecuteRemoteToolAsync(request, toolCancellation);
                    pendingToolTasks[request.OperationId] = task;
                    _ = task.ContinueWith(
                        completedTask =>
                        {
                            pendingToolTasks.TryRemove(request.OperationId, out var removedTask);
                        },
                        CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);
                    continue;
                }

                if (envelope.Type == "remote.tool.cancel")
                {
                    var request = envelope.Payload.Deserialize<RemoteToolCancelRequest>(ProtocolJson.Options)
                        ?? throw new InvalidDataException("Remote tool cancellation request was empty.");
                    if (request.ProtocolVersion == ProtocolConstants.CurrentVersion &&
                        request.SessionId == channel.SessionId &&
                        request.OperationId != Guid.Empty &&
                        authorizer.CanCancel(request.SessionId, DateTimeOffset.UtcNow) &&
                        activeTools.TryGetValue(request.OperationId, out var toolCancellation))
                    {
                        try
                        {
                            toolCancellation.Cancel();
                        }
                        catch (ObjectDisposedException)
                        {
                        }
                    }

                    continue;
                }

                if (envelope.Type == "command.cancel")
                {
                    var request = envelope.Payload.Deserialize<RemoteCommandCancelRequest>(ProtocolJson.Options)
                        ?? throw new InvalidDataException("Remote command cancellation request was empty.");
                    if (request.ProtocolVersion == ProtocolConstants.CurrentVersion &&
                        request.SessionId == channel.SessionId &&
                        request.CommandId != Guid.Empty &&
                        authorizer.CanCancel(request.SessionId, DateTimeOffset.UtcNow) &&
                        activeCommands.TryGetValue(request.CommandId, out var commandCancellation))
                    {
                        commandCancellation.Cancel();
                    }

                    continue;
                }

                throw new InvalidDataException("Unsupported remote command frame.");
            }
        }
        finally
        {
            if (access is not null) access.Changed -= OnPolicyChanged;
            authorizer.Close();
            toolAuthorizer.Close();
            sessionStop.Cancel();
            foreach (var active in activeCommands.Values)
            {
                try
                {
                    active.Cancel();
                }
                catch (ObjectDisposedException)
                {
                }
            }

            foreach (var active in activeTools.Values)
            {
                try
                {
                    active.Cancel();
                }
                catch (ObjectDisposedException)
                {
                }
            }

            try
            {
                await Task.WhenAll(pendingTasks.Values.Concat(pendingToolTasks.Values))
                    .WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is OperationCanceledException or TimeoutException or IOException)
            {
            }
        }
    }
}
