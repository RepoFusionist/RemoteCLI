using System.Formats.Tar;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Net.Http;

namespace RemoteCli.Desktop;

// The DSH LibreOfficeKit package is an optional sidecar/download asset. The CLI stays small
// until an Office document is actually previewed.
internal static class OfficeKitAssets
{
    internal const string PackageFileName = "libreoffice-kit-win32-x64-0.1.2.tgz";
    private const string ResourceName = "RemoteCli.Desktop.Assets.libreoffice-kit-win32-x64-0.1.2.tgz";
    private const string PackageUrl = "https://registry.npmjs.org/@deepseek-ai/libreoffice-kit-win32-x64/-/libreoffice-kit-win32-x64-0.1.2.tgz";
    private const string PackageSha512 = "EUuTMnFF404uVVZz0j2oAoHY0Xhobdg0jChUUm74sudwaqxTM9rvgUYEWbeJd95oVjIParMdsb4hLdAhZ10BIw==";
    private const string ManifestSha256 = "3BC62B6B8B35E880F3FBE74E03DC217197A3EA8CF5859658B55E9966FD2E2E73";
    private static readonly SemaphoreSlim InstallLock = new(1, 1);
    private static readonly HttpClient PackageClient = CreatePackageClient();
    private static string? _verifiedDefault;

