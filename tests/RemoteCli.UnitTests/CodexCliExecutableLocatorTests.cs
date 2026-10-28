using RemoteCli.Local;

namespace RemoteCli.UnitTests;

public sealed class CodexCliExecutableLocatorTests
{
    [Fact]
    public void Find_PrefersSavedPathOverDesktopConfig()
    {
        var root = Path.Combine(Path.GetTempPath(), "longshell-codex-locator");
        var savedPath = Path.Combine(root, "saved", "codex.exe");
        var desktopPath = Path.Combine(root, "desktop", "codex.exe");
        var available = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.GetFullPath(savedPath),
            Path.GetFullPath(desktopPath)
        };

        var result = CodexCliExecutableLocator.FindFrom(
            savedPath,
            null,
            null,
            null,
            candidate => available.Contains(Path.GetFullPath(candidate)),
            _ => "[mcp_servers.node_repl.env]\nCODEX_CLI_PATH = '" + desktopPath + "'");

        Assert.True(result.Found);
        Assert.Equal(Path.GetFullPath(savedPath), result.ExecutablePath);
        Assert.Equal("Remote CLI 手动设置", result.Source);
    }

    [Fact]
    public void Find_ReadsCliPathFromCodexDesktopConfigBeforeOfficialPath()
    {
        var root = Path.Combine(Path.GetTempPath(), "longshell-codex-config");
        var userProfile = Path.Combine(root, "user");
        var configPath = Path.Combine(userProfile, ".codex", "config.toml");
        var desktopPath = Path.Combine(root, "Codex Desktop", "runtime", "codex.exe");
        var officialPath = Path.Combine(root, "localappdata", "Programs", "OpenAI", "Codex", "bin", "codex.exe");
        var available = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.GetFullPath(desktopPath),
            Path.GetFullPath(officialPath)
        };

        var result = CodexCliExecutableLocator.FindFrom(
            null,
            null,
            userProfile,
            Path.Combine(root, "localappdata"),
            candidate => available.Contains(Path.GetFullPath(candidate)),
            candidate => string.Equals(candidate, configPath, StringComparison.OrdinalIgnoreCase)
                ? "[mcp_servers.node_repl.env]\r\nCODEX_CLI_PATH = '" + desktopPath + "'\r\n"
                : null);

        Assert.True(result.Found);
        Assert.Equal(Path.GetFullPath(desktopPath), result.ExecutablePath);
        Assert.Equal("Codex 桌面版配置", result.Source);
    }

    [Fact]
    public void Find_UsesOfficialWindowsInstallDirectoryWhenDesktopConfigHasNoCliPath()
    {
        var localAppData = Path.Combine(Path.GetTempPath(), "longshell-codex-localappdata");
        var expectedPath = Path.Combine(localAppData, "Programs", "OpenAI", "Codex", "bin", "codex.exe");
        var expectedFullPath = Path.GetFullPath(expectedPath);

        var result = CodexCliExecutableLocator.FindFrom(
            null,
            null,
            Path.Combine(Path.GetTempPath(), "longshell-codex-profile"),
            localAppData,
            candidate => string.Equals(Path.GetFullPath(candidate), expectedFullPath, StringComparison.OrdinalIgnoreCase),
            _ => "[mcp_servers.node_repl.env]\nOTHER_SETTING = 'ignored'");

        Assert.True(result.Found);
        Assert.Equal(expectedFullPath, result.ExecutablePath);
        Assert.Equal("Windows 官方默认安装目录", result.Source);
    }

    [Fact]
    public void Find_RejectsAnotherExistingWindowsExecutable()
    {
        var wrongExecutable = Path.Combine(Path.GetTempPath(), "RemoteCli.Desktop.exe");

        var result = CodexCliExecutableLocator.FindFrom(
            wrongExecutable,
            null,
            null,
            null,
            _ => true);

        Assert.False(result.Found);
    }
}
