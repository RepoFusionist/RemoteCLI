using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace RemoteCli.Desktop;

/// <summary>
/// 读取文件、目录或程序在 Windows Shell 中的图标，输出 PNG data URI，供右键菜单显示。
/// </summary>
internal static class PathIcons
{
    private const uint ShgfiIcon = 0x00000100;
    private const uint ShgfiLargeIcon = 0x00000000;
    private const uint FileAttributeNormal = 0x00000080;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellFileInfo
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string path, uint fileAttributes, ref ShellFileInfo info, uint infoSize, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);

    /// <summary>返回 PNG data URI；路径无效或系统没有图标时返回 null。</summary>
    public static string? TryGetDataUri(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            var info = default(ShellFileInfo);
            var result = SHGetFileInfo(path, FileAttributeNormal, ref info, (uint)Marshal.SizeOf<ShellFileInfo>(), ShgfiIcon | ShgfiLargeIcon);
            if (result == IntPtr.Zero || info.hIcon == IntPtr.Zero) return null;

            try
            {
                using var icon = Icon.FromHandle(info.hIcon);
                using var bitmap = icon.ToBitmap();
                using var stream = new MemoryStream();
                bitmap.Save(stream, ImageFormat.Png);
                return "data:image/png;base64," + Convert.ToBase64String(stream.ToArray());
            }
            finally
            {
                DestroyIcon(info.hIcon);
            }
        }
        catch (Exception exception) when (exception is ExternalException or ArgumentException or IOException
            or UnauthorizedAccessException or OutOfMemoryException or System.ComponentModel.Win32Exception)
        {
            Trace.TraceWarning("Remote CLI icon lookup failed for '{0}': {1}", path, exception.Message);
            return null;
        }
    }

    /// <summary>把前端传来的程序名解析成可读取图标的可执行文件路径。</summary>
    public static string? ResolveApplication(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        if (Path.IsPathRooted(name)) return File.Exists(name) ? name : null;

        switch (name.ToLowerInvariant())
        {
            case "code":
            case "vscode":
                return FindCodePath();
            case "explorer":
            case "explorer.exe":
                var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
                return File.Exists(explorer) ? explorer : null;
            case "terminal":
            case "wt":
            case "wt.exe":
                var terminal = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "wt.exe");
                return File.Exists(terminal) ? terminal : null;
            case "openas":
                var shell = Path.Combine(Environment.SystemDirectory, "shell32.dll");
                return File.Exists(shell) ? shell : null;
            case "notepad":
                name = "notepad.exe";
                break;
        }

        var candidate = Path.Combine(Environment.SystemDirectory, name);
        if (File.Exists(candidate)) return candidate;
        var inWindows = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), name);
        return File.Exists(inWindows) ? inWindows : null;
    }

    /// <summary>VS Code 可能装在非默认盘符，先看注册表的 App Paths，再看常见安装目录。</summary>
    private static string? FindCodePath()
    {
        var candidates = new List<string>();
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            try
            {
                using var key = hive.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\Code.exe");
                if (key?.GetValue(null) is string registered && !string.IsNullOrWhiteSpace(registered))
                    candidates.Add(registered.Trim().Trim('"'));
            }
            catch (Exception exception) when (exception is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
            }
        }

        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Microsoft VS Code", "Code.exe"));
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft VS Code", "Code.exe"));
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft VS Code", "Code.exe"));

        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate)) return candidate;
        }
        return null;
    }
}
