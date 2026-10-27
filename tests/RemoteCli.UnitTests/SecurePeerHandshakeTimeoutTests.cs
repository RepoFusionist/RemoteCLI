using System.Diagnostics;
using System.Net.WebSockets;
using Common.Protocol;
using Common.Security;

namespace RemoteCli.UnitTests;

public sealed class SecurePeerHandshakeTimeoutTests
{
    private static readonly Guid SessionId = Guid.Parse("73028c47-82d4-4d47-a1c0-767bb303e914");
    private const string TestSecret = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    [Fact]
    public async Task ConnectLocalAsync_StopsWhenRemotePeerIsSilent()
    {
        using var socket = new ScriptedWebSocket(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            return new WebSocketReceiveResult(0, WebSocketMessageType.Binary, true);
        });
        var stopwatch = Stopwatch.StartNew();

        var exception = await Assert.ThrowsAsync<TimeoutException>(() =>
            ConnectLocalAsync(socket, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15)));
        stopwatch.Stop();

        Assert.Contains("remote hello receive", exception.Message, StringComparison.Ordinal);
        Assert.InRange(stopwatch.Elapsed, TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(15));
        Assert.False(socket.WasAborted);
        Assert.True(socket.WasCloseOutputSent);
        Assert.True(socket.WasDisposed);
    }

    [Fact]
    public async Task ConnectLocalAsync_StopsAfterIncompleteHelloFragment()
    {
        var receiveCount = 0;
        using var socket = new ScriptedWebSocket(async (_, cancellationToken) =>
        {
            if (Interlocked.Increment(ref receiveCount) == 1)
            {
                return new WebSocketReceiveResult(1, WebSocketMessageType.Binary, false);
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            return new WebSocketReceiveResult(0, WebSocketMessageType.Binary, true);
        });
        var stopwatch = Stopwatch.StartNew();

        var exception = await Assert.ThrowsAsync<TimeoutException>(() =>
            ConnectLocalAsync(socket, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15)));
        stopwatch.Stop();

        Assert.Contains("remote hello receive", exception.Message, StringComparison.Ordinal);
        Assert.InRange(stopwatch.Elapsed, TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(15));
        Assert.Equal(2, receiveCount);
        Assert.False(socket.WasAborted);
        Assert.True(socket.WasCloseOutputSent);
        Assert.True(socket.WasDisposed);
    }

    [Fact]
    public async Task AcceptRemoteAsync_PreservesCallerCancellationWhenLocalPeerDoesNotSendHello()
    {
        using var socket = new ScriptedWebSocket(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            return new WebSocketReceiveResult(0, WebSocketMessageType.Binary, true);
        });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            SecurePeerChannel.AcceptRemoteAsync(
                socket,
                SessionId,
                TestSecret,
                "remote-test",
                DateTimeOffset.UtcNow.AddMinutes(1),
                RemotePermission.CommandExecute,
                cancellation.Token));

        Assert.False(socket.WasAborted);
        Assert.True(socket.WasCloseOutputSent);
        Assert.True(socket.WasDisposed);
    }

    [Fact]
    public async Task ConnectLocalAsync_PreservesCallerCancellationInsteadOfReportingTimeout()
    {
        using var socket = new ScriptedWebSocket(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            return new WebSocketReceiveResult(0, WebSocketMessageType.Binary, true);
        });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ConnectLocalAsync(socket, cancellation.Token));

        Assert.False(socket.WasAborted);
        Assert.True(socket.WasCloseOutputSent);
        Assert.True(socket.WasDisposed);
    }

    private static Task<SecurePeerChannel> ConnectLocalAsync(WebSocket socket, CancellationToken cancellationToken) =>
        SecurePeerChannel.ConnectLocalAsync(
            socket,
            SessionId,
            TestSecret,
            "local-test",
            static (_, _) => Task.FromResult(RemotePermission.None),
            cancellationToken);

    private sealed class ScriptedWebSocket(
        Func<ArraySegment<byte>, CancellationToken, Task<WebSocketReceiveResult>> receive) : WebSocket
    {
        private bool _disposed;
        private WebSocketState _state = WebSocketState.Open;

        public bool WasAborted { get; private set; }
        public bool WasCloseOutputSent { get; private set; }
        public bool WasDisposed => _disposed;

        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => _state;
        public override string? SubProtocol => null;

        public override void Abort()
        {
            WasAborted = true;
            _state = WebSocketState.Aborted;
        }

        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            _state = WebSocketState.Closed;
            return Task.CompletedTask;
        }

        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            WasCloseOutputSent = true;
            _state = WebSocketState.Closed;
            return Task.CompletedTask;
        }

        public override void Dispose()
        {
            _disposed = true;
            if (_state != WebSocketState.Aborted)
            {
                _state = WebSocketState.Closed;
            }
        }

        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken) =>
            receive(buffer, cancellationToken);

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}
