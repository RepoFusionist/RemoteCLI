using System.Diagnostics;
using System.Text;
using Common.Protocol;
using Common.Security;

namespace RemoteCli.Remote;

public sealed record CommandExecutionResult(
    RemoteCommandState State,
    int? ExitCode,
    string? ErrorCode,
    string PowerShellPath,
    string PowerShellVersion);

public sealed class PowerShellCommandRunner
{
    private const int MaximumConcurrentCommands = 2;
    private const int MaximumOutputCharacters = 8 * 1024 * 1024;
    private readonly SemaphoreSlim _commandSlots = new(MaximumConcurrentCommands, MaximumConcurrentCommands);
    private readonly string _powerShellPath;
    private readonly Lazy<Task<string>> _powerShellVersion;

    public PowerShellCommandRunner()
    {
        _powerShellPath = ResolvePowerShellPath();
        _powerShellVersion = new Lazy<Task<string>>(ReadPowerShellVersionAsync, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public string PowerShellPath => _powerShellPath;

    public async Task<CommandExecutionResult> ExecuteAsync(
        string commandText,
        string? workingDirectory,
        TimeSpan timeout,
        Func<OutputStreamKind, string, Task> onOutput,
        CancellationToken cancellationToken,
        RemoteSessionPolicy? accessPolicy = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandText);
        ArgumentNullException.ThrowIfNull(onOutput);
        if (commandText.Length > ProtocolConstants.MaximumCommandCharacters)
        {
            throw new ArgumentOutOfRangeException(nameof(commandText), "Command exceeds the configured character limit.");
        }

        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromHours(24))
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        await _commandSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (accessPolicy is { WorkspaceRestricted: true })
            {
                if (accessPolicy.WorkspaceRoot is null)
                    return new CommandExecutionResult(RemoteCommandState.Failed, null, "workspace_not_selected", _powerShellPath, "unknown");
                return await WindowsCommandSandbox.ExecuteAsync(commandText, workingDirectory, timeout,
                    accessPolicy.WorkspaceRoot, onOutput, cancellationToken).ConfigureAwait(false);
            }
            var powerShellVersion = await _powerShellVersion.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (workingDirectory is not null && !Directory.Exists(workingDirectory))
            {
                return new CommandExecutionResult(RemoteCommandState.Failed, null, "working_directory_not_found", _powerShellPath, powerShellVersion);
            }

            return await ExecuteCoreAsync(commandText, workingDirectory, timeout, powerShellVersion, onOutput, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _commandSlots.Release();
        }
    }

