using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Common.Protocol;

namespace RemoteCli.Local;

public sealed class LocalConnectionServiceClient
{
    private readonly string _pipeName;
    private readonly Func<IReadOnlySet<int>>? _processLookup;
    private readonly bool _discoverProcessPipes;
    private int _selectedProcessId;
    private Guid? _selectedSessionId;

    public int? SelectedProcessId => _selectedProcessId == 0 ? null : _selectedProcessId;
    public bool SelectService(int processId)
    {
        if (_processLookup?.Invoke().Contains(processId) != true) return false;
        _selectedProcessId = processId; _selectedSessionId = null; return true;
    }

    public LocalConnectionServiceClient(string? pipeName = null, Func<IReadOnlySet<int>>? processLookup = null)
    {
        _pipeName = pipeName ?? LocalConnectionServicePipeServer.DefaultPipeName;
        _processLookup = processLookup;
        _discoverProcessPipes = pipeName is null && processLookup is not null;
    }

    public async Task<LocalServiceResponse> SendAsync(
        string type,
        object payload,
        Guid? expectedSessionId,
        long? expectedStateVersion,
        CancellationToken cancellationToken,
        int timeoutMilliseconds = LocalIpcConstants.DefaultRequestTimeoutMilliseconds)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var request = LocalIpcProtocol.ValidateRequest(new LocalServiceRequest(
            LocalIpcConstants.CurrentVersion,
            Guid.NewGuid(),
            type,
            timeoutMilliseconds,
            JsonSerializer.SerializeToElement(payload, ProtocolJson.Options),
            expectedSessionId,
            expectedStateVersion));
        var discoveredProcessIds = _processLookup?.Invoke();
        if (_processLookup is not null && (discoveredProcessIds is null || discoveredProcessIds.Count == 0))
        {
            throw new IOException("No matching desktop service process was found.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(timeoutMilliseconds);
        await using var pipe = new NamedPipeClientStream(
            ".",
            _discoverProcessPipes
                ? LocalConnectionServicePipeServer.PipeNameForProcess(
                    discoveredProcessIds!.Contains(_selectedProcessId) ? _selectedProcessId : discoveredProcessIds.OrderDescending().First())
                : _pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await pipe.ConnectAsync(timeoutMilliseconds, timeout.Token).ConfigureAwait(false);
            if (_processLookup is not null)
            {
                var currentDesktopProcessIds = _processLookup();
                if (currentDesktopProcessIds is null || currentDesktopProcessIds.Count == 0 ||
                    !NamedPipeProcessBinding.IsConnectedToAny(pipe.SafePipeHandle, currentDesktopProcessIds))
                {
                    throw new UnauthorizedAccessException("The connected pipe does not belong to a discovered desktop process.");
                }
            }

            using var writeLock = new SemaphoreSlim(1, 1);
            await FramedJsonStream.WriteAsync(
                pipe, request, writeLock, timeout.Token, LocalIpcConstants.MaximumFrameBytes).ConfigureAwait(false);
            var response = await FramedJsonStream.ReadAsync<LocalServiceResponse>(
                pipe, timeout.Token, LocalIpcConstants.MaximumFrameBytes).ConfigureAwait(false);
            if (response is null ||
                response.ProtocolVersion != LocalIpcConstants.CurrentVersion ||
                response.RequestId != request.RequestId ||
                !string.Equals(response.Type, request.Type, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Local service response did not match its request.");
            }

            return response;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Local service request exceeded its time limit.");
        }
    }

    public async Task<LocalConnectionStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        if (_discoverProcessPipes)
        {
            var services = await DiscoverServicesAsync(cancellationToken).ConfigureAwait(false);
            var selected = services.FirstOrDefault(service => service.ProcessId == _selectedProcessId);
            if (selected is null && _selectedSessionId is not null)
                return LocalConnectionStatus.Unavailable("The selected service exited; rediscover and select a running service before remote work.");
            selected ??= services.OrderByDescending(service => service.Status.IsUsable(DateTimeOffset.UtcNow))
                .ThenByDescending(service => service.ProcessId).FirstOrDefault();
            if (selected is null) return LocalConnectionStatus.Unavailable("No running LongShell service was discovered.");
            _selectedProcessId = selected.ProcessId;
            _selectedSessionId = selected.Status.SessionId;
            return selected.Status;
        }

        try
        {
            var response = await SendAsync("connection.status", new { }, null, null, cancellationToken)
                .ConfigureAwait(false);
            if (!response.Success)
            {
                return LocalConnectionStatus.Unavailable(response.Error?.Message ?? "The local service rejected the status request.");
            }

            return response.Payload.Deserialize<LocalConnectionStatus>(ProtocolJson.Options)
                ?? LocalConnectionStatus.Unavailable("The local service returned an empty status.");
        }
        catch (Exception exception) when (exception is IOException or TimeoutException or InvalidDataException or UnauthorizedAccessException)
        {
            return LocalConnectionStatus.Unavailable("The local connection service is not responding.");
        }
    }

    public async Task<IReadOnlyList<DiscoveredLocalService>> DiscoverServicesAsync(CancellationToken cancellationToken)
    {
        var result = new List<DiscoveredLocalService>();
        foreach (var processId in (_processLookup?.Invoke() ?? new HashSet<int>()).OrderDescending().Take(16))
        {
            var candidate = new LocalConnectionServiceClient(
                LocalConnectionServicePipeServer.PipeNameForProcess(processId), () => new HashSet<int> { processId });
            try
            {
                var response = await candidate.SendAsync("connection.status", new { }, null, null, cancellationToken, 400).ConfigureAwait(false);
                var status = response.Payload.Deserialize<LocalConnectionStatus>(ProtocolJson.Options);
                if (response.Success && status is not null) result.Add(new DiscoveredLocalService(processId, status));
            }
            catch (Exception exception) when (exception is IOException or TimeoutException or UnauthorizedAccessException or InvalidDataException)
            {
                // Exiting, inaccessible, and old hosts are skipped. No path fallback or executable launch.
            }
        }
        return result;
    }

    public async Task<LocalConnectionStatus> WaitForStatusChangeAsync(
        long afterStateVersion,
        int waitMilliseconds,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await SendAsync(
                "connection.wait",
                new ConnectionWaitInput(afterStateVersion, waitMilliseconds),
                null,
                null,
                cancellationToken,
                Math.Clamp(waitMilliseconds + 2_000, 2_000, 22_000)).ConfigureAwait(false);
            return response.Success
                ? response.Payload.Deserialize<LocalConnectionStatus>(ProtocolJson.Options)
                    ?? LocalConnectionStatus.Unavailable("The local service returned an empty status.")
                : LocalConnectionStatus.Unavailable(response.Error?.Message ?? "The local service rejected the state wait request.");
        }
        catch (Exception exception) when (exception is IOException or TimeoutException or InvalidDataException or UnauthorizedAccessException)
        {
            return LocalConnectionStatus.Unavailable("The local connection service is not responding.");
        }
    }
}

