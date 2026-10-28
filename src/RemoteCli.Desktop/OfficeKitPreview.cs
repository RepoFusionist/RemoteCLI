using System.Diagnostics;
using System.Text;

namespace RemoteCli.Desktop;

// Runs the DSH LibreOfficeKit helper in a short-lived, hidden, isolated process.
internal static class OfficeKitPreview
{
    private static readonly SemaphoreSlim Slots = new(2, 2);
    private static int _pending;

    internal static async Task<byte[]> ConvertAsync(byte[] input, string extension, string engineRoot, CancellationToken cancellationToken)
    {
        extension = extension.ToLowerInvariant();
        if (extension is not (".doc" or ".docx" or ".xls" or ".xlsx"))
            throw new InvalidDataException("不支持此 Office 格式。");
        DocumentPreview.ValidateOfficeContainer(input, extension);
        if (Interlocked.Increment(ref _pending) > 4)
        {
            Interlocked.Decrement(ref _pending);
            throw new InvalidOperationException("文档预览任务过多，请稍后重试。");
        }
        var acquired = false;
        string? scratch = null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            await Slots.WaitAsync(timeout.Token).ConfigureAwait(false);
            acquired = true;
            var executable = Path.Combine(engineRoot, "bin", "libreoffice-kit.exe");
            var program = Path.Combine(engineRoot, "program", "program");
            if (!File.Exists(executable) || !Directory.Exists(program))
                throw new InvalidOperationException("Office 预览引擎不可用。");

            var parent = Path.Combine(Path.GetTempPath(), "RemoteCLI", "office-preview");
            scratch = Path.Combine(parent, Guid.NewGuid().ToString("N"));
            var profile = Path.Combine(scratch, "profile");
            Directory.CreateDirectory(profile);
            var source = Path.Combine(scratch, "input" + extension);
            var output = Path.Combine(scratch, "preview.pdf");
            var renderInput = extension == ".xlsx" ? DocumentPreview.FitExcelPreviewToWidth(input) : input;
            await File.WriteAllBytesAsync(source, renderInput, timeout.Token).ConfigureAwait(false);
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            foreach (var argument in new[] { "--program-directory", program, "--input-path", source, "--output-path", output,
                "--profile-directory", profile, "--max-output-bytes", DocumentPreview.MaxFileBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "--max-image-resolution", "144", "--format", "pdf", "--recalculate", "false" })
                start.ArgumentList.Add(argument);
            var allowedEnvironment = new[] { "PATH", "SystemRoot", "WINDIR", "COMSPEC", "PATHEXT", "LANG", "LC_ALL", "TZ" };
            var inherited = allowedEnvironment.Select(key => (key, value: Environment.GetEnvironmentVariable(key)))
                .Where(item => item.value is not null).ToArray();
            start.Environment.Clear();
            foreach (var (key, value) in inherited) start.Environment[key] = value;
            foreach (var key in new[] { "HOME", "USERPROFILE", "TMPDIR", "TMP", "TEMP" }) start.Environment[key] = profile;
            using var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动 Office 预览引擎。");
            var stdout = DrainAsync(process.StandardOutput);
            var stderr = DrainAsync(process.StandardError);
            try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                throw;
            }
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            if (process.ExitCode != 0 || !File.Exists(output)) throw new InvalidDataException("文档无法转换，可能已损坏或受密码保护。");
            using var resultStream = new FileStream(output, FileMode.Open, FileAccess.Read, FileShare.Read);
            var result = DocumentPreview.ReadBounded(resultStream);
            if (!DocumentPreview.IsPdf(result)) throw new InvalidDataException("文档预览输出无效。");
            return result;
        }
        finally
        {
            if (scratch is not null)
            {
                try { Directory.Delete(scratch, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            if (acquired) Slots.Release();
            Interlocked.Decrement(ref _pending);
        }
    }

    private static async Task DrainAsync(StreamReader reader)
    {
        var buffer = new char[4096];
        while (await reader.ReadAsync(buffer).ConfigureAwait(false) != 0) { }
    }
}
