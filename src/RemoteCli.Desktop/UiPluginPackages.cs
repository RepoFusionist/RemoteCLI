using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RemoteCli.Desktop;

// UI plugins are optional .lspkg packages (a zip with plugin.json and a JS entry).
// Packages live outside the executable so the main download stays small.
internal static class UiPluginPackages
{
    internal const string CodeHighlightId = "code-highlight";
    internal const string PackageExtension = ".lspkg";
    private const string ManifestName = "plugin.json";
    private const string MarkerName = ".complete";
    private const long MaxPackageBytes = 16L * 1024 * 1024;
    private const long MaxExpandedBytes = 32L * 1024 * 1024;
    private const int MaxFiles = 64;
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly HttpClient Client = CreateClient();

    internal static string DefaultRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RemoteCLI", "Plugins");

    internal static bool IsInstalled(string id, string? root = null)
    {
        if (!IsValidId(id)) return false;
        var directory = Path.Combine(root ?? DefaultRoot, id);
        if (!Directory.Exists(directory) || !File.Exists(Path.Combine(directory, MarkerName))) return false;
        try
        {
            var manifest = ReadManifest(Path.Combine(directory, ManifestName));
            return manifest.Id == id && File.Exists(Path.Combine(directory, manifest.Entry));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return false;
        }
    }

    internal static string LoadEntryScript(string id, string? root = null)
    {
        if (!IsValidId(id)) throw new InvalidDataException("插件标识无效。");
        var directory = Path.Combine(root ?? DefaultRoot, id);
        var manifest = ReadManifest(Path.Combine(directory, ManifestName));
        if (manifest.Id != id) throw new InvalidDataException("插件标识与安装目录不一致。");
        var entry = Path.Combine(directory, manifest.Entry);
        VerifyFile(entry, manifest.Files[manifest.Entry]);
        return File.ReadAllText(entry, Encoding.UTF8);
    }

    internal static IEnumerable<string> LoadInstalledScripts(string? root = null)
    {
        var rootPath = root ?? DefaultRoot;
        if (!Directory.Exists(rootPath)) yield break;
        foreach (var directory in Directory.EnumerateDirectories(rootPath))
        {
            var id = Path.GetFileName(directory);
            if (!IsValidId(id) || !File.Exists(Path.Combine(directory, MarkerName))) continue;
            string? script = null;
            try { script = LoadEntryScript(id, rootPath); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                Trace.TraceWarning("Remote CLI UI plugin {0} could not be loaded: {1}", id, exception.Message);
            }
            if (script is not null) yield return script;
        }
    }

