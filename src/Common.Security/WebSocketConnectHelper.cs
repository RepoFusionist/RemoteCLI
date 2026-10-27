using System.Net.WebSockets;
using System.Diagnostics;
using System.Net;

namespace Common.Security;

public static class WebSocketConnectHelper
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    public static async Task<ClientWebSocket> ConnectClientWithRetryAsync(
        Func<ClientWebSocket> socketFactory,
        Uri uri,
        CancellationToken cancellationToken,
        int maximumAttempts = 3,
        TimeSpan? totalTimeout = null,
        Guid? requestId = null)
    {
        ArgumentNullException.ThrowIfNull(socketFactory);
        ArgumentNullException.ThrowIfNull(uri);
        if (maximumAttempts is < 1 or > 3)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumAttempts), "Connection retries are limited to three attempts.");
        }

        var totalDeadline = totalTimeout ?? TimeSpan.FromSeconds(32);
        if (totalDeadline <= TimeSpan.Zero || totalDeadline > TimeSpan.FromMinutes(2))
        {
            throw new ArgumentOutOfRangeException(nameof(totalTimeout));
        }

        using var totalCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        totalCancellation.CancelAfter(totalDeadline);
        Exception? lastFailure = null;
        for (var attempt = 1; attempt <= maximumAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (totalCancellation.IsCancellationRequested)
            {
                throw new TimeoutException("The relay WebSocket connection exceeded its total deadline.", lastFailure);
            }

            var socket = socketFactory() ?? throw new InvalidOperationException("The WebSocket factory returned null.");
            try
            {
                Trace.TraceInformation("LongShell relay WebSocket connect requestId={0} attempt={1}/{2}", requestId, attempt, maximumAttempts);
                await ConnectClientAsync(socket, uri, totalCancellation.Token).ConfigureAwait(false);
                return socket;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                socket.Dispose();
                throw new TimeoutException("The relay WebSocket connection exceeded its total deadline.", lastFailure);
            }
            catch (Exception exception) when (exception is WebSocketException or TimeoutException or RelayWebSocketException)
            {
                socket.Dispose();
                lastFailure = exception;
                Trace.TraceWarning(
                    "LongShell relay WebSocket connect requestId={0} attempt={1}/{2} failure={3}",
                    requestId,
                    attempt,
                    maximumAttempts,
                    exception.GetType().Name);
                if (attempt == maximumAttempts ||
                    exception is RelayWebSocketException { Retryable: false })
                {
                    throw;
                }

                var backoff = TimeSpan.FromMilliseconds(250 * (1 << (attempt - 1)));
                try
                {
                    await Task.Delay(backoff, totalCancellation.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new TimeoutException("The relay WebSocket connection exceeded its total deadline.", lastFailure);
                }
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        throw new TimeoutException("The relay WebSocket connection did not complete.", lastFailure);
    }

    public static async Task ConnectClientAsync(
        ClientWebSocket socket,
        Uri uri,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(socket);
        ArgumentNullException.ThrowIfNull(uri);
        var deadline = timeout ?? DefaultTimeout;
        if (deadline <= TimeSpan.Zero || deadline > TimeSpan.FromMinutes(2))
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCancellation.CancelAfter(deadline);
        socket.Options.CollectHttpResponseDetails = true;
        try
        {
            await socket.ConnectAsync(uri, timeoutCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            socket.Abort();
            throw new TimeoutException($"Timed out connecting the relay WebSocket after {deadline.TotalSeconds:0} seconds.");
        }
        catch (WebSocketException) when ((int)socket.HttpStatusCode >= 400)
        {
            var statusCode = socket.HttpStatusCode;
            socket.Abort();
            throw new RelayWebSocketException(statusCode);
        }
        catch
        {
            socket.Abort();
            throw;
        }
    }
}

public sealed class RelayWebSocketException(HttpStatusCode statusCode) : IOException(GetMessage(statusCode))
{
    public HttpStatusCode StatusCode { get; } = statusCode;
    public bool Retryable => StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests ||
        (int)StatusCode >= 500;

    private static string GetMessage(HttpStatusCode statusCode) => statusCode switch
    {
        HttpStatusCode.NotFound => "连接授权已过期、已被更新或已关闭（Relay WebSocket HTTP 404）。请确认被控端连接授权仍开启。",
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "Relay 拒绝建立连接，请检查服务器访问规则（HTTP " + (int)statusCode + "）。",
        HttpStatusCode.TooManyRequests => "Relay 连接请求过于频繁，请稍后重试（HTTP 429）。",
        _ => "Relay 无法建立 WebSocket 连接（HTTP " + (int)statusCode + "），请检查服务状态和反向代理配置。"
    };
}
