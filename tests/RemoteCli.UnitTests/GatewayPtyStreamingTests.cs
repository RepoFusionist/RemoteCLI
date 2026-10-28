using System.Text;
using System.Text.Json;
using RemoteCli.Desktop;

namespace RemoteCli.UnitTests;

public sealed class GatewayPtyStreamingTests
{
    private static JsonElement Args(object value) => JsonSerializer.SerializeToElement(value);

    [Fact]
    public async Task SplitSurrogateWaitsForCompletionAndConsumersRemainIndependent()
    {
        var buffer = new GatewayPtyStreamBuffer();
        buffer.Append("A\uD83D");
        Assert.Equal("A", buffer.TakeForModel());
        var first = buffer.ReadTerminal(0).Output;
        Assert.Equal("A", first.Output);
        Assert.Equal(1, first.NextCursor);
        var awaiting = buffer.ReadTerminal(1);
        Assert.Empty(awaiting.Output.Output);
        Assert.False(awaiting.Signal.IsCompleted);
        buffer.Append("\uDE00B");
        await awaiting.Signal.WaitAsync(TimeSpan.FromSeconds(1));
        var second = buffer.ReadTerminal(1).Output;
        Assert.Equal("😀B", second.Output);
        Assert.Equal(4, second.NextCursor);
        Assert.NotNull(buffer.ReadTerminal(2).Output.Error);
        Assert.Equal("😀B", buffer.TakeForModel());
        buffer.Complete();
        Assert.True(buffer.ReadTerminal(4).Output.Done);
    }

    [Fact]
    public async Task TerminalRequestValuesRemainValidAfterSourceDocumentIsDisposed()
    {
        GatewayPtyRequest request;
        using (var json = JsonDocument.Parse("{\"type\":\"pty-watch\",\"conversationId\":\"c\",\"sessionId\":\"s\",\"requestId\":\"r\",\"cursor\":12,\"waitMilliseconds\":500}"))
            request = GatewayPtyRequest.Parse(json.RootElement);
        await Task.Yield();
        Assert.Equal("pty-watch", request.Type);
        Assert.Equal("c", request.ConversationId);
        Assert.Equal(12, request.Cursor);
        Assert.Equal(500, request.WaitMilliseconds);
    }