    internal static async Task<string> InstallFromUrlAsync(string id, IProgress<OfficeKitInstallProgress>? progress,
        CancellationToken cancellationToken, string? rootOverride = null)
    {
        if (!IsValidId(id)) throw new InvalidDataException("插件标识无效。");
        var url = ResolveDownloadUrl(id);
        var root = rootOverride ?? DefaultRoot;
        Directory.CreateDirectory(root);
        var temporary = Path.Combine(root, id + PackageExtension + ".download-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var response = await Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength;
                if (total is > MaxPackageBytes) throw new InvalidDataException("插件包超过 16 MiB。");
                await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await using var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                var buffer = new byte[81920];
                long received = 0;
                var lastPercent = -1;
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
                {
                    received += read;
                    if (received > MaxPackageBytes) throw new InvalidDataException("插件包超过 16 MiB。");
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    if (total is > 0)
                    {
                        var percent = (int)(received * 100 / total.Value);
                        if (percent != lastPercent)
                        {
                            lastPercent = percent;
                            progress?.Report(new(OfficeKitInstallPhase.Download, percent));
                        }
                    }
                }
                progress?.Report(new(OfficeKitInstallPhase.Download, 100));
            }
            return await InstallFromFileAsync(temporary, id, progress, cancellationToken, root).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException("插件下载超时，请检查网络后重试。");
        }
        catch (HttpRequestException exception)
        {
            throw new InvalidOperationException("插件下载失败，请检查本机测试下载服务是否已启动。", exception);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    internal static async Task<string> InstallFromFileAsync(string packagePath, string expectedId,
        IProgress<OfficeKitInstallProgress>? progress, CancellationToken cancellationToken, string? rootOverride = null)
    {
        if (!IsValidId(expectedId)) throw new InvalidDataException("插件标识无效。");
        var info = new FileInfo(packagePath);
        if (!info.Exists) throw new InvalidDataException("插件包不存在。");
        if (info.Length > MaxPackageBytes) throw new InvalidDataException("插件包超过 16 MiB。");

        var root = rootOverride ?? DefaultRoot;
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var staging = Path.Combine(root, "staging-" + Guid.NewGuid().ToString("N"));
        try
        {
            progress?.Report(new(OfficeKitInstallPhase.Extract, null));
            Directory.CreateDirectory(staging);
            string packageHash;
            UiPluginManifest manifest;
            using (var stream = new FileStream(packagePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                packageHash = Convert.ToHexString(SHA256.HashData(stream));
                stream.Position = 0;
                using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
                manifest = ExtractPackage(archive, expectedId, staging, cancellationToken);
            }

            VerifyExtractedFiles(staging, manifest);
            await File.WriteAllTextAsync(Path.Combine(staging, MarkerName), packageHash, cancellationToken).ConfigureAwait(false);
            var final = Path.Combine(root, manifest.Id);
            if (Directory.Exists(final)) Directory.Delete(final, recursive: true);
            Directory.Move(staging, final);
            return final;
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                try { Directory.Delete(staging, recursive: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            Gate.Release();
        }
    }

    internal static async Task UninstallAsync(string id, CancellationToken cancellationToken, string? root = null)
    {
        if (!IsValidId(id)) throw new InvalidDataException("插件标识无效。");
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var rootPath = root ?? DefaultRoot;
            var directory = Path.Combine(rootPath, id);
            if (!Directory.Exists(directory)) return;
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException("插件文件正在使用，无法卸载。请稍后重试。", exception);
            }
            TryDeleteEmptyRoot(rootPath);
        }
        finally { Gate.Release(); }
    }

    internal static void CleanupResiduals(string? root = null)
    {
        var rootPath = root ?? DefaultRoot;
        try
        {
            if (!Directory.Exists(rootPath)) return;
            foreach (var staging in Directory.GetDirectories(rootPath, "staging-*"))
            {
                try { Directory.Delete(staging, recursive: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            foreach (var download in Directory.GetFiles(rootPath, "*" + PackageExtension + ".download-*"))
            {
                try { File.Delete(download); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            TryDeleteEmptyRoot(rootPath);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    internal static string ResolveDownloadUrl(string id)
    {
        if (!IsValidId(id)) throw new InvalidDataException("插件标识无效。");
        var specific = Environment.GetEnvironmentVariable("REMOTECLI_PLUGIN_" + id.Replace('-', '_').ToUpperInvariant() + "_URL");
        var baseUrl = Environment.GetEnvironmentVariable("REMOTECLI_PLUGIN_BASE_URL");
        var candidate = !string.IsNullOrWhiteSpace(specific)
            ? specific.Trim()
            : ((string.IsNullOrWhiteSpace(baseUrl) ? "http://127.0.0.1:5765/" : baseUrl.Trim()).TrimEnd('/') + "/" + id + PackageExtension);
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)) ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new InvalidOperationException("插件下载地址无效，需要 HTTPS（本机测试可用 HTTP 回环地址）。");
        }
        return uri.AbsoluteUri;
    }

    private static UiPluginManifest ExtractPackage(ZipArchive archive, string expectedId, string staging,
        CancellationToken cancellationToken)
    {
        if (archive.Entries.Count > MaxFiles + 1) throw new InvalidDataException("插件包文件数量超出限制。");
        long expanded = 0;
        var manifestEntry = archive.GetEntry(ManifestName);
        if (manifestEntry is null || manifestEntry.Length <= 0 || manifestEntry.Length > 64 * 1024)
            throw new InvalidDataException("插件包缺少 plugin.json。");
        byte[] manifestBytes;
        using (var source = manifestEntry.Open())
        using (var buffer = new MemoryStream())
        {
            source.CopyTo(buffer);
            manifestBytes = buffer.ToArray();
        }
        var manifest = ParseManifest(manifestBytes);
        if (manifest.Id != expectedId) throw new InvalidDataException("插件包标识与当前插件不匹配。");

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')) continue;
            var name = entry.FullName;
            if (!IsValidFileName(name) || name == MarkerName)
                throw new InvalidDataException("插件包包含无效路径。");
            if (name != ManifestName && !manifest.Files.ContainsKey(name))
                throw new InvalidDataException("插件包包含未声明的文件。");
            if (entry.Length < 0 || entry.Length > MaxExpandedBytes - expanded)
                throw new InvalidDataException("插件包解压后过大。");
            expanded += entry.Length;
            if (!seen.Add(name)) throw new InvalidDataException("插件包包含重复文件。");
            using var source = entry.Open();
            using var destination = new FileStream(Path.Combine(staging, name), FileMode.CreateNew, FileAccess.Write, FileShare.None);
            source.CopyTo(destination);
        }

        if (!seen.Contains(ManifestName)) throw new InvalidDataException("插件包缺少 plugin.json。");
        foreach (var file in manifest.Files.Keys)
        {
            if (!seen.Contains(file)) throw new InvalidDataException("插件包缺少声明的文件：" + file);
        }
        return manifest;
    }

    private static void VerifyExtractedFiles(string staging, UiPluginManifest manifest)
    {
        foreach (var file in manifest.Files)
        {
            VerifyFile(Path.Combine(staging, file.Key), file.Value);
        }
    }

    private static void VerifyFile(string path, string expectedHash)
    {
        if (!File.Exists(path)) throw new InvalidDataException("插件文件缺失：" + Path.GetFileName(path));
        using var stream = File.OpenRead(path);
        var actual = Convert.ToHexString(SHA256.HashData(stream));
        if (!string.Equals(actual, expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("插件文件校验失败：" + Path.GetFileName(path));
    }

    private static UiPluginManifest ReadManifest(string path)
    {
        if (!File.Exists(path)) throw new InvalidDataException("插件缺少 plugin.json。");
        var info = new FileInfo(path);
        if (info.Length <= 0 || info.Length > 64 * 1024) throw new InvalidDataException("插件清单大小无效。");
        return ParseManifest(File.ReadAllBytes(path));
    }

    private static UiPluginManifest ParseManifest(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("插件清单格式无效。");
        var id = ReadString(root, "id");
        var name = ReadString(root, "name");
        var version = ReadString(root, "version");
        var kind = ReadString(root, "kind");
        var entry = ReadString(root, "entry");
        if (!IsValidId(id)) throw new InvalidDataException("插件标识无效。");
        if (string.IsNullOrWhiteSpace(name) || name.Length > 80) throw new InvalidDataException("插件名称无效。");
        if (!IsValidVersion(version)) throw new InvalidDataException("插件版本无效。");
        if (kind != "ui-script") throw new InvalidDataException("插件类型不受支持。");
        if (!IsValidFileName(entry) || !entry.EndsWith(".js", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("插件入口无效。");
        if (!root.TryGetProperty("files", out var filesElement) || filesElement.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("插件清单缺少文件列表。");
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in filesElement.EnumerateObject())
        {
            if (!IsValidFileName(file.Name) || files.Count >= MaxFiles || file.Value.ValueKind != JsonValueKind.String)
                throw new InvalidDataException("插件文件列表无效。");
            var hash = file.Value.GetString() ?? "";
            if (hash.Length != 64 || !hash.All(IsHex)) throw new InvalidDataException("插件文件哈希无效。");
            files[file.Name] = hash;
        }
        if (!files.ContainsKey(entry)) throw new InvalidDataException("插件入口不在文件列表中。");
        return new UiPluginManifest(id, name, version, entry, files);
    }

    private static bool IsValidId(string id)
        => id.Length is > 0 and <= 40 && id[0] is >= 'a' and <= 'z' or >= '0' and <= '9' &&
           id.All(ch => ch is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');

    private static bool IsValidVersion(string version)
        => version.Length is > 0 and <= 32 && version[0] is >= '0' and <= '9' &&
           version.All(ch => ch is >= '0' and <= '9' or >= 'a' and <= 'z' or >= 'A' and <= 'Z' or '.' or '+' or '-');

    private static bool IsValidFileName(string name)
        => name.Length is > 0 and <= 120 && name is not "." and not ".." &&
           name.IndexOfAny(new[] { '/', '\\', ':', '\0' }) < 0 &&
           name.All(ch => ch is >= ' ' and <= '~' or > (char)127);

    private static bool IsHex(char ch) => ch is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';

    private static string ReadString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static void TryDeleteEmptyRoot(string root)
    {
        try
        {
            if (Directory.Exists(root) && !Directory.EnumerateFileSystemEntries(root).Any())
                Directory.Delete(root, recursive: false);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("RemoteCLI-Plugin/1.0");
        return client;
    }

    private sealed record UiPluginManifest(string Id, string Name, string Version, string Entry,
        IReadOnlyDictionary<string, string> Files);
}
