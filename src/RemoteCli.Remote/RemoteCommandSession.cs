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
        RemoteAccessController? access = null,
        Action<Func<ControllerModelInput, CancellationToken, Task<string>>?>? controllerModelRequestHandlerChanged = null,
        Action<ControllerModelCatalogResult>? controllerModelCatalogChanged = null)
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
        var pendingControllerModelRequests = new ConcurrentDictionary<Guid, TaskCompletionSource<ControllerModelResult>>();
        using var controllerModelRequestGate = new SemaphoreSlim(1, 1);
        var pendingControllerModelCatalogRequests = new ConcurrentDictionary<Guid, TaskCompletionSource<ControllerModelCatalogResult>>();
        using var controllerModelCatalogGate = new SemaphoreSlim(1, 1);
        var toolAuthorizer = new RemoteToolCallAuthorizer(channel.SessionId, channel.ApprovedPermissions, channel.SessionExpiresAt);
        var currentPermissions = (int)channel.ApprovedPermissions;
        long responseSequence = 0;
        long lastRemoteSequence = 0;

        void OnPolicyChanged(RemoteSessionPolicy policy)
        {
            var previousPermissions = (RemotePermission)Volatile.Read(ref currentPermissions);
            var effectivePolicy = policy with { Permissions = policy.Permissions & channel.ApprovedPermissions };
            Volatile.Write(ref currentPermissions, (int)effectivePolicy.Permissions);
            authorizer.UpdatePermissions(effectivePolicy.Permissions);
            toolAuthorizer.UpdatePermissions(effectivePolicy.Permissions);
            foreach (var active in activeCommands.Values.Concat(activeTools.Values))
                try { active.Cancel(); } catch (ObjectDisposedException) { }
            if ((effectivePolicy.Permissions & RemotePermission.ControllerModelUse) == 0)
            {
                foreach (var pending in pendingControllerModelRequests.Values)
                    pending.TrySetException(new UnauthorizedAccessException("controller_model_permission_denied"));
                foreach (var pending in pendingControllerModelCatalogRequests.Values)
                    pending.TrySetException(new UnauthorizedAccessException("controller_model_permission_denied"));
                PublishControllerModelCatalog(new ControllerModelCatalogResult(
                    ProtocolConstants.CurrentVersion,
                    channel.SessionId,
                    Guid.Empty,
                    false,
                    [],
                    null,
                    null,
                    "permission_denied"));
            }
            else if ((previousPermissions & RemotePermission.ControllerModelUse) == 0)
            {
                _ = RefreshControllerModelCatalogAsync(sessionStop.Token);
            }
            _ = PublishPolicyAsync(effectivePolicy);
        }

        void PublishControllerModelCatalog(ControllerModelCatalogResult result)
        {
            try { controllerModelCatalogChanged?.Invoke(result); }
            catch { }
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

        async Task<string> RequestControllerModelAsync(ControllerModelInput input, CancellationToken requestCancellation)
        {
            using var linkedRequest = CancellationTokenSource.CreateLinkedTokenSource(requestCancellation, sessionStop.Token);
            if (((RemotePermission)Volatile.Read(ref currentPermissions) & RemotePermission.ControllerModelUse) == 0)
                throw new UnauthorizedAccessException("controller_model_permission_denied");
            if (string.IsNullOrWhiteSpace(input.Prompt) || input.Prompt.Length > ProtocolConstants.MaximumControllerModelRequestCharacters)
                throw new ArgumentException("invalid_model_prompt", nameof(input));

            await controllerModelRequestGate.WaitAsync(linkedRequest.Token).ConfigureAwait(false);
            try
            {
                var requestId = Guid.NewGuid();
                var completion = new TaskCompletionSource<ControllerModelResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                if (!pendingControllerModelRequests.TryAdd(requestId, completion))
                    throw new InvalidOperationException("controller_model_request_id_collision");

                using var cancellationRegistration = linkedRequest.Token.Register(() => completion.TrySetCanceled(linkedRequest.Token));
                try
                {
                    await SendAsync("controller.model.request", new ControllerModelRequest(
                        ProtocolConstants.CurrentVersion,
                        channel.SessionId,
                        requestId,
                        input.Prompt,
                        input.Model,
                        input.ReasoningEffort), linkedRequest.Token).ConfigureAwait(false);
                    // The controller's Codex Exec is allowed 120 seconds. Keep this caller-side
                    // deadline longer so it can return the provider's own result instead of
                    // timing out first while an MCP tool call or remote approval is pending.
                    var result = await completion.Task.WaitAsync(
                        TimeSpan.FromSeconds(ProtocolConstants.ControllerModelResponseTimeoutSeconds),
                        linkedRequest.Token).ConfigureAwait(false);
                    if (result.ProtocolVersion != ProtocolConstants.CurrentVersion || result.SessionId != channel.SessionId || result.RequestId != requestId)
                        throw new InvalidDataException("Controller model response did not match the active session request.");
                    if (!result.Success)
                        throw new InvalidOperationException(result.ErrorCode ?? "controller_model_failed");
                    return result.Response ?? string.Empty;
                }
                finally
                {
                    pendingControllerModelRequests.TryRemove(requestId, out _);
                }
            }
            catch (TimeoutException)
            {
                throw new TimeoutException("controller_model_timeout");
            }
            finally
            {
                controllerModelRequestGate.Release();
            }
        }

        async Task<ControllerModelCatalogResult> RequestControllerModelCatalogAsync(CancellationToken requestCancellation)
        {
            using var linkedRequest = CancellationTokenSource.CreateLinkedTokenSource(requestCancellation, sessionStop.Token);
            if (((RemotePermission)Volatile.Read(ref currentPermissions) & RemotePermission.ControllerModelUse) == 0)
                throw new UnauthorizedAccessException("controller_model_permission_denied");

            await controllerModelCatalogGate.WaitAsync(linkedRequest.Token).ConfigureAwait(false);
            try
            {
                var requestId = Guid.NewGuid();
                var completion = new TaskCompletionSource<ControllerModelCatalogResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                if (!pendingControllerModelCatalogRequests.TryAdd(requestId, completion))
                    throw new InvalidOperationException("controller_model_catalog_request_id_collision");

                using var cancellationRegistration = linkedRequest.Token.Register(() => completion.TrySetCanceled(linkedRequest.Token));
                try
                {
                    await SendAsync("controller.model.catalog.request", new ControllerModelCatalogRequest(
                        ProtocolConstants.CurrentVersion,
                        channel.SessionId,
                        requestId), linkedRequest.Token).ConfigureAwait(false);
                    var result = await completion.Task.WaitAsync(
                        TimeSpan.FromSeconds(ProtocolConstants.ControllerModelCatalogResponseTimeoutSeconds),
                        linkedRequest.Token).ConfigureAwait(false);
                    if (result.ProtocolVersion != ProtocolConstants.CurrentVersion || result.SessionId != channel.SessionId ||
                        result.RequestId != requestId || result.Models is null || result.Models.Count > ProtocolConstants.MaximumControllerModelCatalogModels ||
                        result.Models.Any(model => !IsValidControllerModelDescriptor(model)))
                        throw new InvalidDataException("Controller model catalog did not match the active session request.");
                    return result;
                }
                finally
                {
                    pendingControllerModelCatalogRequests.TryRemove(requestId, out _);
                }
            }
            finally
            {
                controllerModelCatalogGate.Release();
            }
        }

        async Task RefreshControllerModelCatalogAsync(CancellationToken requestCancellation)
        {
            try
            {
                var result = await RequestControllerModelCatalogAsync(requestCancellation).ConfigureAwait(false);
                PublishControllerModelCatalog(result);
            }
            catch (UnauthorizedAccessException)
            {
                PublishControllerModelCatalog(new ControllerModelCatalogResult(
                    ProtocolConstants.CurrentVersion, channel.SessionId, Guid.Empty, false, [], null, null, "permission_denied"));
            }
            catch (TimeoutException)
            {
                PublishControllerModelCatalog(new ControllerModelCatalogResult(
                    ProtocolConstants.CurrentVersion, channel.SessionId, Guid.Empty, false, [], null, null, "codex_model_catalog_timeout"));
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException or OperationCanceledException)
            {
                if (!sessionStop.IsCancellationRequested)
                {
                    PublishControllerModelCatalog(new ControllerModelCatalogResult(
                        ProtocolConstants.CurrentVersion, channel.SessionId, Guid.Empty, false, [], null, null, "codex_model_catalog_unavailable"));
                }
            }
        }

        static bool IsValidControllerModelDescriptor(ControllerModelDescriptor model)
        {
            return model is not null && !string.IsNullOrWhiteSpace(model.Id) && model.Id.Length <= ProtocolConstants.MaximumControllerModelIdCharacters &&
                   model.Id.All(character => char.IsLetterOrDigit(character) || character is '.' or '_' or ':' or '/' or '-') &&
                   !string.IsNullOrWhiteSpace(model.DisplayName) && model.DisplayName.Length <= ProtocolConstants.MaximumControllerModelDisplayNameCharacters &&
                   model.ReasoningEfforts is not null && model.ReasoningEfforts.Count > 0 && model.ReasoningEfforts.Count <= ProtocolConstants.MaximumControllerModelReasoningEfforts &&
                   model.ReasoningEfforts.All(effort => effort is "low" or "medium" or "high" or "xhigh" or "max" or "ultra") &&
                   model.DefaultReasoningEffort is "low" or "medium" or "high" or "xhigh" or "max" or "ultra" &&
                   model.ReasoningEfforts.Contains(model.DefaultReasoningEffort, StringComparer.Ordinal);
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
                initialPolicy = initialPolicy with { Permissions = initialPolicy.Permissions & channel.ApprovedPermissions };
                Volatile.Write(ref currentPermissions, (int)initialPolicy.Permissions);
                authorizer.UpdatePermissions(initialPolicy.Permissions);
                toolAuthorizer.UpdatePermissions(initialPolicy.Permissions);
                await SendAsync("session.policy", initialPolicy, sessionStop.Token).ConfigureAwait(false);
            }
            controllerModelRequestHandlerChanged?.Invoke(RequestControllerModelAsync);
            var permissions = (RemotePermission)Volatile.Read(ref currentPermissions);
            if ((permissions & RemotePermission.ControllerModelUse) != 0)
            {
                _ = RefreshControllerModelCatalogAsync(sessionStop.Token);
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

                if (envelope.Type == "controller.model.result")
                {
                    var result = envelope.Payload.Deserialize<ControllerModelResult>(ProtocolJson.Options)
                        ?? throw new InvalidDataException("Controller model response was empty.");
                    if (result.ProtocolVersion == ProtocolConstants.CurrentVersion && result.SessionId == channel.SessionId &&
                        result.RequestId != Guid.Empty &&
                        (result.Response?.Length ?? 0) <= ProtocolConstants.MaximumControllerModelResponseCharacters &&
                        (result.ErrorCode is null || result.ErrorCode.Length <= 64) &&
                        pendingControllerModelRequests.TryGetValue(result.RequestId, out var pending))
                    {
                        pending.TrySetResult(result);
                    }

                    continue;
                }

                if (envelope.Type == "controller.model.catalog.result")
                {
                    var result = envelope.Payload.Deserialize<ControllerModelCatalogResult>(ProtocolJson.Options)
                        ?? throw new InvalidDataException("Controller model catalog response was empty.");
                    if (result.ProtocolVersion == ProtocolConstants.CurrentVersion && result.SessionId == channel.SessionId &&
                        result.RequestId != Guid.Empty && result.Models is not null && result.Models.Count <= ProtocolConstants.MaximumControllerModelCatalogModels &&
                        (result.ErrorCode is null || result.ErrorCode.Length <= 64) &&
                        result.Models.All(IsValidControllerModelDescriptor) &&
                        pendingControllerModelCatalogRequests.TryGetValue(result.RequestId, out var pendingCatalog))
                    {
                        pendingCatalog.TrySetResult(result);
                    }

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
            controllerModelRequestHandlerChanged?.Invoke(null);
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
