using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using Common.Security;

namespace RemoteCli.UnitTests;

public sealed class WebSocketConnectRetryTests
{
    [Fact]
    public async Task ConnectClientWithRetryAsync_UsesThreeAttemptsAndBoundedBackoffOnLoopbackRefusal()
    {
        var port = GetUnusedLoopbackPort();
        var uri = new Uri("ws://127.0.0.1:" + port + "/retry-test");
        var sockets = new List<ClientWebSocket>();
        var attempts = 0;
        var totalDeadline = TimeSpan.FromSeconds(32);
        using var callerWatchdog = new CancellationTokenSource(TimeSpan.FromSeconds(34));
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var connectTask = WebSocketConnectHelper.ConnectClientWithRetryAsync(
                () =>
                {
                    Interlocked.Increment(ref attempts);
                    var socket = new ClientWebSocket();
                    socket.Options.Proxy = null;
                    lock (sockets)
                    {
                        sockets.Add(socket);
                    }

                    return socket;
                },
                uri,
                callerWatchdog.Token,
                totalTimeout: totalDeadline);

            var exception = await Assert.ThrowsAsync<WebSocketException>(
                () => connectTask.WaitAsync(TimeSpan.FromSeconds(35)));
            stopwatch.Stop();

            Assert.Equal(3, attempts);
            Assert.True(
                stopwatch.Elapsed >= TimeSpan.FromMilliseconds(700),
                "Two retries should include at least the 250 ms and 500 ms backoff delays.");
            Assert.True(
                stopwatch.Elapsed < totalDeadline,
                "The final refusal should be returned before the total connection deadline.");
            Assert.NotNull(exception);
        }
        finally
        {
            lock (sockets)
            {
                foreach (var socket in sockets)
                {
                    socket.Dispose();
                }
            }
        }
    }

    [Fact]
    public async Task ConnectClientWithRetryAsync_RejectsMoreThanThreeAttemptsBeforeCreatingSocket()
    {
        var factoryCalls = 0;
        using var socket = new ClientWebSocket();
        var uri = new Uri("ws://127.0.0.1:1/retry-test");

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            WebSocketConnectHelper.ConnectClientWithRetryAsync(
                () =>
                {
                    Interlocked.Increment(ref factoryCalls);
                    return socket;
                },
                uri,
                CancellationToken.None,
                maximumAttempts: 4));

        Assert.Equal(0, factoryCalls);
    }

    private static int GetUnusedLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
