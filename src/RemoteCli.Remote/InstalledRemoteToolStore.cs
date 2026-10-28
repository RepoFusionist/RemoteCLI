using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Common.Protocol;

namespace RemoteCli.Remote;

public sealed record InstalledRemoteToolManifest(
    string Id,
    string Version,
    string Description,
    string Script,
    string PermissionDescription,
    JsonElement InputSchema,
    JsonElement? OutputSchema);

public sealed class InstalledRemoteToolStore
{
    private static readonly Regex IdPattern = new("^[a-z][a-z0-9_-]{1,62}$", RegexOptions.CultureInvariant);
    private readonly string _root;

    public InstalledRemoteToolStore(string? root = null)
    {
        _root = root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RemoteCLI",
            "Tools");
    }

    public IReadOnlyList<IRemoteTool> LoadInstalled(RemoteSessionPolicy? policy = null)
    {
        if (!Directory.Exists(_root))
        {
            return [];
        }

        var result = new List<IRemoteTool>();
        foreach (var directory in Directory.EnumerateDirectories(_root).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var manifestPath = Path.Combine(directory, "manifest.json");
                var manifest = ReadManifest(manifestPath);
                var scriptPath = Path.Combine(directory, manifest.Script);
                ValidateManifest(manifest, scriptPath);
                result.Add(new InstalledScriptRemoteTool(manifest, scriptPath, policy));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
            {
                Trace.TraceWarning("Ignored invalid installed Remote CLI tool at {0}: {1}", directory, exception.Message);
            }
        }

        return result;
    }

    public async Task<InstalledRemoteToolManifest> InstallAsync(string manifestPath, CancellationToken cancellationToken)
    {
        manifestPath = Path.GetFullPath(manifestPath);
        var manifest = ReadManifest(manifestPath);
        var packageDirectory = Path.GetDirectoryName(manifestPath)
            ?? throw new InvalidDataException("The tool manifest has no package directory.");
        if (Path.GetFileName(manifest.Script) != manifest.Script || !manifest.Script.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The tool script must be a sibling .ps1 file.");
        }

        var scriptPath = Path.GetFullPath(Path.Combine(packageDirectory, manifest.Script));
        ValidateManifest(manifest, scriptPath);
        await ValidatePowerShell51SyntaxAsync(scriptPath, cancellationToken).ConfigureAwait(false);

        Directory.CreateDirectory(_root);
        var destination = Path.Combine(_root, manifest.Id);
        var staging = destination + ".installing-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(staging);
        try
        {
            File.Copy(manifestPath, Path.Combine(staging, "manifest.json"), overwrite: false);
            File.Copy(scriptPath, Path.Combine(staging, manifest.Script), overwrite: false);
            if (Directory.Exists(destination))
            {
                Directory.Delete(destination, recursive: true);
            }

            Directory.Move(staging, destination);
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }
        }

        return manifest;
    }

    private static InstalledRemoteToolManifest ReadManifest(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length is < 2 or > 32 * 1024)
        {
            throw new InvalidDataException("The tool manifest size is invalid.");
        }

        return JsonSerializer.Deserialize<InstalledRemoteToolManifest>(bytes, ProtocolJson.Options)
            ?? throw new InvalidDataException("The tool manifest is empty.");
    }

    private static void ValidateManifest(InstalledRemoteToolManifest manifest, string scriptPath)
    {
        if (!IdPattern.IsMatch(manifest.Id ?? string.Empty) ||
            string.IsNullOrWhiteSpace(manifest.Version) || manifest.Version.Length > 64 ||
            string.IsNullOrWhiteSpace(manifest.Description) || manifest.Description.Length > 2_048 ||
            string.IsNullOrWhiteSpace(manifest.PermissionDescription) || manifest.PermissionDescription.Length > 512 ||
            Path.GetFileName(manifest.Script) != manifest.Script ||
            !manifest.Script.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(scriptPath) ||
            !RemoteToolSchemaValidator.ValidateSchema(manifest.InputSchema, out _) ||
            (manifest.OutputSchema is { } output && !RemoteToolSchemaValidator.ValidateSchema(output, out _)))
        {
            throw new InvalidDataException("The tool manifest contains invalid metadata, schemas, or script path.");
        }
    }

    private static async Task ValidatePowerShell51SyntaxAsync(string scriptPath, CancellationToken cancellationToken)
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var powerShell = Path.Combine(windows, "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!File.Exists(powerShell))
        {
            throw new FileNotFoundException("Windows PowerShell 5.1 is required to validate installed tools.", powerShell);
        }

        var escaped = scriptPath.Replace("'", "''", StringComparison.Ordinal);
        var command = "$errors=$null; [System.Management.Automation.Language.Parser]::ParseFile('" + escaped +
            "',[ref]$null,[ref]$errors)|Out-Null; $errors.Count";
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(command));
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = powerShell,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        process.StartInfo.ArgumentList.Add("-NoLogo");
        process.StartInfo.ArgumentList.Add("-NoProfile");
        process.StartInfo.ArgumentList.Add("-NonInteractive");
        process.StartInfo.ArgumentList.Add("-EncodedCommand");
        process.StartInfo.ArgumentList.Add(encoded);
        if (!process.Start())
        {
            throw new InvalidOperationException("Windows PowerShell 5.1 could not be started.");
        }

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        await Task.WhenAll(outputTask, errorTask).ConfigureAwait(false);
        if (process.ExitCode != 0 || !string.Equals(outputTask.Result.Trim(), "0", StringComparison.Ordinal))
        {
            throw new InvalidDataException("The installed tool script is not valid Windows PowerShell 5.1 syntax.");
        }
    }

    private sealed class InstalledScriptRemoteTool(
        InstalledRemoteToolManifest manifest,
        string scriptPath,
        RemoteSessionPolicy? policy) : IRemoteTool
    {
        public RemoteToolDescriptor Descriptor { get; } = new(
            "user." + manifest.Id,
            manifest.Version,
            manifest.Description,
            manifest.InputSchema.Clone(),
            manifest.OutputSchema?.Clone());

        public string? ApprovalKey => "installed." + manifest.Id;
        public string PermissionDescription => manifest.PermissionDescription;

        public async Task<JsonElement> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken)
        {
            if (policy is not null)
            {
                var script = await File.ReadAllTextAsync(scriptPath, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
                var encodedArguments = Convert.ToBase64String(Encoding.UTF8.GetBytes(arguments.GetRawText()));
                var stdout = new StringBuilder();
                var stderr = new StringBuilder();
                var result = await new PowerShellCommandRunner().ExecuteAsync(
                    "& {\n" + script + "\n} -ArgumentsBase64 '" + encodedArguments + "'",
                    policy.WorkspaceRoot, TimeSpan.FromSeconds(30),
                    (kind, text) => { var buffer = kind == OutputStreamKind.Stdout ? stdout : stderr;
                        if (buffer.Length + text.Length > 8192) throw new RemoteToolException("remote_tool_result_too_large", "工具输出过大。");
                        buffer.Append(text); return Task.CompletedTask; }, cancellationToken, policy).ConfigureAwait(false);
                if (result.State != RemoteCommandState.Succeeded)
                    throw new RemoteToolException(result.ErrorCode ?? "installed_tool_failed", Limit(stderr.ToString(), 512));
                try { using var json = JsonDocument.Parse(stdout.ToString()); return json.RootElement.Clone(); }
                catch (JsonException) { throw new RemoteToolException("installed_tool_invalid_json", "工具没有返回有效 JSON。"); }
            }
            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            var powerShell = Path.Combine(windows, "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
            var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(arguments.GetRawText()));
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = powerShell,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = new UTF8Encoding(false),
                    StandardErrorEncoding = new UTF8Encoding(false)
                }
            };
            process.StartInfo.ArgumentList.Add("-NoLogo");
            process.StartInfo.ArgumentList.Add("-NoProfile");
            process.StartInfo.ArgumentList.Add("-NonInteractive");
            process.StartInfo.ArgumentList.Add("-ExecutionPolicy");
            process.StartInfo.ArgumentList.Add("RemoteSigned");
            process.StartInfo.ArgumentList.Add("-File");
            process.StartInfo.ArgumentList.Add(scriptPath);
            process.StartInfo.ArgumentList.Add("-ArgumentsBase64");
            process.StartInfo.ArgumentList.Add(base64);
            process.StartInfo.WorkingDirectory = Path.GetDirectoryName(scriptPath)!;
            if (!process.Start())
            {
                throw new RemoteToolException("tool_process_start_failed", "The installed tool process could not be started.");
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch (InvalidOperationException)
                {
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }

                throw new RemoteToolException("installed_tool_timeout", "The installed tool exceeded 30 seconds.");
            }

            if (process.ExitCode != 0)
            {
                throw new RemoteToolException(
                    "installed_tool_failed",
                    Limit(string.IsNullOrWhiteSpace(stderrTask.Result)
                        ? "The installed tool returned a failure exit code."
                        : stderrTask.Result.Trim(), 512));
            }

            if (Encoding.UTF8.GetByteCount(stdoutTask.Result) > 16 * 1024)
            {
                throw new RemoteToolException("remote_tool_result_too_large", "The installed tool returned more than 16 KiB.");
            }

            try
            {
                using var document = JsonDocument.Parse(stdoutTask.Result, new JsonDocumentOptions { MaxDepth = 8 });
                return document.RootElement.Clone();
            }
            catch (JsonException)
            {
                throw new RemoteToolException("installed_tool_invalid_json", "The installed tool did not return one valid JSON value.");
            }
        }

        private static string Limit(string value, int maximum) =>
            value.Length <= maximum ? value : value[..maximum];
    }
}
