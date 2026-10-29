using RemoteCli.Desktop;

namespace RemoteCli.UnitTests;

public sealed class ExternalPathLauncherTests
{
    [Theory]
    [InlineData("setup.exe")]
    [InlineData("build.bat")]
    [InlineData("deploy.ps1")]
    [InlineData("script.js")]
    [InlineData("installer.msi")]
    [InlineData("shortcut.lnk")]
    public void DefaultModeNeverExecutesRiskyFilesDirectly(string fileName)
    {
        var path = Path.Combine(@"C:\Work\Alpha", fileName);

        var startInfo = ExternalPathLauncher.Build(path, isDirectory: false, mode: "default");

        Assert.Equal("rundll32.exe", startInfo.FileName);
        Assert.Contains("OpenAs_RunDLL", startInfo.Arguments);
        Assert.Contains(path, startInfo.Arguments);
        Assert.True(startInfo.UseShellExecute);
    }

    [Theory]
    [InlineData("notes.txt")]
    [InlineData("report.docx")]
    [InlineData("sheet.xlsx")]
    [InlineData("photo.png")]
    public void DefaultModeHandsDocumentsToTheirRegisteredApp(string fileName)
    {
        var path = Path.Combine(@"C:\Work\Alpha", fileName);

        var startInfo = ExternalPathLauncher.Build(path, isDirectory: false, mode: "default");

        Assert.Equal(path, startInfo.FileName);
        Assert.True(startInfo.UseShellExecute);
    }

    [Fact]
    public void OpenAsAlwaysUsesTheSystemChooser()
    {
        var path = @"C:\Work\Alpha\notes.txt";

        var startInfo = ExternalPathLauncher.Build(path, isDirectory: false, mode: "openas");

        Assert.Equal("rundll32.exe", startInfo.FileName);
        Assert.Equal("shell32.dll,OpenAs_RunDLL \"C:\\Work\\Alpha\\notes.txt\"", startInfo.Arguments);
    }

    [Fact]
    public void RevealSelectsThePathInsteadOfOpeningIt()
    {
        var path = @"C:\Work\Alpha\notes.txt";

        var startInfo = ExternalPathLauncher.Build(path, isDirectory: false, mode: "reveal");

        Assert.Equal("explorer.exe", startInfo.FileName);
        Assert.Equal("/select,\"C:\\Work\\Alpha\\notes.txt\"", startInfo.Arguments);
    }

    [Fact]
    public void ExplorerModeOnAFileOpensItsFolder()
    {
        var startInfo = ExternalPathLauncher.Build(@"C:\Work\Alpha\notes.txt", isDirectory: false, mode: "explorer");

        Assert.Equal("explorer.exe", startInfo.FileName);
        Assert.Equal("\"C:\\Work\\Alpha\"", startInfo.Arguments);
    }

    [Fact]
    public void ExplorerAndTerminalModesTargetTheDirectoryItself()
    {
        var explorer = ExternalPathLauncher.Build(@"C:\Work\Alpha\src", isDirectory: true, mode: "explorer");
        var terminal = ExternalPathLauncher.Build(@"C:\Work\Alpha\src", isDirectory: true, mode: "terminal");

        Assert.Equal("\"C:\\Work\\Alpha\\src\"", explorer.Arguments);
        Assert.Equal("wt.exe", terminal.FileName);
        Assert.Equal("-d \"C:\\Work\\Alpha\\src\"", terminal.Arguments);
    }

    [Fact]
    public void TerminalModeOnAFileStartsInItsParentDirectory()
    {
        var startInfo = ExternalPathLauncher.Build(@"C:\Work\Alpha\notes.txt", isDirectory: false, mode: "terminal");

        Assert.Equal("wt.exe", startInfo.FileName);
        Assert.Equal("-d \"C:\\Work\\Alpha\"", startInfo.Arguments);
    }

    [Fact]
    public void NotepadQuotesPathsWithSpaces()
    {
        var startInfo = ExternalPathLauncher.Build(@"C:\Work folder\a b.txt", isDirectory: false, mode: "notepad");

        Assert.Equal("notepad.exe", startInfo.FileName);
        Assert.Equal("\"C:\\Work folder\\a b.txt\"", startInfo.Arguments);
    }
}
