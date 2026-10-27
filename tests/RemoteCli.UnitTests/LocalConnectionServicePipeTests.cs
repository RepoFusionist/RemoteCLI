using System.IO.Pipes;
using System.Text.Json;
using Common.Protocol;
using RemoteCli.Local;

namespace RemoteCli.UnitTests;

public sealed class LocalConnectionServicePipeTests
{
    private static readonly TimeSpan CompletionLimit = TimeSpan.FromSeconds(3);

    [Fact]
    public async Task ConcurrentRequestsKeepRequestIdsAndPayloadsCorrelated()
    {
        const int requestCount = 8;
        var allHandlersEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handlerRequestIds = new System.Collections.Concurrent.ConcurrentBag<Guid>();
        var handlerCount = 0;
        await using var server = StartServer(async (request, cancellationToken) =>
        {
            handlerRequestIds.Add(request.RequestId);
            if (Interlocked.Increment(ref handlerCount) == requestCount)
            {
                allHandlersEntered.TrySetResult(true);
            }

            await allHandlersEntered.Task.WaitAsync(cancellationToken);
            var value = request.Payload.GetProperty("value").GetInt32();
            return LocalConnectionServicePipeServer.Success(
                request,
                stateVersion: 12,
                JsonSerializer.SerializeToElement(new { echo = value }));
        });

        var clients = Enumerable.Range(0, requestCount).Select(async value =>
        {
            var client = new LocalConnectionServiceClient(server.PipeName);
            return await client.SendAsync(
                "connection.status",
                new { value },
                expectedSessionId: null,
                expectedStateVersion: null,
                CancellationToken.None,
                timeoutMilliseconds: 5_000);
        }).ToArray();

        var responses = await Task.WhenAll(clients).WaitAsync(CompletionLimit);

        Assert.Equal(requestCount, handlerCount);
        Assert.Equal(requestCount, handlerRequestIds.Distinct().Count());
        Assert.Equal(requestCount, responses.Select(response => response.RequestId).Distinct().Count());
        Assert.All(responses, response =>
        {
            Assert.True(response.Success);
            Assert.Equal("connection.status", response.Type);
            Assert.Contains(response.RequestId, handlerRequestIds);
        });

        var echoedValues = responses
            .Select(response => response.Payload.GetProperty("echo").GetInt32())
            .Order()
            .ToArray();
        Assert.Equal(Enumerable.Range(0, requestCount), echoedValues);
    }

    [Fact]
    public async Task LocatedDesktopProcessMustOwnTheConnectedNamedPipe()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using var server = StartServer((request, _) => Task.FromResult(LocalConnectionServicePipeServer.Success(
            request,
            stateVersion: 12,
            JsonSerializer.SerializeToElement(new { connected = true }))));
        var client = new LocalConnectionServiceClient(
            server.PipeName,
            () => new HashSet<int> { Environment.ProcessId });

        var response = await client.SendAsync(
            "connection.status",
            new { },
            expectedSessionId: null,
            expectedStateVersion: null,
            CancellationToken.None,
            timeoutMilliseconds: 2_000);

