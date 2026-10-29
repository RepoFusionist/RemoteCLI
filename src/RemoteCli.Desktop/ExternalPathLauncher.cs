using System.Diagnostics;

namespace RemoteCli.Desktop;

/// <summary>
/// 构造“用系统程序打开工作区路径”的启动参数。
/// 关键安全约定：默认程序模式绝不直接执行 .exe/.ps1/.js 一类的文件，改为弹出系统的“打开方式”对话框。
/// </summary>
internal static class ExternalPathLauncher
{
    private static readonly HashSet<string> ExecutableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".com", ".bat", ".cmd", ".msi", ".ps1", ".scr", ".lnk", ".vbs", ".vbe", ".js", ".jse",
        ".wsf", ".wsh", ".reg", ".jar", ".hta", ".cpl", ".msc"
    };

    public static ProcessStartInfo Build(string fullPath, bool isDirectory, string mode)
    {
        var container = isDirectory ? fullPath : Path.GetDirectoryName(fullPath) ?? fullPath;
        switch (mode)
        {
            case "reveal":
                // 只定位不执行：目录会打开父级并高亮该目录。
                return new ProcessStartInfo { FileName = "explorer.exe", Arguments = "/select,\"" + fullPath + "\"", UseShellExecute = true };
            case "explorer":
                return new ProcessStartInfo { FileName = "explorer.exe", Arguments = "\"" + container + "\"", UseShellExecute = true };
            case "terminal":
                return new ProcessStartInfo { FileName = "wt.exe", Arguments = "-d \"" + container + "\"", UseShellExecute = true };
            case "code":
                return new ProcessStartInfo { FileName = FindVisualStudioCode() ?? "code", Arguments = "\"" + fullPath + "\"", UseShellExecute = true };
            case "notepad":
                return new ProcessStartInfo { FileName = "notepad.exe", Arguments = "\"" + fullPath + "\"", UseShellExecute = true };
            case "openas":
                return BuildOpenAs(fullPath);
            default:
                return ExecutableExtensions.Contains(Path.GetExtension(fullPath))
                    ? BuildOpenAs(fullPath)
                    : new ProcessStartInfo { FileName = fullPath, UseShellExecute = true };
        }
    }

    private static ProcessStartInfo BuildOpenAs(string fullPath) => new()
    {
        FileName = "rundll32.exe",
        Arguments = "shell32.dll,OpenAs_RunDLL \"" + fullPath + "\"",
        UseShellExecute = true
    };

    internal static string? FindVisualStudioCode()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Microsoft VS Code", "Code.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft VS Code", "Code.exe")
        };
        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}
