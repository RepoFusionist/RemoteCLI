using System.Collections.Concurrent;
using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace RemoteCli.Desktop;

internal sealed record GatewayPtyStarted(string SessionId, short Columns, short Rows);
internal sealed record GatewayPtyResized(string SessionId, short Columns, short Rows);
internal sealed record GatewayPtyOutput(long Cursor, long NextCursor, string Output, bool Done, bool Truncated, string? Error = null);
internal sealed record GatewayPtyRequest(string Type, string ConversationId, string SessionId, string RequestId, string Input,
    long? Cursor, long? WaitMilliseconds, long? Columns, long? Rows)
{
    internal static GatewayPtyRequest Parse(JsonElement request)
    {
        static string Text(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() ?? "" : "";
        static long? Number(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out var number) ? number : null;
        return new GatewayPtyRequest(Text(request, "type"), Text(request, "conversationId"), Text(request, "sessionId"), Text(request, "requestId"), Text(request, "input"),
            Number(request, "cursor"), Number(request, "waitMilliseconds"), Number(request, "columns"), Number(request, "rows"));
    }
}

// Shared bounded output history with independent cursors for the model and UI watchers.
internal sealed class GatewayPtyStreamBuffer
{
    private const int MaximumRetainedCharacters = 65536;
    private const int MaximumWatchCharacters = 8192;
    private readonly object gate = new();
    private readonly StringBuilder output = new();
    private TaskCompletionSource<bool> changed = NewSignal();
    private long firstCursor;
    private long modelCursor;
    private bool finished;

    internal void Append(string value)
    {
        lock (gate)
        {
            output.Append(value);
            if (output.Length > MaximumRetainedCharacters)
            {
                var remove = output.Length - MaximumRetainedCharacters;
                if (remove < output.Length && char.IsLowSurrogate(output[remove]) && remove > 0 && char.IsHighSurrogate(output[remove - 1])) remove++;
                output.Remove(0, remove); firstCursor += remove;
            }
            Pulse();
        }
    }

    internal void Complete() { lock (gate) { finished = true; Pulse(); } }

    internal string TakeForModel()
    {
        lock (gate)
        {
            var truncated = modelCursor < firstCursor;
            var start = Math.Max(modelCursor, firstCursor);
            var index = (int)(start - firstCursor);
            var end = output.Length;
            if (!finished && end > index && char.IsHighSurrogate(output[end - 1])) end--;
            var text = output.ToString(index, end - index);
            modelCursor = firstCursor + end;
            return truncated ? "（较早输出已截断）\n" + text : text;
        }
    }

    internal (GatewayPtyOutput Output, Task Signal) ReadTerminal(long cursor)
    {
        lock (gate)
        {
            var end = firstCursor + output.Length;
            if (cursor > end) return (new GatewayPtyOutput(cursor, cursor, "", finished, false, "终端游标超出当前输出。"), changed.Task);
            var start = Math.Max(cursor, firstCursor);
            var index = (int)(start - firstCursor);
            if (index > 0 && index < output.Length && char.IsLowSurrogate(output[index]) && char.IsHighSurrogate(output[index - 1]))
                return (new GatewayPtyOutput(cursor, cursor, "", finished, false, "终端游标不能落在字符中间。"), changed.Task);
            var length = Math.Min(MaximumWatchCharacters, output.Length - index);
            if (length > 0 && char.IsHighSurrogate(output[index + length - 1]) &&
                (index + length < output.Length ? char.IsLowSurrogate(output[index + length]) : !finished)) length--;
            var next = start + length;
            return (new GatewayPtyOutput(start, next, output.ToString(index, length), finished && next == end, cursor < firstCursor), changed.Task);
        }
    }

    private static TaskCompletionSource<bool> NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    // Caller holds gate.
    private void Pulse() { var previous = changed; changed = NewSignal(); previous.TrySetResult(true); }
}

// The owner decides session lifetime; DisposeAsync terminates all owned processes.
internal sealed class GatewayCommandSession(string workspace, Func<string, CancellationToken, Task<bool>> approve,
    Func<string, CancellationToken, Task<bool>>? approveInput = null) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, Command> commands = new();
    private readonly object sessionGate = new();
    private Func<string, CancellationToken, Task<bool>> _approve = approve;
    private Func<string, CancellationToken, Task<bool>>? _approveInput = approveInput;
    private bool stopped;
    internal string Workspace => workspace;
    internal bool HasRunningCommands => commands.Values.Any(item => !item.Completion.IsCompleted);
    internal event Action<GatewayPtyStarted>? TerminalStarted;
    internal event Action<GatewayPtyResized>? TerminalResized;

    internal void SetApproval(Func<string, CancellationToken, Task<bool>> approval,
        Func<string, CancellationToken, Task<bool>>? approvalInput = null)
    {
        _approve = approval;
        _approveInput = approvalInput ?? approval;
    }

    internal IReadOnlyList<LocalChatTool> Create() =>
    [
        new("exec_command", "运行 Windows PowerShell 5.1 命令；tty=true 使用 Windows ConPTY。每次启动前由本机用户审批。工作目录不是沙箱，命令可访问本机和网络。长任务返回 session_id；timeout_ms 默认 120000，范围 1000–3600000。PowerShell 5.1 的 Get-Content 默认可能按 ANSI 读取 UTF-8，读取中文文件时请显式使用 -Encoding UTF8。", """{"type":"object","properties":{"cmd":{"type":"string"},"yield_time_ms":{"type":"integer"},"tty":{"type":"boolean"},"columns":{"type":"integer"},"rows":{"type":"integer"},"timeout_ms":{"type":"integer"}},"required":["cmd"],"additionalProperties":false}""", StartAsync),
        new("write_stdin", "向当前对话启动的命令发送输入或读取后续输出；非空输入需要用户审批。可用 columns 和 rows 调整 PTY 尺寸。", """{"type":"object","properties":{"session_id":{"type":"string"},"chars":{"type":"string"},"yield_time_ms":{"type":"integer"},"columns":{"type":"integer"},"rows":{"type":"integer"}},"required":["session_id"],"additionalProperties":false}""", WriteAsync)
    ];

    private async Task<string> StartAsync(JsonElement args, CancellationToken token)
    {
        var script = ReadString(args, "cmd");
        if (string.IsNullOrWhiteSpace(script) || script.Length > 2000 || script.Contains('\0')) return "错误：命令必须为 1 至 2000 个字符。";
        lock (sessionGate) { if (stopped) return "错误：命令会话已关闭。"; }
        if (!TryDimensions(args, out var columns, out var rows, out _)) return "错误：终端宽度和高度必须分别为 20–500 和 5–300。";
        if (args.TryGetProperty("tty", out var ttyArg) && ttyArg.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return "错误：tty 必须是布尔值。";
        var useTty = ttyArg.ValueKind == JsonValueKind.True;
        var timeoutMilliseconds = 120000;
        if (args.TryGetProperty("timeout_ms", out var timeoutArg) && (timeoutArg.ValueKind != JsonValueKind.Number || !timeoutArg.TryGetInt32(out timeoutMilliseconds) || timeoutMilliseconds is < 1000 or > 3600000))
            return "错误：timeout_ms 必须为 1000 至 3600000。";
        if (commands.Values.Count(item => !item.Completion.IsCompleted) >= 4) return "错误：当前对话的命令数量已达上限。";
        // Keep meaningful approval metadata in a PowerShell comment so the 5.1
        // classifier parses the actual script without treating UI prose as commands.
        if (!await _approve("# 终端模式：" + (useTty ? "交互式 ConPTY" : "管道") + "；最长运行：" + timeoutMilliseconds + " 毫秒\n" + script, token).ConfigureAwait(false)) return "错误：用户拒绝执行命令。";
        token.ThrowIfCancellationRequested();
        var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!File.Exists(executable)) return "错误：未找到 Windows PowerShell 5.1。";
        var info = new ProcessStartInfo(executable) { WorkingDirectory = workspace, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        var wrappedScript = "[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false); $OutputEncoding = [Console]::OutputEncoding; & {\n" + script + "\n}";
        info.ArgumentList.Add("-NoLogo"); info.ArgumentList.Add("-NoProfile"); info.ArgumentList.Add("-NonInteractive"); info.ArgumentList.Add("-Command"); info.ArgumentList.Add(wrappedScript);
        Process process;
        NativePty? terminal = null;
        try
        {
            if (useTty)
            {
                terminal = NativePty.Start(executable, "& {\n" + script + "\n}", workspace, columns, rows);
                process = terminal.Process;
            }
            else
            {
                process = new Process { StartInfo = info };
                if (!process.Start()) { process.Dispose(); return "错误：命令无法启动。"; }
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException or PlatformNotSupportedException or EntryPointNotFoundException)
        { terminal?.Dispose(); return "错误：命令无法启动：" + ex.Message; }
        var id = Guid.NewGuid().ToString("N");
        var command = new Command(process, terminal, columns, rows, timeoutMilliseconds);
        lock (sessionGate)
        {
            if (!stopped)
            {
                // Completed sessions remain replayable for the UI, but bound the retained archive.
                if (commands.Count >= 32)
                {
                    var oldest = commands.Where(item => item.Value.Completion.IsCompleted).OrderBy(item => item.Value.CreatedAt).FirstOrDefault();
                    if (!string.IsNullOrEmpty(oldest.Key) && commands.TryRemove(oldest.Key, out var expired)) expired.DisposeResources();
                }
                commands[id] = command;
            }
        }
        if (!commands.ContainsKey(id))
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { }
            try { await command.Completion.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
            catch (Exception ex) when (ex is TimeoutException or IOException or InvalidOperationException) { }
            command.DisposeResources();
            return "错误：命令会话已关闭。";
        }
        if (terminal is not null)
        {
            try { TerminalStarted?.Invoke(new GatewayPtyStarted(id, columns, rows)); }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException) { }
        }
        return await ReadAsync(id, command, WaitMilliseconds(args), token).ConfigureAwait(false);
    }

    private async Task<string> WriteAsync(JsonElement args, CancellationToken token)
    {
        var id = ReadString(args, "session_id");
        if (!commands.TryGetValue(id, out var command)) return "错误：命令会话不存在或已结束。";
        if (!TryDimensions(args, out var columns, out var rows, out var resize)) return "错误：终端宽度和高度必须分别为 20–500 和 5–300。";
        if (resize)
        {
            if (command.Terminal is null) return "错误：该命令不是 PTY 会话。";
            var current = command.Size;
            if (!args.TryGetProperty("columns", out _)) columns = current.Columns;
            if (!args.TryGetProperty("rows", out _)) rows = current.Rows;
            try
            {
                command.Resize(columns, rows);
                TerminalResized?.Invoke(new GatewayPtyResized(id, columns, rows));
            }
            catch (Win32Exception ex) { return "错误：无法调整终端大小：" + ex.Message; }
        }
        var input = ReadString(args, "chars");
        if (input.Length > 8192) return "错误：输入超过 8192 字符。";
        if (input.Length > 0 && !command.Completion.IsCompleted)
        {
            if (!await (_approveInput ?? _approve)("会话 " + id + " 的标准输入：\n" + input, token).ConfigureAwait(false))
                return "错误：用户拒绝发送命令输入。";
            token.ThrowIfCancellationRequested();
            using var cancellation = token.Register(() =>
            {
                try { if (!command.Process.HasExited) command.Process.Kill(entireProcessTree: true); }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { }
            });
            try { await command.WriteInputAsync(input, token).ConfigureAwait(false); }
            catch (IOException) { }
            catch (InvalidOperationException) { }
        }
        return await ReadAsync(id, command, WaitMilliseconds(args), token).ConfigureAwait(false);
    }

    private async Task<string> ReadAsync(string id, Command command, int wait, CancellationToken token)
    {
        await Task.WhenAny(command.Completion, Task.Delay(wait, token)).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (command.Completion.IsCompleted)
        {
            await command.Completion.ConfigureAwait(false);
            var output = command.TakeOutput();
            return output + (command.TimedOut ? "\n[命令达到运行期限并已终止]" : "") + "\n[exit code: " + command.ExitCode + "]";
        }
        return "[运行中 session_id: " + id + "]\n" + command.TakeOutput();
    }

    internal async Task<GatewayPtyOutput> WatchAsync(string sessionId, long cursor, int waitMilliseconds, CancellationToken token)
    {
        if (cursor < 0 || waitMilliseconds is < 0 or > 20000) return new(cursor, cursor, "", true, false, "终端游标或等待时间无效。");
        if (!commands.TryGetValue(sessionId, out var command) || command.Terminal is null)
            return new(cursor, cursor, "", true, false, "终端会话不存在或已关闭。");
        var (initial, signal) = command.ReadTerminal(cursor);
        if (initial.Error is not null || initial.Output.Length > 0 || initial.Done || waitMilliseconds == 0) return initial;
        await Task.WhenAny(signal, Task.Delay(waitMilliseconds, token)).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        return command.ReadTerminal(cursor).Output;
    }

    // This entry point is exclusively for direct user interaction from the local WebView.
    internal async Task<string?> UserInputAsync(string sessionId, string input, CancellationToken token)
    {
        if (input.Length is 0 or > 8192) return "终端输入必须为 1 至 8192 个字符。";
        if (!commands.TryGetValue(sessionId, out var command) || command.Terminal is null || command.Completion.IsCompleted)
            return "终端会话不存在或已结束。";
        try { await command.WriteInputAsync(input, token).ConfigureAwait(false); return null; }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException) { return "终端输入失败。"; }
    }

    internal string? Resize(string sessionId, int columns, int rows)
    {
        if (columns is < 20 or > 500 || rows is < 5 or > 300) return "终端宽度和高度必须分别为 20–500 和 5–300。";
        if (!commands.TryGetValue(sessionId, out var command) || command.Terminal is null || command.Completion.IsCompleted)
            return "终端会话不存在或已结束。";
        try { command.Resize((short)columns, (short)rows); return null; }
        catch (Win32Exception) { return "无法调整终端大小。"; }
    }

    internal async Task<string?> StopTerminalAsync(string sessionId, CancellationToken token)
    {
        if (!commands.TryGetValue(sessionId, out var command) || command.Terminal is null) return "终端会话不存在或已关闭。";
        if (command.Completion.IsCompleted) return null;
        try
        {
            if (!command.Process.HasExited) command.Process.Kill(entireProcessTree: true);
            await command.Completion.WaitAsync(TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
            return null;
        }
        catch (TimeoutException) { return "终端进程未能在限定时间内退出。"; }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or IOException) { return "无法停止终端进程。"; }
    }

    public async ValueTask DisposeAsync()
    {
        Command[] pending;
        lock (sessionGate)
        {
            if (stopped) return;
            stopped = true;
            pending = commands.Values.ToArray();
            commands.Clear();
        }
        foreach (var command in pending)
        {
            try { if (!command.Process.HasExited) command.Process.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
        foreach (var command in pending)
        {
            try { await command.Completion.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
            catch (Exception ex) when (ex is TimeoutException or IOException or InvalidOperationException) { }
            command.DisposeResources();
        }
    }

    private static int WaitMilliseconds(JsonElement args) => args.TryGetProperty("yield_time_ms", out var value) && value.TryGetInt32(out var wait) ? Math.Clamp(wait, 100, 10000) : 1000;
    private static string ReadString(JsonElement args, string name) => args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    private static bool TryDimensions(JsonElement args, out short columns, out short rows, out bool present)
    {
        columns = 120; rows = 30; present = false;
        if (args.TryGetProperty("columns", out var c)) { present = true; if (c.ValueKind != JsonValueKind.Number || !c.TryGetInt16(out columns) || columns is < 20 or > 500) return false; }
        if (args.TryGetProperty("rows", out var r)) { present = true; if (r.ValueKind != JsonValueKind.Number || !r.TryGetInt16(out rows) || rows is < 5 or > 300) return false; }
        return true;
    }

    private sealed class Command
    {
        private readonly GatewayPtyStreamBuffer output = new();
        private readonly SemaphoreSlim inputGate = new(1, 1);
        private readonly object sizeGate = new();
        private int disposed;
        internal Process Process { get; }
        internal NativePty? Terminal { get; }
        internal TextWriter Input => Terminal?.Input ?? Process.StandardInput;
        internal Task Completion { get; }
        internal DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;
        internal int ExitCode { get; private set; }
        internal bool TimedOut { get; private set; }
        private short columns;
        private short rows;
        private readonly int timeoutMilliseconds;
        internal (short Columns, short Rows) Size { get { lock (sizeGate) return (columns, rows); } }
        internal Command(Process process, NativePty? terminal, short initialColumns, short initialRows, int timeoutMilliseconds)
        {
            Process = process;
            Terminal = terminal;
            columns = initialColumns;
            rows = initialRows;
            this.timeoutMilliseconds = timeoutMilliseconds;
            Completion = CompleteAsync();
        }
        internal void Resize(short width, short height)
        {
            lock (sizeGate) { Terminal!.Resize(width, height); columns = width; rows = height; }
        }
        private async Task CompleteAsync()
        {
            try
            {
                var stdout = DrainAsync(Terminal?.Output ?? Process.StandardOutput, "");
                var stderr = Terminal is null ? DrainAsync(Process.StandardError, "[stderr] ") : Task.CompletedTask;
                using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(timeoutMilliseconds));
                try { await Process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { TimedOut = true; try { Process.Kill(entireProcessTree: true); } catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { } await Process.WaitForExitAsync().ConfigureAwait(false); }
                if (Terminal is not null)
                {
                    // ConPTY renders screen updates asynchronously after the child exits.
                    await Task.Delay(150).ConfigureAwait(false);
                    Terminal.CloseConsole();
                }
                await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            }
            finally
            {
                try { ExitCode = Process.ExitCode; }
                catch (InvalidOperationException) { ExitCode = -1; }
                output.Complete();
                DisposeResources();
            }
        }
        private async Task DrainAsync(StreamReader reader, string prefix)
        {
            var buffer = new char[2048]; int count;
            while ((count = await reader.ReadAsync(buffer).ConfigureAwait(false)) > 0)
            {
                output.Append(prefix + new string(buffer, 0, count));
            }
        }
        internal string TakeOutput() => output.TakeForModel();
        internal (GatewayPtyOutput Output, Task Signal) ReadTerminal(long cursor) => output.ReadTerminal(cursor);
        internal async Task WriteInputAsync(string text, CancellationToken token)
        {
            await inputGate.WaitAsync(token).ConfigureAwait(false);
            try { await Input.WriteAsync(text.AsMemory(), token).ConfigureAwait(false); await Input.FlushAsync(token).ConfigureAwait(false); }
            finally { inputGate.Release(); }
        }
        internal void DisposeResources()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            Terminal?.Dispose();
            Process.Dispose();
        }
    }

    // ConPTY uses separate synchronous pipes. Output is drained concurrently while the
    // process is running and while the pseudo console is closed, avoiding close deadlocks.
    private sealed class NativePty : IDisposable
    {
        private IntPtr console;
        private readonly object consoleGate = new();
        internal Process Process { get; }
        internal StreamWriter Input { get; }
        internal StreamReader Output { get; }

        private NativePty(Process process, IntPtr consoleHandle, SafeFileHandle input, SafeFileHandle output)
        {
            Process = process;
            console = consoleHandle;
            Input = new StreamWriter(new FileStream(input, FileAccess.Write, 4096, isAsync: false), new UTF8Encoding(false)) { AutoFlush = true };
            Output = new StreamReader(new FileStream(output, FileAccess.Read, 4096, isAsync: false), Encoding.UTF8, false);
        }

        internal static NativePty Start(string executable, string script, string workingDirectory, short columns, short rows)
        {
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763)) throw new PlatformNotSupportedException("PTY 需要 Windows 10 1809 或更新版本。");
            SafeFileHandle? inputRead = null, inputWrite = null, outputRead = null, outputWrite = null;
            IntPtr pc = IntPtr.Zero, attributes = IntPtr.Zero;
            ProcessInformation processInfo = default;
            var created = false; var initialized = false;
            try
            {
                if (!CreatePipe(out inputRead, out inputWrite, IntPtr.Zero, 0) || !CreatePipe(out outputRead, out outputWrite, IntPtr.Zero, 0))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                var result = CreatePseudoConsole(new Coord(columns, rows), inputRead, outputWrite, 0, out pc);
                if (result != 0) throw new Win32Exception(result, "无法创建 Windows 伪终端。");
                IntPtr size = IntPtr.Zero;
                InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
                if (size == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
                attributes = Marshal.AllocHGlobal(size);
                if (!InitializeProcThreadAttributeList(attributes, 1, 0, ref size)) throw new Win32Exception(Marshal.GetLastWin32Error());
                initialized = true;
                if (!UpdateProcThreadAttribute(attributes, 0, (IntPtr)0x00020016, pc, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                // Prevent Windows from duplicating redirected parent standard handles into
                // the child; null explicit handles let the pseudoconsole supply its own.
                var startup = new StartupInfoEx { StartupInfo = new StartupInfo { cb = Marshal.SizeOf<StartupInfoEx>(), flags = 0x100 }, AttributeList = attributes };
                var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
                var commandLine = new StringBuilder("\"" + executable + "\" -NoLogo -NoProfile -NonInteractive -EncodedCommand " + encoded);
                if (!CreateProcessW(executable, commandLine, IntPtr.Zero, IntPtr.Zero, false, 0x00080000, IntPtr.Zero, workingDirectory, ref startup, out processInfo))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                created = true;
                inputRead.Dispose(); inputRead = null;
                outputWrite.Dispose(); outputWrite = null;
                var process = Process.GetProcessById(unchecked((int)processInfo.ProcessId));
                var terminal = new NativePty(process, pc, inputWrite, outputRead);
                inputWrite = null; outputRead = null; pc = IntPtr.Zero; created = false;
                return terminal;
            }
            finally
            {
                if (created && processInfo.Process != IntPtr.Zero) TerminateProcess(processInfo.Process, 1);
                if (processInfo.Thread != IntPtr.Zero) CloseHandle(processInfo.Thread);
                if (processInfo.Process != IntPtr.Zero) CloseHandle(processInfo.Process);
                if (attributes != IntPtr.Zero) { if (initialized) DeleteProcThreadAttributeList(attributes); Marshal.FreeHGlobal(attributes); }
                inputRead?.Dispose(); inputWrite?.Dispose(); outputRead?.Dispose(); outputWrite?.Dispose();
                if (pc != IntPtr.Zero) ClosePseudoConsole(pc);
            }
        }

        internal void Resize(short columns, short rows)
        {
            lock (consoleGate)
            {
                if (console == IntPtr.Zero) throw new Win32Exception("终端已经关闭。");
                var result = ResizePseudoConsole(console, new Coord(columns, rows));
                if (result != 0) throw new Win32Exception(result, "调整终端大小失败。");
            }
        }
        internal void CloseConsole()
        {
            lock (consoleGate)
            {
                var handle = console; console = IntPtr.Zero;
                if (handle != IntPtr.Zero) ClosePseudoConsole(handle);
            }
        }
        public void Dispose() { Input.Dispose(); CloseConsole(); Output.Dispose(); }

        [StructLayout(LayoutKind.Sequential)] private readonly struct Coord(short x, short y) { public readonly short X = x; public readonly short Y = y; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct StartupInfo
        {
            public int cb; public IntPtr reserved, desktop, title;
            public int x, y, xSize, ySize, xChars, yChars, fillAttribute, flags;
            public short showWindow, reserved2; public IntPtr reservedPointer, stdInput, stdOutput, stdError;
        }
        [StructLayout(LayoutKind.Sequential)] private struct StartupInfoEx { public StartupInfo StartupInfo; public IntPtr AttributeList; }
        [StructLayout(LayoutKind.Sequential)] private struct ProcessInformation { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, IntPtr security, int size);
        [DllImport("kernel32.dll")] private static extern int CreatePseudoConsole(Coord size, SafeFileHandle input, SafeFileHandle output, uint flags, out IntPtr console);
        [DllImport("kernel32.dll")] private static extern int ResizePseudoConsole(IntPtr console, Coord size);
        [DllImport("kernel32.dll")] private static extern void ClosePseudoConsole(IntPtr console);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previous, IntPtr returnSize);
        [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(IntPtr list);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateProcessW(string applicationName, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles, uint creationFlags, IntPtr environment, string currentDirectory, ref StartupInfoEx startup, out ProcessInformation information);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateProcess(IntPtr process, uint code);
    }
}