    internal static async Task<string> EnsureInstalledAsync(CancellationToken cancellationToken, string? cacheRoot = null, Assembly? assembly = null)
    {
        if (!OperatingSystem.IsWindows() || System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture != System.Runtime.InteropServices.Architecture.X64)
            throw new InvalidOperationException("Office 高保真预览仅支持 Windows x64。");
        var isDefault = cacheRoot is null;
        if (isDefault && _verifiedDefault is not null) return _verifiedDefault;
        cacheRoot ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RemoteCLI", "OfficeKit");
        var final = Path.Combine(cacheRoot, "win-x64-0.1.2-114b93327145");
        await InstallLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (isDefault && _verifiedDefault is not null) return _verifiedDefault;
            if (Directory.Exists(final))
            {
                VerifyInstalled(final);
                if (isDefault) _verifiedDefault = final;
                return final;
            }
            Directory.CreateDirectory(cacheRoot);
            var staging = Path.Combine(cacheRoot, "staging-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            try
            {
                using var package = await OpenPackageAsync(cacheRoot, assembly, cancellationToken).ConfigureAwait(false);
                using var gzip = new GZipStream(package, CompressionMode.Decompress);
                using var tar = new TarReader(gzip);
                long total = 0;
                var count = 0;
                TarEntry? entry;
                while ((entry = tar.GetNextEntry()) is not null)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (++count > 2500 || entry.Length < 0 || entry.Length > 256L * 1024 * 1024 - total)
                        throw new InvalidDataException("Office 预览引擎资源超出限制。");
                    total += entry.Length;
                    if (entry.Name == "package" || entry.Name == "package/") continue;
                    if (!entry.Name.StartsWith("package/", StringComparison.Ordinal)) throw new InvalidDataException("Office 预览引擎路径无效。");
                    var relative = entry.Name[8..].Replace('/', Path.DirectorySeparatorChar);
                    if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(':'))
                        throw new InvalidDataException("Office 预览引擎路径无效。");
                    var destination = Path.GetFullPath(Path.Combine(staging, relative));
                    var prefix = Path.GetFullPath(staging).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                    if (!destination.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Office 预览引擎路径无效。");
                    if (entry.EntryType == TarEntryType.Directory) { Directory.CreateDirectory(destination); continue; }
                    if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile) || entry.DataStream is null)
                        throw new InvalidDataException("Office 预览引擎包含不支持的条目。");
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    await entry.DataStream.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                }
                VerifyFiles(staging);
                await File.WriteAllTextAsync(Path.Combine(staging, ".complete"), PackageSha512, cancellationToken).ConfigureAwait(false);
                try { Directory.Move(staging, final); }
                catch (IOException) when (Directory.Exists(final)) { VerifyInstalled(final); }
                if (isDefault) _verifiedDefault = final;
                return final;
            }
            finally
            {
                if (Directory.Exists(staging))
                {
                    try { Directory.Delete(staging, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                }
            }
        }
        finally { InstallLock.Release(); }
    }

    private static void VerifyInstalled(string root)
    {
        var marker = Path.Combine(root, ".complete");
        if (!File.Exists(marker) || File.ReadAllText(marker) != PackageSha512)
            throw new InvalidDataException("Office 预览引擎缓存不完整。");
        VerifyFiles(root);
    }

    private static void VerifyFiles(string root)
    {
        var executable = Path.Combine(root, "bin", "libreoffice-kit.exe");
        var manifest = Path.Combine(root, "prebuilds.json");
        if (!File.Exists(executable) || !File.Exists(manifest) || !File.Exists(Path.Combine(root, "program", "program", "soffice.ini")))
            throw new InvalidDataException("Office 预览引擎资源缺失。");
        var data = File.ReadAllBytes(manifest);
        if (!string.Equals(Convert.ToHexString(SHA256.HashData(data)), ManifestSha256, StringComparison.Ordinal))
            throw new InvalidDataException("Office 预览引擎校验失败。");
        using var json = JsonDocument.Parse(data);
        foreach (var file in json.RootElement.GetProperty("files").EnumerateObject())
        {
            var path = Path.GetFullPath(Path.Combine(root, file.Name.Replace('/', Path.DirectorySeparatorChar)));
            var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
                throw new InvalidDataException("Office 预览引擎资源缺失。");
            using var stream = File.OpenRead(path);
            if (!string.Equals(Convert.ToHexString(SHA256.HashData(stream)), file.Value.GetString(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Office 预览引擎校验失败。");
        }
    }

    private static HttpClient CreatePackageClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("RemoteCLI-OfficePreview/1.0");
        return client;
    }

    private static async Task<Stream> OpenPackageAsync(string cacheRoot, Assembly? assembly, CancellationToken cancellationToken)
    {
        var candidates = new List<string>();
        var configured = Environment.GetEnvironmentVariable("REMOTECLI_OFFICEKIT_PACKAGE");
        if (!string.IsNullOrWhiteSpace(configured)) candidates.Add(configured);
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "OfficeKit", PackageFileName));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, PackageFileName));
        candidates.Add(Path.Combine(cacheRoot, PackageFileName));

        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(candidate)) continue;
            ValidatePackageFile(candidate);
            return new FileStream(candidate, FileMode.Open, FileAccess.Read, FileShare.Read);
        }

        assembly ??= Assembly.GetExecutingAssembly();
        var resource = assembly.GetManifestResourceStream(ResourceName);
        if (resource is not null)
        {
            ValidatePackageStream(resource);
            resource.Position = 0;
            return resource;
        }

        Directory.CreateDirectory(cacheRoot);
        var temporary = Path.Combine(cacheRoot, PackageFileName + ".download-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var response = await PackageClient.GetAsync(PackageUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await using var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            }
            ValidatePackageFile(temporary);
            var cached = Path.Combine(cacheRoot, PackageFileName);
            try { File.Move(temporary, cached); }
            catch (IOException) when (File.Exists(cached)) { }
            return new FileStream(cached, FileMode.Open, FileAccess.Read, FileShare.Read);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new OfficeKitUnavailableException("Office 预览组件下载超时，请检查网络后重试，或安装旁边的 Office 可选包。", null);
        }
        catch (HttpRequestException exception)
        {
            throw new OfficeKitUnavailableException("Office 预览组件下载失败，请检查网络后重试，或安装旁边的 Office 可选包。", exception);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                try { File.Delete(temporary); } catch (IOException) { }
            }
        }
    }

    private static void ValidatePackageFile(string path)
    {
        using var stream = File.OpenRead(path);
        ValidatePackageStream(stream);
    }

    private static void ValidatePackageStream(Stream stream)
    {
        if (!stream.CanSeek || Convert.ToBase64String(SHA512.HashData(stream)) != PackageSha512)
            throw new InvalidDataException("Office 预览引擎完整性检查失败。");
        stream.Position = 0;
    }
}

internal sealed class OfficeKitUnavailableException : InvalidOperationException
{
    internal OfficeKitUnavailableException(string message, Exception? innerException) : base(message, innerException) { }
}
