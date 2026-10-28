using System.Text.Json;
using RemoteCli.Desktop;

namespace RemoteCli.UnitTests;

public sealed class GatewayPtyPatchTests
{
    private static JsonElement Args(object value) => JsonSerializer.SerializeToElement(value);

    [Fact]
    public async Task PatchSupportsMoveAnchorsMultipleHunksAndEof()
    {
        var root = Path.Combine(Path.GetTempPath(), "longshell-patch-plus-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "old.txt"), "head\nfunction one\nvalue\nfunction two\nvalue\nend\n");
            var patch = "*** Begin Patch\n*** Update File: old.txt\n*** Move to: new.txt\n@@ function two\n-value\n+changed\n@@\n end\n+tail\n*** End of File\n\n*** End Patch";
            var tool = new GatewayPatch(root, (_, _) => Task.FromResult(true)).Create();
            var result = await tool.Execute(Args(new { patch }), CancellationToken.None);
            Assert.StartsWith("已应用", result);
            Assert.False(File.Exists(Path.Combine(root, "old.txt")));
            Assert.Equal("head\nfunction one\nvalue\nfunction two\nchanged\nend\ntail\n", await File.ReadAllTextAsync(Path.Combine(root, "new.txt")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task PatchPreservesUtf8BomAndWindowsNewlines()
    {
        var root = Path.Combine(Path.GetTempPath(), "longshell-patch-plus-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "bom.txt");
            var utf8Bom = new System.Text.UTF8Encoding(true);
            await File.WriteAllTextAsync(path, "first\r\nsecond\r\n", utf8Bom);
            var patch = "*** Begin Patch\n*** Update File: bom.txt\n@@\n-first\n+new\n*** End Patch";
            var result = await new GatewayPatch(root, (_, _) => Task.FromResult(true)).Create().Execute(Args(new { patch }), CancellationToken.None);
            Assert.StartsWith("已应用", result);
            var bytes = await File.ReadAllBytesAsync(path);
            Assert.True(bytes.AsSpan().StartsWith(utf8Bom.GetPreamble()));
            Assert.Equal("new\r\nsecond\r\n", utf8Bom.GetString(bytes[3..]));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task PatchDenialAndMoveCollisionLeaveBothFilesUnchanged()
    {
        var root = Path.Combine(Path.GetTempPath(), "longshell-patch-plus-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "old.txt"), "original\n");
            await File.WriteAllTextAsync(Path.Combine(root, "target.txt"), "keep\n");
            var collision = "*** Begin Patch\n*** Update File: old.txt\n*** Move to: target.txt\n@@\n-original\n+new\n*** End Patch";
            var tool = new GatewayPatch(root, (_, _) => Task.FromResult(true)).Create();
            Assert.StartsWith("错误：", await tool.Execute(Args(new { patch = collision }), CancellationToken.None));
            var denied = new GatewayPatch(root, (_, _) => Task.FromResult(false)).Create();
            var move = collision.Replace("target.txt", "new.txt", StringComparison.Ordinal);
            Assert.StartsWith("错误：", await denied.Execute(Args(new { patch = move }), CancellationToken.None));
            Assert.Equal("original\n", await File.ReadAllTextAsync(Path.Combine(root, "old.txt")));
            Assert.Equal("keep\n", await File.ReadAllTextAsync(Path.Combine(root, "target.txt")));
            Assert.False(File.Exists(Path.Combine(root, "new.txt")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task WindowsPtyExposesConsoleAndAcceptsInteractiveInput()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763)) return;
        var approvals = 0;
        await using var session = new GatewayCommandSession(Path.GetTempPath(), (_, _) => { approvals++; return Task.FromResult(true); });
        var tools = session.Create();
        var initial = await tools[0].Execute(Args(new { cmd = "[Console]::WriteLine('TTY=' + [Console]::IsOutputRedirected); $key = [Console]::ReadKey($true); [Console]::WriteLine('key=' + $key.KeyChar); [Console]::WriteLine('size=' + [Console]::WindowWidth + 'x' + [Console]::WindowHeight)", tty = true, columns = 100, rows = 25, yield_time_ms = 500 }), CancellationToken.None);
        var match = System.Text.RegularExpressions.Regex.Match(initial, "session_id: ([0-9a-f]+)");
        Assert.True(match.Success, initial);
        var result = await tools[1].Execute(Args(new { session_id = match.Groups[1].Value, chars = "x", columns = 90, rows = 24, yield_time_ms = 10000 }), CancellationToken.None);
        Assert.True((initial + result).Contains("TTY=False", StringComparison.OrdinalIgnoreCase), initial + "\n---\n" + result);
        Assert.Contains("key=x", initial + result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("size=90x24", initial + result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[exit code: 0]", result);
        Assert.Equal(2, approvals);
    }

    [Fact]
    public async Task WindowsPtyHonorsCommandDuration()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763)) return;
        await using var session = new GatewayCommandSession(Path.GetTempPath(), (_, _) => Task.FromResult(true));
        var tools = session.Create();
        var result = await tools[0].Execute(Args(new { cmd = "[Console]::WriteLine('PTY-BEGIN'); Start-Sleep -Seconds 1; [Console]::WriteLine('PTY-END')", tty = true, yield_time_ms = 100 }), CancellationToken.None);
        var match = System.Text.RegularExpressions.Regex.Match(result, "session_id: ([0-9a-f]+)");
        Assert.True(match.Success, result);
        var done = await tools[1].Execute(Args(new { session_id = match.Groups[1].Value, yield_time_ms = 10000 }), CancellationToken.None);
        Assert.True((result + done).Contains("PTY-BEGIN") && (result + done).Contains("PTY-END"), result + "\n---\n" + done);
    }

    [Fact]
    public async Task WindowsPtyCancellationTerminatesTheOwnedProcess()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763)) return;
        var root = Path.Combine(Path.GetTempPath(), "longshell-pty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var pidPath = Path.Combine(root, "pid.txt");
            var quoted = pidPath.Replace("'", "''", StringComparison.Ordinal);
            using var cancellation = new CancellationTokenSource();
            await using var session = new GatewayCommandSession(root, (_, _) => Task.FromResult(true));
            var pending = session.Create()[0].Execute(Args(new { cmd = "$PID | Set-Content -LiteralPath '" + quoted + "' -Encoding ASCII; Start-Sleep -Seconds 30", tty = true, yield_time_ms = 10000 }), cancellation.Token);
            // The child process can still hold the PID file while it is being written.
            var pidText = "";
            for (var attempt = 0; attempt < 100 && pidText.Length == 0; attempt++)
            {
                if (File.Exists(pidPath))
                {
                    try { pidText = (await File.ReadAllTextAsync(pidPath)).Trim(); }
                    catch (IOException) { }
                }
                if (pidText.Length == 0) await Task.Delay(50);
            }
            Assert.True(pidText.Length > 0, "The test process did not start.");
            var pid = int.Parse(pidText);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            await session.DisposeAsync();
            try
            {
                using var process = System.Diagnostics.Process.GetProcessById(pid);
                Assert.True(process.HasExited);
            }
            catch (ArgumentException) { }
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task WindowsPtyInputRequiresSeparateApproval()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763)) return;
        await using var session = new GatewayCommandSession(Path.GetTempPath(), (_, _) => Task.FromResult(true), (_, _) => Task.FromResult(false));
        var tools = session.Create();
        var initial = await tools[0].Execute(Args(new { cmd = "[Console]::ReadKey($true)", tty = true, yield_time_ms = 300 }), CancellationToken.None);
        var match = System.Text.RegularExpressions.Regex.Match(initial, "session_id: ([0-9a-f]+)");
        Assert.True(match.Success, initial);
        var result = await tools[1].Execute(Args(new { session_id = match.Groups[1].Value, chars = "x", yield_time_ms = 300 }), CancellationToken.None);
        Assert.StartsWith("错误：用户拒绝", result);
    }
}
