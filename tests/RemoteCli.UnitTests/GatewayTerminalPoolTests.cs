using System.Text.Json;
using System.Text.RegularExpressions;
using RemoteCli.Desktop;

namespace RemoteCli.UnitTests;

public sealed class GatewayTerminalPoolTests
{
    [Fact]
    public async Task SameConversationCanResumeProcessAndClosingRemovesIt()
    {
        if (!OperatingSystem.IsWindows()) return;
        var conversation = Guid.NewGuid().ToString();
        await using var pool = new GatewayTerminalPool(path => new GatewayCommandSession(path, (_, _) => Task.FromResult(true)));
        var first = await pool.GetAsync(conversation, Path.GetTempPath(), CancellationToken.None);
        var initial = await first.Create()[0].Execute(JsonSerializer.SerializeToElement(new { cmd = "[Console]::WriteLine([Console]::ReadLine())", yield_time_ms = 100 }), CancellationToken.None);
        var match = Regex.Match(initial, "session_id: ([0-9a-f]+)");
        Assert.True(match.Success, initial);
        var resumed = await pool.GetAsync(conversation, Path.GetTempPath(), CancellationToken.None);
        Assert.Same(first, resumed);
        var output = await resumed.Create()[1].Execute(JsonSerializer.SerializeToElement(new { session_id = match.Groups[1].Value, chars = "resume-test\n", yield_time_ms = 10000 }), CancellationToken.None);
        Assert.Contains("resume-test", output);
        Assert.Contains("[exit code: 0]", output);
        await pool.CloseAsync(conversation);
        var replacement = await pool.GetAsync(conversation, Path.GetTempPath(), CancellationToken.None);
        Assert.NotSame(first, replacement);
    }
}
