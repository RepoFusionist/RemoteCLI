using System.Text;
using System.Text.Json;
using RemoteCli.Desktop;

namespace RemoteCli.UnitTests;

public sealed class GatewayPatchCompatibilityTests
{
    private static JsonElement Arguments(object value) => JsonSerializer.SerializeToElement(value);

    [Fact]
    public async Task FuzzyUnicodeAnchorAndContextKeepOriginalBytes()
    {
        var root = NewRoot();
        try
        {
            var path = Path.Combine(root, "file.txt");
            await File.WriteAllTextAsync(path, "  section — “quoted”  \r\n  item ‘left’\u00a0value  \nremove\r\n");
            var patch = "*** Begin Patch\n*** Update File: file.txt\n@@ section - \"quoted\"\n item 'left' value\n-remove\n+added\n*** End Patch";
            var tool = new GatewayPatch(root, (_, _) => Task.FromResult(true)).Create();
            Assert.StartsWith("已应用", await tool.Execute(Arguments(new { patch }), CancellationToken.None));
            Assert.Equal("  section — “quoted”  \r\n  item ‘left’\u00a0value  \nadded\r\n", await File.ReadAllTextAsync(path));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task KeepsUnchangedMixedLineEndingsAndUsesFirstEndingForInsertions()
    {
        var root = NewRoot();
        try
        {
            var path = Path.Combine(root, "file.txt");
            await File.WriteAllTextAsync(path, "one\r\nold\nlast\r");
            var patch = "*** Begin Patch\n*** Update File: file.txt\n@@\n one\n-old\n+new\n last\n*** End Patch";
            Assert.StartsWith("已应用", await new GatewayPatch(root, (_, _) => Task.FromResult(true)).Create().Execute(Arguments(new { patch }), CancellationToken.None));
            Assert.Equal("one\r\nnew\r\nlast\r", await File.ReadAllTextAsync(path));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task RetriesTrailingEmptyContextAndTerminatesUnterminatedFile()
    {
        var root = NewRoot();
        try
        {
            var path = Path.Combine(root, "file.txt");
            await File.WriteAllTextAsync(path, "before\nlast");
            var patch = "*** Begin Patch\n*** Update File: file.txt\n@@\n-last\n+changed\n \n*** End Patch";
            Assert.StartsWith("已应用", await new GatewayPatch(root, (_, _) => Task.FromResult(true)).Create().Execute(Arguments(new { patch }), CancellationToken.None));
            Assert.Equal("before\nchanged\n", await File.ReadAllTextAsync(path));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task AdditionOnlyChunkAppendsAtEofEvenWhenAnchored()
    {
        var root = NewRoot();
        try
        {
            var path = Path.Combine(root, "file.txt");
            await File.WriteAllTextAsync(path, "first\nsecond\n");
            var patch = "*** Begin Patch\n*** Update File: file.txt\n@@ first\n+third\n*** End Patch";
            Assert.StartsWith("已应用", await new GatewayPatch(root, (_, _) => Task.FromResult(true)).Create().Execute(Arguments(new { patch }), CancellationToken.None));
            Assert.Equal("first\nsecond\nthird\n", await File.ReadAllTextAsync(path));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ExactMatchWinsOverEarlierWhitespaceFuzzyMatch()
    {
        var root = NewRoot();
        try
        {
            var path = Path.Combine(root, "file.txt");
            await File.WriteAllTextAsync(path, "target \ntarget\n");
            var patch = "*** Begin Patch\n*** Update File: file.txt\n@@\n-target\n+changed\n*** End Patch";
            Assert.StartsWith("已应用", await new GatewayPatch(root, (_, _) => Task.FromResult(true)).Create().Execute(Arguments(new { patch }), CancellationToken.None));
            Assert.Equal("target \nchanged\n", await File.ReadAllTextAsync(path));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task EndOfFileMarkerSelectsLastOccurrence()
    {
        var root = NewRoot();
        try
        {
            var path = Path.Combine(root, "file.txt");
            await File.WriteAllTextAsync(path, "repeat\nother\nrepeat\n");
            var patch = "*** Begin Patch\n*** Update File: file.txt\n@@\n-repeat\n+last\n*** End of File\n*** End Patch";
            Assert.StartsWith("已应用", await new GatewayPatch(root, (_, _) => Task.FromResult(true)).Create().Execute(Arguments(new { patch }), CancellationToken.None));
            Assert.Equal("repeat\nother\nlast\n", await File.ReadAllTextAsync(path));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task MultipleOperationsOnSameFileUseVirtualStateAndMoveWithoutLosingSource()
    {
        var root = NewRoot();
        try
        {
            var oldPath = Path.Combine(root, "old.txt");
            await File.WriteAllTextAsync(oldPath, "first\n");
            var patch = "*** Begin Patch\n*** Update File: old.txt\n@@\n-first\n+second\n*** Update File: old.txt\n*** Move to: new.txt\n@@\n-second\n+third\n*** Update File: new.txt\n@@\n-third\n+fourth\n*** End Patch";
            Assert.StartsWith("已应用", await new GatewayPatch(root, (_, _) => Task.FromResult(true)).Create().Execute(Arguments(new { patch }), CancellationToken.None));
            Assert.False(File.Exists(oldPath));
            Assert.Equal("fourth\n", await File.ReadAllTextAsync(Path.Combine(root, "new.txt")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task DestinationChangeDuringApprovalRejectsMoveWithoutDeletingSource()
    {
        var root = NewRoot();
        try
        {
            var oldPath = Path.Combine(root, "old.txt"); var newPath = Path.Combine(root, "new.txt");
            await File.WriteAllTextAsync(oldPath, "original\n");
            var patch = "*** Begin Patch\n*** Update File: old.txt\n*** Move to: new.txt\n@@\n-original\n+modified\n*** End Patch";
            var tool = new GatewayPatch(root, async (_, _) => { await File.WriteAllTextAsync(newPath, "other\n"); return true; }).Create();
            Assert.StartsWith("错误：", await tool.Execute(Arguments(new { patch }), CancellationToken.None));
            Assert.Equal("original\n", await File.ReadAllTextAsync(oldPath));
            Assert.Equal("other\n", await File.ReadAllTextAsync(newPath));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ExplicitLocalEnvironmentAndWorkdirAreValidatedAndShownForApproval()
    {
        var root = NewRoot();
        try
        {
            var nested = Path.Combine(root, "nested"); Directory.CreateDirectory(nested);
            await File.WriteAllTextAsync(Path.Combine(nested, "file.txt"), "old\n", new UTF8Encoding(false));
            var patch = "*** Begin Patch\n*** Environment ID: local\n*** Update File: file.txt\n@@\n-old\n+new\n*** End Patch";
            string? approved = null;
            var tool = new GatewayPatch(root, (details, _) => { approved = details; return Task.FromResult(true); }).Create();
            Assert.StartsWith("已应用", await tool.Execute(Arguments(new { patch, workdir = "nested", environment_id = "local" }), CancellationToken.None));
            Assert.Contains(nested, approved);
            Assert.Equal("new\n", await File.ReadAllTextAsync(Path.Combine(nested, "file.txt")));
            approved = null;
            Assert.StartsWith("错误：", await tool.Execute(Arguments(new { patch, workdir = "..", environment_id = "local" }), CancellationToken.None));
            Assert.Null(approved);
            Assert.StartsWith("错误：", await tool.Execute(Arguments(new { patch, workdir = "nested", environment_id = "remote" }), CancellationToken.None));
            Assert.Null(approved);
            Assert.StartsWith("错误：", await tool.Execute(Arguments(new { patch = patch.Replace("ID: local", "ID: remote"), workdir = "nested" }), CancellationToken.None));
            Assert.Null(approved);
        }
        finally { Directory.Delete(root, true); }
    }

    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "longshell-patch-compat-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}