    [Fact]
    public async Task UserAndModelReadIndependentOutputAndCanStopTheTerminal()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763)) return;
        await using var session = new GatewayCommandSession(Path.GetTempPath(), (_, _) => Task.FromResult(true), (_, _) => Task.FromResult(false));
        GatewayPtyStarted? started = null;
        session.TerminalStarted += value => started = value;
        var tools = session.Create();
        var initial = await tools[0].Execute(Args(new { cmd = "[Console]::Write('READY'); $k = [Console]::ReadKey($true); [Console]::Write('END' + $k.KeyChar)", tty = true, yield_time_ms = 500, timeout_ms = 10000 }), CancellationToken.None);
        Assert.NotNull(started);
        var id = started!.SessionId;
        Assert.Contains("session_id: " + id, initial);
        var cursor = 0L; var userView = new StringBuilder();
        for (var i = 0; i < 10 && !userView.ToString().Contains("READY"); i++)
        {
            var part = await session.WatchAsync(id, cursor, 1000, CancellationToken.None);
            Assert.Null(part.Error); Assert.Equal(cursor, part.Cursor);
            userView.Append(part.Output); cursor = part.NextCursor;
        }
        Assert.Contains("READY", userView.ToString());
        // Model-originated input still needs its own approval.
        Assert.StartsWith("错误：用户拒绝", await tools[1].Execute(Args(new { session_id = id, chars = "x", yield_time_ms = 100 }), CancellationToken.None));
        Assert.Null(await session.UserInputAsync(id, "z", CancellationToken.None));
        var done = false;
        for (var i = 0; i < 15 && !done; i++)
        {
            var part = await session.WatchAsync(id, cursor, 1000, CancellationToken.None);
            Assert.Null(part.Error); userView.Append(part.Output); cursor = part.NextCursor; done = part.Done;
        }
        Assert.True(done);
        Assert.Contains("ENDz", userView.ToString());
        // UI reads did not consume the model's output; completed sessions remain replayable.
        var model = await tools[1].Execute(Args(new { session_id = id, chars = "", yield_time_ms = 100 }), CancellationToken.None);
        Assert.Contains("ENDz", model);
        var replay = await session.WatchAsync(id, 0, 0, CancellationToken.None);
        Assert.Contains("READY", replay.Output);
        Assert.NotNull(await session.UserInputAsync(id, "other", CancellationToken.None));
        Assert.Null(await session.StopTerminalAsync(id, CancellationToken.None));
    }

    [Fact]
    public async Task WatchUsesBoundedUtf16OffsetsAndReportsTruncation()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763)) return;
        await using var session = new GatewayCommandSession(Path.GetTempPath(), (_, _) => Task.FromResult(true));
        GatewayPtyStarted? started = null;
        session.TerminalStarted += value => started = value;
        await session.Create()[0].Execute(Args(new { cmd = "$s = [char]::ConvertFromUtf32(0x1F600); [Console]::Write(($s * 40000)); [Console]::Write('THE_END')", tty = true, columns = 120, rows = 30, yield_time_ms = 10000, timeout_ms = 30000 }), CancellationToken.None);
        Assert.NotNull(started);
        var cursor = 0L; var text = new StringBuilder(); var truncated = false; var done = false;
        for (var i = 0; i < 30 && !done; i++)
        {
            var part = await session.WatchAsync(started!.SessionId, cursor, 1000, CancellationToken.None);
            Assert.Null(part.Error);
            Assert.Equal(part.Cursor + part.Output.Length, part.NextCursor);
            Assert.True(part.Cursor >= cursor);
            _ = new UTF8Encoding(false, true).GetBytes(part.Output);
            truncated |= part.Truncated; text.Append(part.Output); cursor = part.NextCursor; done = part.Done;
        }
        Assert.True(done);
        Assert.True(truncated);
        Assert.Contains("THE_END", text.ToString());
        Assert.NotNull((await session.WatchAsync(started!.SessionId, cursor + 1, 0, CancellationToken.None)).Error);
    }

    [Fact]
    public async Task IdleSweepKeepsRunningAuthorizedTerminalAndSeparatesConversations()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763)) return;
        var now = DateTimeOffset.UtcNow;
        await using var pool = new GatewayTerminalPool(path => new GatewayCommandSession(path, (_, _) => Task.FromResult(true)), () => now);
        var firstId = Guid.NewGuid().ToString(); var secondId = Guid.NewGuid().ToString();
        var first = await pool.GetAsync(firstId, Path.GetTempPath(), CancellationToken.None);
        GatewayPtyStarted? started = null; first.TerminalStarted += value => started = value;
        await first.Create()[0].Execute(Args(new { cmd = "Start-Sleep -Seconds 15", tty = true, yield_time_ms = 100, timeout_ms = 30000 }), CancellationToken.None);
        Assert.NotNull(started);
        now += TimeSpan.FromMinutes(6);
        var second = await pool.GetAsync(secondId, Path.GetTempPath(), CancellationToken.None);
        Assert.Same(first, await pool.FindAsync(firstId, CancellationToken.None));
        Assert.NotSame(first, second);
        Assert.NotNull((await second.WatchAsync(started!.SessionId, 0, 0, CancellationToken.None)).Error);
        Assert.Null(await first.StopTerminalAsync(started.SessionId, CancellationToken.None));
        await pool.CloseAsync(firstId);
        Assert.Null(await pool.FindAsync(firstId, CancellationToken.None));
    }

    [Fact]
    public async Task CompletedTerminalsDoNotConsumeRunningSlotsAndArchiveIsBounded()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763)) return;
        await using var session = new GatewayCommandSession(Path.GetTempPath(), (_, _) => Task.FromResult(true));
        var ids = new List<string>();
        session.TerminalStarted += started => ids.Add(started.SessionId);
        for (var i = 0; i < 33; i++)
        {
            var result = await session.Create()[0].Execute(Args(new { cmd = "[Console]::Write('done')", tty = true, yield_time_ms = 10000 }), CancellationToken.None);
            Assert.Contains("[exit code: 0]", result);
        }
        Assert.Equal(33, ids.Count);
        Assert.NotNull((await session.WatchAsync(ids[0], 0, 0, CancellationToken.None)).Error);
        Assert.Contains("done", (await session.WatchAsync(ids[^1], 0, 0, CancellationToken.None)).Output);
    }

    [Fact]
    public async Task TimeoutIsBoundedAndDisplayedForApproval()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763)) return;
        var approvals = new List<string>();
        await using var session = new GatewayCommandSession(Path.GetTempPath(), (value, _) => { approvals.Add(value); return Task.FromResult(true); });
        var tool = session.Create()[0];
        Assert.StartsWith("错误：", await tool.Execute(Args(new { cmd = "x", timeout_ms = 999 }), CancellationToken.None));
        Assert.StartsWith("错误：", await tool.Execute(Args(new { cmd = "x", timeout_ms = 3600001 }), CancellationToken.None));
        Assert.Empty(approvals);
        var output = await tool.Execute(Args(new { cmd = "Start-Sleep -Seconds 5", tty = true, yield_time_ms = 10000, timeout_ms = 1000 }), CancellationToken.None);
        Assert.Contains("1000 毫秒", Assert.Single(approvals));
        Assert.Contains("达到运行期限", output);
    }
}
