using System.Text.Json;

namespace RemoteCli.Desktop;

internal sealed partial class MainForm
{
    private async Task HandleGatewayTerminalAsync(JsonElement request)
    {
        // Materialize all values before the first await; no JsonDocument lifetime is retained.
        var parsed = GatewayPtyRequest.Parse(request);
        var type = parsed.Type;
        var conversationId = parsed.ConversationId;
        var sessionId = parsed.SessionId;
        var requestId = parsed.RequestId;
        if (requestId.Length is 0 or > 128 || sessionId.Length is 0 or > 128) return;
        try
        {
            var session = await _gatewayTerminalPool.FindAsync(conversationId, _serviceLifetime.Token).ConfigureAwait(false);
            if (type == "pty-watch")
            {
                if (parsed.Cursor is not { } cursor || cursor < 0 ||
                    parsed.WaitMilliseconds is not { } wait || wait is < 0 or > 20000)
                {
                    PostToUi(new { type = "pty-output", conversationId, sessionId, requestId, cursor = 0L, nextCursor = 0L,
                        output = "", done = true, truncated = false, error = "终端游标或等待时间无效。" });
                    return;
                }
                var result = session is null
                    ? new GatewayPtyOutput(cursor, cursor, "", true, false, "对话的终端会话不存在或已关闭。")
                    : await session.WatchAsync(sessionId, cursor, (int)wait, _serviceLifetime.Token).ConfigureAwait(false);
                PostToUi(new { type = "pty-output", conversationId, sessionId, requestId, cursor = result.Cursor,
                    nextCursor = result.NextCursor, output = result.Output, done = result.Done, truncated = result.Truncated, error = result.Error });
                return;
            }

            string? error;
            if (session is null) error = "对话的终端会话不存在或已关闭。";
            else if (type == "pty-user-input") error = await session.UserInputAsync(sessionId, parsed.Input, _serviceLifetime.Token).ConfigureAwait(false);
            else if (type == "pty-user-stop") error = await session.StopTerminalAsync(sessionId, _serviceLifetime.Token).ConfigureAwait(false);
            else if (type == "pty-resize")
            {
                if (parsed.Columns is not { } columns || parsed.Rows is not { } rows ||
                    columns is < 20 or > 500 || rows is < 5 or > 300) error = "终端尺寸无效。";
                else error = session.Resize(sessionId, (int)columns, (int)rows);
            }
            else return;
            PostToUi(new { type = "pty-ack", conversationId, sessionId, requestId, success = error is null, error });
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            if (type == "pty-watch") PostToUi(new { type = "pty-output", conversationId, sessionId, requestId, cursor = 0L, nextCursor = 0L, output = "", done = true, truncated = false, error = "终端读取失败。" });
            else PostToUi(new { type = "pty-ack", conversationId, sessionId, requestId, success = false, error = "终端操作失败。" });
        }
    }

}
