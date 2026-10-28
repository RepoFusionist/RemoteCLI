using RemoteCli.Desktop;

namespace RemoteCli.UnitTests;

public sealed class OfficeKitPreviewTests
{
    [Fact]
    public async Task BundledEngineConvertsWordAndExcelWithoutModifyingInput()
    {
        if (!OperatingSystem.IsWindows() || System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture != System.Runtime.InteropServices.Architecture.X64) return;
        var root = Path.Combine(Path.GetTempPath(), "LongShell-office-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var engine = await OfficeKitAssets.EnsureInstalledAsync(CancellationToken.None, root, typeof(OfficeKitPreviewTests).Assembly);
            Assert.True(File.Exists(Path.Combine(engine, "licenses", "LibreOffice-MPL-2.0.txt")));
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
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
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