    public async Task<CommandExecutionResult> ExecuteAsync(
        RemoteCommandRequest request,
        Guid authorizedSessionId,
        SessionCommandAuthorizer authorizer,
        Func<OutputStreamKind, string, Task> onOutput,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(authorizer);
        if (request.SessionId != authorizedSessionId)
        {
            return new CommandExecutionResult(RemoteCommandState.Failed, null, "session_mismatch", _powerShellPath, "unknown");
        }

        if (!authorizer.TryAuthorizeCommand(request, DateTimeOffset.UtcNow, out var errorCode))
        {
            return new CommandExecutionResult(RemoteCommandState.Failed, null, errorCode, _powerShellPath, "unknown");
        }

        return await ExecuteAsync(
            request.CommandText,
            request.WorkingDirectory,
            TimeSpan.FromSeconds(request.TimeoutSeconds),
            onOutput,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<CommandExecutionResult> ExecuteCoreAsync(
        string commandText,
        string? workingDirectory,
        TimeSpan timeout,
        string powerShellVersion,
        Func<OutputStreamKind, string, Task> onOutput,
        CancellationToken cancellationToken)
    {
        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        using var process = new Process
        {
            StartInfo = CreateStartInfo(commandText, workingDirectory),
            EnableRaisingEvents = true
        };

        try
        {
            if (!process.Start())
            {
                return new CommandExecutionResult(RemoteCommandState.Failed, null, "process_start_failed", _powerShellPath, powerShellVersion);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return new CommandExecutionResult(RemoteCommandState.Failed, null, "process_start_failed", _powerShellPath, powerShellVersion);
        }

        using var outputBudget = new OutputBudget(MaximumOutputCharacters);
        using var outputSource = CancellationTokenSource.CreateLinkedTokenSource(linkedSource.Token, outputBudget.LimitToken);
        var stdoutTask = ForwardOutputAsync(process.StandardOutput, OutputStreamKind.Stdout, onOutput, outputBudget, outputSource.Token);
        var stderrTask = ForwardOutputAsync(process.StandardError, OutputStreamKind.Stderr, onOutput, outputBudget, outputSource.Token);
        try
        {
            await process.WaitForExitAsync(outputSource.Token).ConfigureAwait(false);
            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKillProcessTree(process);
            await DrainAfterKillAsync(stdoutTask, stderrTask).ConfigureAwait(false);
            if (outputBudget.LimitReached)
            {
                return new CommandExecutionResult(RemoteCommandState.Failed, null, "output_limit", _powerShellPath, powerShellVersion);
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return new CommandExecutionResult(RemoteCommandState.Cancelled, null, "cancelled", _powerShellPath, powerShellVersion);
            }

            return new CommandExecutionResult(RemoteCommandState.TimedOut, null, "timeout", _powerShellPath, powerShellVersion);
        }
        catch (Exception)
        {
            TryKillProcessTree(process);
            await DrainAfterKillAsync(stdoutTask, stderrTask).ConfigureAwait(false);
            return new CommandExecutionResult(RemoteCommandState.Failed, null, "output_read_failed", _powerShellPath, powerShellVersion);
        }

        if (outputBudget.LimitReached)
        {
            return new CommandExecutionResult(RemoteCommandState.Failed, process.ExitCode, "output_limit", _powerShellPath, powerShellVersion);
        }

        var state = process.ExitCode == 0 ? RemoteCommandState.Succeeded : RemoteCommandState.Failed;
        return new CommandExecutionResult(state, process.ExitCode, null, _powerShellPath, powerShellVersion);
    }

    private ProcessStartInfo CreateStartInfo(string commandText, string? workingDirectory)
    {
        var utf8Prefix = "[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false); $OutputEncoding = [Console]::OutputEncoding; ";
        var encodedCommand = Convert.ToBase64String(Encoding.Unicode.GetBytes(utf8Prefix + commandText));
        var startInfo = new ProcessStartInfo
        {
            FileName = _powerShellPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false)
        };

        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-EncodedCommand");
        startInfo.ArgumentList.Add(encodedCommand);
        if (workingDirectory is not null)
        {
            startInfo.WorkingDirectory = Path.GetFullPath(workingDirectory);
        }

        return startInfo;
    }

    private static async Task ForwardOutputAsync(
        StreamReader reader,
        OutputStreamKind stream,
        Func<OutputStreamKind, string, Task> onOutput,
        OutputBudget budget,
        CancellationToken cancellationToken)
    {
        var buffer = new char[4096];
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                return;
            }

            if (!budget.TryReserve(count))
            {
                continue;
            }

            await onOutput(stream, new string(buffer, 0, count)).ConfigureAwait(false);
        }
    }

    private static async Task DrainAfterKillAsync(Task stdoutTask, Task stderrTask)
    {
        try
        {
            await Task.WhenAll(stdoutTask, stderrTask).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException or TimeoutException)
        {
        }
    }

    private static void TryKillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
        }
    }

    private static string ResolvePowerShellPath()
    {
        var pathEntries = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var entry in pathEntries)
        {
            var candidate = Path.Combine(entry, "pwsh.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var windowsPowerShell = Path.Combine(windows, "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        if (File.Exists(windowsPowerShell))
        {
            return windowsPowerShell;
        }

        foreach (var entry in pathEntries)
        {
            var candidate = Path.Combine(entry, "powershell.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException("Neither pwsh.exe nor powershell.exe was found.");
    }

    private async Task<string> ReadPowerShellVersionAsync()
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = _powerShellPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        process.StartInfo.ArgumentList.Add("-NoLogo");
        process.StartInfo.ArgumentList.Add("-NoProfile");
        process.StartInfo.ArgumentList.Add("-NonInteractive");
        process.StartInfo.ArgumentList.Add("-Command");
        process.StartInfo.ArgumentList.Add("$PSVersionTable.PSVersion.ToString()");

        try
        {
            if (!process.Start())
            {
                return "unknown";
            }

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
            return process.ExitCode == 0 && !string.IsNullOrWhiteSpace(stdoutTask.Result)
                ? stdoutTask.Result.Trim()
                : "unknown";
        }
        catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            TryKillProcessTree(process);
            return "unknown";
        }
    }

    private sealed class OutputBudget(int maximumCharacters) : IDisposable
    {
        private long _used;
        private readonly CancellationTokenSource _limitReached = new();

        public bool LimitReached => Volatile.Read(ref _used) > maximumCharacters;
        public CancellationToken LimitToken => _limitReached.Token;

        public bool TryReserve(int characters)
        {
            var total = Interlocked.Add(ref _used, characters);
            if (total > maximumCharacters)
            {
                _limitReached.Cancel();
                return false;
            }

            return true;
        }

        public void Dispose() => _limitReached.Dispose();
    }
}