        Assert.True(response.Success);
        Assert.True(response.Payload.GetProperty("connected").GetBoolean());
    }

    [Fact]
    public async Task MissingLocatedDesktopProcessRejectsBeforePipeConnection()
    {
        var handlerCount = 0;
        await using var server = StartServer((request, _) =>
        {
            Interlocked.Increment(ref handlerCount);
            return Task.FromResult(LocalConnectionServicePipeServer.Success(
                request,
                stateVersion: 1,
                JsonSerializer.SerializeToElement(new { connected = true })));
        });
        var client = new LocalConnectionServiceClient(server.PipeName, () => new HashSet<int>());

        var exception = await Assert.ThrowsAsync<IOException>(() => client.SendAsync(
            "connection.status",
            new { },
            expectedSessionId: null,
            expectedStateVersion: null,
            CancellationToken.None,
            timeoutMilliseconds: 500));

        Assert.Contains("No matching desktop service process", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, handlerCount);
    }

    [Fact]
    public async Task ProcessLookupIsRepeatedAfterConnectBeforeSendingTheRequest()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var handlerCount = 0;
        var processLookupCount = 0;
        await using var server = StartServer((request, _) =>
        {
            Interlocked.Increment(ref handlerCount);
            return Task.FromResult(LocalConnectionServicePipeServer.Success(
                request,
                stateVersion: 1,
                JsonSerializer.SerializeToElement(new { connected = true })));
        });
        var client = new LocalConnectionServiceClient(server.PipeName, () =>
            Interlocked.Increment(ref processLookupCount) == 1
                ? new HashSet<int> { Environment.ProcessId }
                : new HashSet<int>());

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => client.SendAsync(
            "connection.status",
            new { },
            expectedSessionId: null,
            expectedStateVersion: null,
            CancellationToken.None,
            timeoutMilliseconds: 1_000));

        Assert.Equal(2, processLookupCount);
        Assert.Equal(0, handlerCount);
    }

    [Fact]
    public void DesktopProcessCandidateIsIndependentOfInstallDirectoryButMustBeADesktopHost()
    {
        Assert.True(DesktopProcessLocator.IsMatchingDesktopProcess(
            @"C:\Program Files\LongShell\RemoteCli.Desktop.exe",
            @"c:\program files\longshell\RemoteCli.Desktop.exe",
            hasMainWindow: true));
        Assert.True(DesktopProcessLocator.IsMatchingDesktopProcess(
            @"C:\Program Files\LongShell\RemoteCli.Desktop.exe",
            @"C:\Other\RemoteCli.Desktop.exe",
            hasMainWindow: true));
        Assert.False(DesktopProcessLocator.IsMatchingDesktopProcess("ignored", @"C:\Other\Other.exe", true));
        Assert.False(DesktopProcessLocator.IsMatchingDesktopProcess(
            @"C:\Program Files\LongShell\RemoteCli.Desktop.exe",
            @"C:\Program Files\LongShell\RemoteCli.Desktop.exe",
            hasMainWindow: false));
    }

    [Fact]
    public async Task ServiceRequestTimeoutCompletesWithinBoundedTime()
    {
        var handlerStarted = NewSignal();
        var handlerCancelled = NewSignal();
        await using var server = StartServer(async (request, cancellationToken) =>
        {
            handlerStarted.TrySetResult(true);
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                handlerCancelled.TrySetResult(true);
                throw;
            }

            return LocalConnectionServicePipeServer.Success(
                request,
                stateVersion: 1,
                JsonSerializer.SerializeToElement(new { completed = true }));
        });
        var client = new LocalConnectionServiceClient(server.PipeName);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        LocalServiceResponse? response = null;
        Exception? failure = null;

        try
        {
            response = await client.SendAsync(
                "connection.status",
                new { },
                expectedSessionId: null,
                expectedStateVersion: null,
                CancellationToken.None,
                timeoutMilliseconds: 300).WaitAsync(CompletionLimit);
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        stopwatch.Stop();
        await handlerStarted.Task.WaitAsync(CompletionLimit);
        await handlerCancelled.Task.WaitAsync(CompletionLimit);
        Assert.True(stopwatch.Elapsed < CompletionLimit, $"Request timeout took {stopwatch.Elapsed.TotalMilliseconds:F0} ms.");
        Assert.True(
            failure is TimeoutException || response?.Error?.Code == "request_timeout",
            $"Expected a bounded timeout, received response code '{response?.Error?.Code}' and exception '{failure?.GetType().Name}'.");
    }

    [Fact]
    public async Task CallerCancellationRemainsCancellationAndStopsClientWaitPromptly()
    {
        var handlerStarted = NewSignal();
        await using var server = StartServer(async (request, cancellationToken) =>
        {
            handlerStarted.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return LocalConnectionServicePipeServer.Success(
                request,
                stateVersion: 1,
                JsonSerializer.SerializeToElement(new { completed = true }));
        });
        var client = new LocalConnectionServiceClient(server.PipeName);
        using var cancellation = new CancellationTokenSource();
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var requestTask = client.SendAsync(
            "connection.status",
            new { },
            expectedSessionId: null,
            expectedStateVersion: null,
            cancellation.Token,
            timeoutMilliseconds: 5_000);

        await handlerStarted.Task.WaitAsync(CompletionLimit);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await requestTask.WaitAsync(CompletionLimit));

        stopwatch.Stop();
        Assert.True(stopwatch.Elapsed < CompletionLimit, $"Caller cancellation took {stopwatch.Elapsed.TotalMilliseconds:F0} ms.");
    }

    [Fact]
    public async Task InvalidVersionTypeTimeoutAndPreconditionsAreRejectedBeforeHandler()
    {
        var handlerCount = 0;
        await using var server = StartServer((request, _) =>
        {
            Interlocked.Increment(ref handlerCount);
            return Task.FromResult(LocalConnectionServicePipeServer.Success(
                request,
                1,
                JsonSerializer.SerializeToElement(new { reachedHandler = true })));
        });

        var invalidRequests = new[]
        {
            CreateRequest(protocolVersion: LocalIpcConstants.CurrentVersion + 1),
            CreateRequest(type: "local.unknown"),
            CreateRequest(timeoutMilliseconds: 0),
            CreateRequest(timeoutMilliseconds: LocalIpcConstants.MaximumRequestTimeoutMilliseconds + 1),
            CreateRequest(expectedSessionId: Guid.NewGuid(), expectedStateVersion: null),
            CreateRequest(expectedSessionId: Guid.Empty, expectedStateVersion: 0),
            CreateRequest(expectedSessionId: Guid.NewGuid(), expectedStateVersion: -1)
        };

        foreach (var request in invalidRequests)
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var response = await SendRawAsync(server.PipeName, request).WaitAsync(CompletionLimit);
            stopwatch.Stop();

            Assert.False(response.Success);
            Assert.Equal(request.RequestId, response.RequestId);
            Assert.Equal(request.Type, response.Type);
            Assert.Equal("invalid_request", response.Error?.Code);
            Assert.True(stopwatch.Elapsed < CompletionLimit, $"Invalid request '{request.Type}' took {stopwatch.Elapsed.TotalMilliseconds:F0} ms.");
        }

        Assert.Equal(0, handlerCount);
    }

    [Fact]
    public async Task OversizedPayloadIsRejectedBeforeConnectingToService()
    {
        var client = new LocalConnectionServiceClient(CreatePipeName());
        var payload = new { value = new string('x', LocalIpcConstants.MaximumFrameBytes) };
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await client.SendAsync(
                "connection.status",
                payload,
                expectedSessionId: null,
                expectedStateVersion: null,
                CancellationToken.None,
                timeoutMilliseconds: 5_000).WaitAsync(CompletionLimit));

        stopwatch.Stop();
        Assert.True(stopwatch.Elapsed < CompletionLimit, $"Oversize rejection took {stopwatch.Elapsed.TotalMilliseconds:F0} ms.");
    }

    [Fact]
    public async Task MissingServiceFailsFastWithConnectTimeout()
    {
        var client = new LocalConnectionServiceClient(CreatePipeName());
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        await Assert.ThrowsAsync<TimeoutException>(async () =>
            await client.SendAsync(
                "connection.status",
                new { },
                expectedSessionId: null,
                expectedStateVersion: null,
                CancellationToken.None,
                timeoutMilliseconds: 250).WaitAsync(CompletionLimit));

        stopwatch.Stop();
        Assert.True(stopwatch.Elapsed < CompletionLimit, $"Missing service took {stopwatch.Elapsed.TotalMilliseconds:F0} ms to reject.");
    }

    [Fact]
    public async Task StoppingServerCancelsAcceptLoopAndActiveHandlerWithinBoundedTime()
    {
        var handlerStarted = NewSignal();
        var handlerCancelled = NewSignal();
        var server = StartServer(async (request, cancellationToken) =>
        {
            handlerStarted.TrySetResult(true);
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                handlerCancelled.TrySetResult(true);
                throw;
            }

            return LocalConnectionServicePipeServer.Success(
                request,
                stateVersion: 1,
                JsonSerializer.SerializeToElement(new { completed = true }));
        });
        var client = new LocalConnectionServiceClient(server.PipeName);
        var clientRequest = client.SendAsync(
            "connection.status",
            new { },
            expectedSessionId: null,
            expectedStateVersion: null,
            CancellationToken.None,
            timeoutMilliseconds: 10_000);
        await handlerStarted.Task.WaitAsync(CompletionLimit);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        await server.StopAsync().WaitAsync(CompletionLimit);
        await handlerCancelled.Task.WaitAsync(CompletionLimit);
        await Task.WhenAny(clientRequest, Task.Delay(CompletionLimit));
        stopwatch.Stop();

        Assert.True(server.RunTask.IsCompleted, "The named-pipe accept loop did not stop.");
        Assert.True(clientRequest.IsCompleted, "The in-flight client did not observe server shutdown.");
        Assert.True(stopwatch.Elapsed < CompletionLimit, $"Server stop took {stopwatch.Elapsed.TotalMilliseconds:F0} ms.");
        try
        {
            await clientRequest;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or OperationCanceledException or TimeoutException)
        {
        }

        await server.DisposeAsync();
    }

    private static ServerHarness StartServer(
        Func<LocalServiceRequest, CancellationToken, Task<LocalServiceResponse>> handler)
    {
        var pipeName = CreatePipeName();
        var server = new LocalConnectionServicePipeServer(handler, pipeName);
        var cancellation = new CancellationTokenSource();
        return new ServerHarness(pipeName, cancellation, server.RunAsync(cancellation.Token));
    }

    private static string CreatePipeName() => "RemoteCli.UnitTests." + Guid.NewGuid().ToString("N");

    [Fact]
    public async Task DiscoveryBindsTheRunningProcessEndpointWithoutAnInstallDirectory()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var state = new LocalConnectionStateMachine();
        var server = new LocalConnectionServicePipeServer((request, _) => Task.FromResult(
            LocalConnectionServicePipeServer.Success(request, state.GetSnapshot().StateVersion,
                JsonSerializer.SerializeToElement(state.GetSnapshot(), ProtocolJson.Options))),
            LocalConnectionServicePipeServer.PipeNameForProcess(Environment.ProcessId));
        var listening = NewSignal(); server.Listening += () => listening.TrySetResult(true);
        var run = server.RunAsync(cancellation.Token);
        try
        {
            await listening.Task.WaitAsync(CompletionLimit);
            var client = new LocalConnectionServiceClient(processLookup: () => new HashSet<int> { Environment.ProcessId });
            var services = await client.DiscoverServicesAsync(cancellation.Token);
            Assert.Equal(Environment.ProcessId, Assert.Single(services).ProcessId);
            Assert.True(client.SelectService(Environment.ProcessId));
            Assert.Equal(LocalConnectionState.Disconnected, (await client.GetStatusAsync(cancellation.Token)).ConnectionState);
            Assert.Equal(Environment.ProcessId, client.SelectedProcessId);
        }
        finally { cancellation.Cancel(); await run.WaitAsync(CompletionLimit); }
    }

    private static TaskCompletionSource<bool> NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static LocalServiceRequest CreateRequest(
        int? protocolVersion = null,
        string type = "connection.status",
        int timeoutMilliseconds = LocalIpcConstants.DefaultRequestTimeoutMilliseconds,
        Guid? expectedSessionId = null,
        long? expectedStateVersion = null) => new(
            protocolVersion ?? LocalIpcConstants.CurrentVersion,
            Guid.NewGuid(),
            type,
            timeoutMilliseconds,
            JsonSerializer.SerializeToElement(new { probe = true }),
            expectedSessionId,
            expectedStateVersion);

    private static async Task<LocalServiceResponse> SendRawAsync(string pipeName, LocalServiceRequest request)
    {
        await using var pipe = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await pipe.ConnectAsync(2_000, timeout.Token);
        using var writeLock = new SemaphoreSlim(1, 1);
        await FramedJsonStream.WriteAsync(
            pipe,
            request,
            writeLock,
            timeout.Token,
            LocalIpcConstants.MaximumFrameBytes);
        return await FramedJsonStream.ReadAsync<LocalServiceResponse>(
            pipe,
            timeout.Token,
            LocalIpcConstants.MaximumFrameBytes)
            ?? throw new InvalidDataException("Local service closed without a response.");
    }

    private sealed class ServerHarness(
        string pipeName,
        CancellationTokenSource cancellation,
        Task runTask) : IAsyncDisposable
    {
        private int _stopped;

        public string PipeName { get; } = pipeName;
        public Task RunTask { get; } = runTask;

        public async Task StopAsync()
        {
            if (Interlocked.Exchange(ref _stopped, 1) == 0)
            {
                cancellation.Cancel();
            }

            await RunTask.WaitAsync(CompletionLimit);
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await StopAsync();
            }
            finally
            {
                cancellation.Dispose();
            }
        }
    }
}
