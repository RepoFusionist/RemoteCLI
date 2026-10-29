using RemoteCli.Desktop;

namespace RemoteCli.UnitTests;

public sealed class OfficeKitPreviewTests
{
    [Fact]
    public async Task BundledEngineConvertsWordAndExcelWithoutModifyingInput()
    {
        if (!OperatingSystem.IsWindows() || System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture != System.Runtime.InteropServices.Architecture.X64) return;
        // The LibreOffice kit is an optional upstream runtime download whose license does not
        // allow this repository to redistribute it. Keep the archive out of git and out of the
        // test assembly; run the real conversion check only when a local copy is supplied.
        var package = LocalOfficeKitPackage();
        if (package is null) return;
        var root = Path.Combine(Path.GetTempPath(), "LongShell-office-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.False(OfficeKitAssets.IsInstalled(root));
            var progress = new ProgressCollector();
            var engine = await OfficeKitAssets.EnsureInstalledAsync(CancellationToken.None, root, typeof(OfficeKitPreviewTests).Assembly, progress, package);
            Assert.True(File.Exists(Path.Combine(engine, "licenses", "LibreOffice-MPL-2.0.txt")));
            Assert.True(OfficeKitAssets.IsInstalled(root));
            Assert.Equal(engine, OfficeKitAssets.GetInstalledRoot(root));
            Assert.Contains(OfficeKitInstallPhase.Extract, progress.Phases);
            Assert.Equal(engine, await OfficeKitAssets.EnsureInstalledAsync(CancellationToken.None, root, typeof(OfficeKitPreviewTests).Assembly));
            var fixtures = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Fixtures"));
            foreach (var name in new[] { "preview-modern.docx", "preview-modern.xlsx", "preview-legacy.doc", "preview-legacy.xls" })
            {
                var bytes = await File.ReadAllBytesAsync(Path.Combine(fixtures, name));
                var original = System.Security.Cryptography.SHA256.HashData(bytes);
                var pdf = await OfficeKitPreview.ConvertAsync(bytes, Path.GetExtension(name), engine, CancellationToken.None);
                Assert.True(DocumentPreview.IsPdf(pdf), name);
                Assert.True(pdf.Length > 1000, name);
                Assert.Equal(original, System.Security.Cryptography.SHA256.HashData(bytes));
                var output = Environment.GetEnvironmentVariable("LONGSHELL_TEST_PREVIEW_OUTPUT");
                if (!string.IsNullOrWhiteSpace(output)) await File.WriteAllBytesAsync(Path.Combine(output, name + ".pdf"), pdf);
            }
            await OfficeKitAssets.UninstallAsync(CancellationToken.None, root);
            Assert.False(OfficeKitAssets.IsInstalled(root));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CancellingInstallationRemovesPartialFiles()
    {
        if (!OperatingSystem.IsWindows() || System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture != System.Runtime.InteropServices.Architecture.X64) return;
        var root = Path.Combine(Path.GetTempPath(), "LongShell-office-cancel-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var cancellation = new CancellationTokenSource();
            var progress = new CancelOnPrepareProgress(cancellation);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                OfficeKitAssets.EnsureInstalledAsync(cancellation.Token, root, typeof(OfficeKitPreviewTests).Assembly, progress));
            Assert.False(OfficeKitAssets.IsInstalled(root));
            Assert.Empty(Directory.GetDirectories(root, "staging-*"));
            Assert.Empty(Directory.GetFiles(root, OfficeKitAssets.PackageFileName + ".download-*"));

            Directory.CreateDirectory(Path.Combine(root, "staging-leftover"));
            await File.WriteAllTextAsync(Path.Combine(root, OfficeKitAssets.PackageFileName + ".download-leftover"), "partial");
            OfficeKitAssets.CleanupResiduals(root);
            Assert.False(Directory.Exists(root));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static string? LocalOfficeKitPackage()
    {
        var configured = Environment.GetEnvironmentVariable("REMOTECLI_OFFICEKIT_PACKAGE");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return Path.GetFullPath(configured);
        var repository = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var candidate = Path.Combine(repository, "src", "RemoteCli.Desktop", "Assets", OfficeKitAssets.PackageFileName);
        return File.Exists(candidate) ? candidate : null;
    }

    private sealed class ProgressCollector : IProgress<OfficeKitInstallProgress>
    {
        internal List<OfficeKitInstallPhase> Phases { get; } = new();

        public void Report(OfficeKitInstallProgress value)
        {
            lock (Phases) Phases.Add(value.Phase);
        }
    }

    private sealed class CancelOnPrepareProgress(CancellationTokenSource cancellation) : IProgress<OfficeKitInstallProgress>
    {
        public void Report(OfficeKitInstallProgress value)
        {
            if (value.Phase == OfficeKitInstallPhase.Prepare) cancellation.Cancel();
        }
    }

    [Fact]
    public void OfficeInputValidationRejectsIncorrectLegacyAndOversizedContainers()
    {
        Assert.Throws<InvalidDataException>(() => DocumentPreview.ValidateOfficeContainer("not-a-doc"u8.ToArray(), ".doc"));
        Assert.Throws<InvalidDataException>(() => DocumentPreview.ValidateOfficeContainer("not-an-xls"u8.ToArray(), ".xls"));
        Assert.Throws<InvalidDataException>(() => DocumentPreview.ValidateOfficeContainer(new byte[DocumentPreview.MaxFileBytes + 1], ".docx"));
    }
}
