using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RemoteCli.Desktop;

namespace RemoteCli.UnitTests;

public sealed class UiPluginPackagesTests
{
    private const string Script = "window.LongShellPlugins={};";

    [Fact]
    public async Task InstallFromFileVerifiesAndActivatesThenUninstallRemovesIt()
    {
        using var scope = new PluginRoot();
        var package = scope.WritePackage("code-highlight.lspkg", Package("code-highlight", ("entry.js", Script)));

        var progress = new RecordingProgress();
        var installed = await UiPluginPackages.InstallFromFileAsync(package, "code-highlight", progress,
            CancellationToken.None, scope.Path);

        Assert.True(Directory.Exists(installed));
        Assert.True(File.Exists(Path.Combine(installed, ".complete")));
        Assert.True(UiPluginPackages.IsInstalled("code-highlight", scope.Path));
        Assert.Equal(Script, UiPluginPackages.LoadEntryScript("code-highlight", scope.Path));
        Assert.Equal(new[] { Script }, UiPluginPackages.LoadInstalledScripts(scope.Path));
        Assert.Contains(progress.Reports, report => report.Phase == OfficeKitInstallPhase.Extract);

        await UiPluginPackages.UninstallAsync("code-highlight", CancellationToken.None, scope.Path);
        Assert.False(UiPluginPackages.IsInstalled("code-highlight", scope.Path));
        Assert.False(Directory.Exists(scope.Path), "the empty plugin root is removed after uninstall");
    }

    [Fact]
    public async Task InstallRejectsAPackageForAnotherPlugin()
    {
        using var scope = new PluginRoot();
        var package = Path.Combine(scope.Path, "other.lspkg");
        File.WriteAllBytes(package, Package("other-plugin", ("entry.js", Script)));

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            UiPluginPackages.InstallFromFileAsync(package, "code-highlight", null, CancellationToken.None, scope.Path));
        Assert.Contains("标识", error.Message);
        Assert.False(UiPluginPackages.IsInstalled("other-plugin", scope.Path));
    }

    [Fact]
    public async Task InstallRejectsHashMismatchAndUndeclaredFiles()
    {
        using var scope = new PluginRoot();
        var badHash = Path.Combine(scope.Path, "bad-hash.lspkg");
        File.WriteAllBytes(badHash, Package("code-highlight", new[] { ("entry.js", Script) }, new string('0', 64)));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            UiPluginPackages.InstallFromFileAsync(badHash, "code-highlight", null, CancellationToken.None, scope.Path));

        var extra = Path.Combine(scope.Path, "extra.lspkg");
        File.WriteAllBytes(extra, Package("code-highlight", new[] { ("entry.js", Script) },
            extras: new[] { ("extra.js", "alert(1)") }));
        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            UiPluginPackages.InstallFromFileAsync(extra, "code-highlight", null, CancellationToken.None, scope.Path));
        Assert.Contains("未声明", error.Message);
    }

    [Fact]
    public async Task InstallRejectsTraversalAndNonJavaScriptEntries()
    {
        using var scope = new PluginRoot();
        var traversal = Path.Combine(scope.Path, "traversal.lspkg");
        File.WriteAllBytes(traversal, Package("code-highlight", new[] { ("entry.js", Script) },
            extras: new[] { ("../escape.js", "alert(1)") }));
        var traversalError = await Assert.ThrowsAsync<InvalidDataException>(() =>
            UiPluginPackages.InstallFromFileAsync(traversal, "code-highlight", null, CancellationToken.None, scope.Path));
        Assert.Contains("路径", traversalError.Message);

        var textEntry = Path.Combine(scope.Path, "text-entry.lspkg");
        File.WriteAllBytes(textEntry, Package("code-highlight", new[] { ("entry.txt", Script) }, entry: "entry.txt"));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            UiPluginPackages.InstallFromFileAsync(textEntry, "code-highlight", null, CancellationToken.None, scope.Path));
    }

    [Fact]
    public async Task CleanupResidualsRemovesStagingAndPartialDownloads()
    {
        using var scope = new PluginRoot();
        Directory.CreateDirectory(Path.Combine(scope.Path, "staging-abc"));
        File.WriteAllText(Path.Combine(scope.Path, "code-highlight.lspkg.download-abc"), "partial");

        UiPluginPackages.CleanupResiduals(scope.Path);

        Assert.False(Directory.Exists(scope.Path), "cleaning an empty plugin root removes it");
    }

    private static byte[] Package(string id, params (string Name, string Content)[] files)
        => Package(id, files, null);

    private static byte[] Package(string id, (string Name, string Content)[] files, string? declaredHash = null,
        string entry = "entry.js", (string Name, string Content)[]? extras = null)
    {
        var manifest = JsonSerializer.Serialize(new
        {
            id,
            name = "Code Highlight",
            version = "1.0.0",
            kind = "ui-script",
            entry,
            files = files.ToDictionary(file => file.Name, file => declaredHash ?? Hash(file.Content))
        });
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, true))
        {
            WriteEntry(archive, "plugin.json", manifest);
            foreach (var file in files) WriteEntry(archive, file.Name, file.Content);
            foreach (var file in extras ?? Array.Empty<(string Name, string Content)>())
                WriteEntry(archive, file.Name, file.Content);
        }
        return buffer.ToArray();
    }

    private static void WriteEntry(ZipArchive archive, string name, string content)
    {
        using var stream = archive.CreateEntry(name).Open();
        var bytes = Encoding.UTF8.GetBytes(content);
        stream.Write(bytes);
    }

    private static string Hash(string content)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    private sealed class PluginRoot : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "remotecli-plugins-" + Guid.NewGuid().ToString("N"));
        private string Scratch { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "remotecli-packages-" + Guid.NewGuid().ToString("N"));

        internal PluginRoot()
        {
            Directory.CreateDirectory(Path);
            Directory.CreateDirectory(Scratch);
        }

        internal string WritePackage(string name, byte[] bytes)
        {
            var path = System.IO.Path.Combine(Scratch, name);
            File.WriteAllBytes(path, bytes);
            return path;
        }

        public void Dispose()
        {
            foreach (var directory in new[] { Path, Scratch })
            {
                try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    private sealed class RecordingProgress : IProgress<OfficeKitInstallProgress>
    {
        internal List<OfficeKitInstallProgress> Reports { get; } = new();

        public void Report(OfficeKitInstallProgress value) => Reports.Add(value);
    }
}