public sealed class LocalConnectionServicePipeServer
{
    private const int MaximumConcurrentRequests = 8;
    private const int RequestReadTimeoutMilliseconds = 2_000;
    private readonly string _pipeName;
    private readonly Func<LocalServiceRequest, CancellationToken, Task<LocalServiceResponse>> _handler;
    private readonly SemaphoreSlim _requestSlots = new(MaximumConcurrentRequests, MaximumConcurrentRequests);
    private long _nextRequestId;

    public event Action<Guid, string, string>? RequestFailed;
    public event Action? Listening;

    public static string PipeNameForProcess(int processId) => DefaultPipeName + ".pid-" + processId;

    public LocalConnectionServicePipeServer(
        Func<LocalServiceRequest, CancellationToken, Task<LocalServiceResponse>> handler,
        string? pipeName = null)
    {
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        _pipeName = pipeName ?? DefaultPipeName;
    }

    public static string DefaultPipeName
    {
        get
        {
            var user = Environment.UserDomainName + "\\" + Environment.UserName;
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(user));
            try
            {
                return "RemoteCli.Connection." + Convert.ToHexString(hash.AsSpan(0, 12));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(hash);
            }
        }
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var activeRequests = new ConcurrentDictionary<long, Task>();
        var activeCancellations = new ConcurrentDictionary<long, CancellationTokenSource>();
        var firstInstance = true;
        var listeningPublished = false;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await _requestSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
                NamedPipeServerStream? pipe = null;
                try
                {
                    var options = PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly;
                    if (firstInstance)
                    {
                        options |= PipeOptions.FirstPipeInstance;
                    }

                    pipe = new NamedPipeServerStream(
                        _pipeName,
                        PipeDirection.InOut,
                        MaximumConcurrentRequests,
                        PipeTransmissionMode.Byte,
                        options,
                        64 * 1024,
                        64 * 1024);
                    if (!listeningPublished)
                    {
                        listeningPublished = true;
                        try
                        {
                            Listening?.Invoke();
                        }
                        catch
                        {
                            // A UI observer cannot stop the local service from accepting requests.
                        }
                    }
                    await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                    firstInstance = false;
                    var requestId = Interlocked.Increment(ref _nextRequestId);
                    var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    var requestTask = ProcessRequestAsync(pipe, requestCancellation.Token);
                    pipe = null;
                    activeRequests[requestId] = requestTask;
                    activeCancellations[requestId] = requestCancellation;
                    _ = requestTask.ContinueWith(
                        static (completedTask, state) =>
                        {
                            var tuple = ((ConcurrentDictionary<long, Task> Requests, ConcurrentDictionary<long, CancellationTokenSource> Cancellations, SemaphoreSlim Slots, long Id))state!;
                            tuple.Requests.TryRemove(tuple.Id, out var ignoredTask);
                            if (tuple.Cancellations.TryRemove(tuple.Id, out var requestStop))
                            {
                                requestStop.Dispose();
                            }

                            tuple.Slots.Release();
                        },
                        (activeRequests, activeCancellations, _requestSlots, requestId),
                        CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);
                }
                catch
                {
                    pipe?.Dispose();
                    _requestSlots.Release();
                    throw;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            foreach (var requestCancellation in activeCancellations.Values)
            {
                try
                {
                    requestCancellation.Cancel();
                }
                catch (ObjectDisposedException)
                {
                }
            }

            try
            {
                var drainTask = Task.WhenAll(activeRequests.Values);
                await drainTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException or OperationCanceledException or TimeoutException)
            {
            }
        }
    }

    private async Task ProcessRequestAsync(NamedPipeServerStream pipe, CancellationToken serverToken)
    {
        try
        {
            await using (pipe.ConfigureAwait(false))
            {
                LocalServiceRequest? request;
                using (var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(serverToken))
                {
                    readTimeout.CancelAfter(RequestReadTimeoutMilliseconds);
                    request = await FramedJsonStream.ReadAsync<LocalServiceRequest>(
                        pipe, readTimeout.Token, LocalIpcConstants.MaximumFrameBytes).ConfigureAwait(false);
                }

                if (request is null)
                {
                    return;
                }

                LocalServiceResponse response;
                try
                {
                    request = LocalIpcProtocol.ValidateRequest(request);
                    using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(serverToken);
                    requestTimeout.CancelAfter(request.TimeoutMilliseconds);
                    Task<LocalServiceResponse>? handlerTask = null;
                    try
                    {
                        handlerTask = _handler(request, requestTimeout.Token);
                        response = await handlerTask.WaitAsync(TimeSpan.FromMilliseconds(request.TimeoutMilliseconds), serverToken).ConfigureAwait(false);
                    }
                    catch (TimeoutException)
                    {
                        requestTimeout.Cancel();
                        if (handlerTask is not null)
                        {
                            ObserveLateFailure(handlerTask);
                        }

                        response = Failure(request, "request_timeout", "The local service request timed out.", retryable: true);
                    }
                    catch (OperationCanceledException) when (!serverToken.IsCancellationRequested)
                    {
                        response = Failure(request, "request_timeout", "The local service request timed out.", retryable: true);
                    }
                    catch (OperationCanceledException) when (serverToken.IsCancellationRequested)
                    {
                        try
                        {
                            requestTimeout.Cancel();
                        }
                        catch (ObjectDisposedException)
                        {
                        }

                        if (handlerTask is not null)
                        {
                            ObserveLateFailure(handlerTask);
                        }

                        throw;
                    }
                    catch (Exception exception) when (exception is IOException or InvalidOperationException or ArgumentException or JsonException or TimeoutException)
                    {
                        response = Failure(request, "request_failed", "The local service could not complete the request.", retryable: false);
                    }

                    if (response.RequestId != request.RequestId || response.Type != request.Type ||
                        response.ProtocolVersion != LocalIpcConstants.CurrentVersion)
                    {
                        response = Failure(request, "invalid_service_response", "The local service returned an invalid response.", retryable: false);
                    }
                }
                catch (InvalidDataException exception)
                {
                    response = Failure(request, "invalid_request", exception.Message, retryable: false);
                }

                using var writeTimeout = CancellationTokenSource.CreateLinkedTokenSource(serverToken);
                writeTimeout.CancelAfter(RequestReadTimeoutMilliseconds);
                using var writeLock = new SemaphoreSlim(1, 1);
                if (!response.Success && response.Error is { } error)
                {
                    try
                    {
                        RequestFailed?.Invoke(response.RequestId, response.Type, error.Code);
                    }
                    catch
                    {
                        // Diagnostics cannot prevent a response from being returned to the caller.
                    }
                }

                await FramedJsonStream.WriteAsync(
                    pipe, response, writeLock, writeTimeout.Token, LocalIpcConstants.MaximumFrameBytes).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or OperationCanceledException or TimeoutException or JsonException or InvalidDataException)
        {
            // Malformed, abandoned, or timed-out IPC clients are isolated to their own pipe instance.
        }
    }

    private static void ObserveLateFailure(Task task)
    {
        _ = task.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    public static LocalServiceResponse Success(LocalServiceRequest request, long stateVersion, JsonElement payload) => new(
        LocalIpcConstants.CurrentVersion,
        request.RequestId,
        request.Type,
        true,
        stateVersion,
        payload,
        null);

    public static LocalServiceResponse Failure(
        LocalServiceRequest request,
        string code,
        string message,
        bool retryable,
        long stateVersion = 0,
        string stage = "service") => new(
            LocalIpcConstants.CurrentVersion,
            request.RequestId,
            request.Type,
            false,
            stateVersion,
            JsonSerializer.SerializeToElement(new { }, ProtocolJson.Options),
            new LocalConnectionError(stage, code, message, retryable, retryable ? 500 : 0, request.RequestId));
}
