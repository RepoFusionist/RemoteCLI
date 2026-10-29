using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace RemoteCli.Desktop;

internal sealed record OpenWithApp(string Name, string Path, bool IsDefault);

/// <summary>
/// 列出“用其他应用打开”的候选程序，并记住用户选过的程序。
/// 候选来源：用户选择历史 → 系统默认关联 → 注册表 OpenWithList。
/// </summary>
internal static class OpenWithCatalog
{
    private const int MaxApps = 12;
    private const int MaxRememberedPerExtension = 6;

    private static readonly string StorePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LongShell", "open-with.json");

    public static IReadOnlyList<OpenWithApp> List(string filePath)
    {
        var extension = Path.GetExtension(filePath).ToLowerInvariant();
        var apps = new List<OpenWithApp>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? candidate, bool isDefault)
        {
            if (apps.Count >= MaxApps || string.IsNullOrWhiteSpace(candidate)) return;
            var expanded = Environment.ExpandEnvironmentVariables(candidate.Trim().Trim('"'));
            if (!Path.IsPathRooted(expanded) || !File.Exists(expanded)) return;
            if (!seen.Add(expanded)) return;
            apps.Add(new OpenWithApp(Describe(expanded), expanded, isDefault));
        }

        var remembered = LoadMap();
        foreach (var saved in remembered.TryGetValue(extension, out var savedList) ? savedList : new List<string>()) Add(saved, false);
        Add(QueryDefaultApplication(extension), true);
        foreach (var registered in QueryRegisteredApplications(extension)) Add(registered, false);

        return apps;
    }

    /// <summary>记住一次“用这个程序打开这类文件”的选择，最近选择的排在最前。</summary>
    public static void Remember(string filePath, string appPath)
    {
        var extension = Path.GetExtension(filePath).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(extension) || string.IsNullOrWhiteSpace(appPath)) return;

        var map = LoadMap();
        var list = map.TryGetValue(extension, out var existing) ? new List<string>(existing) : [];
        list.RemoveAll(item => string.Equals(item, appPath, StringComparison.OrdinalIgnoreCase));
        list.Insert(0, appPath);
        map[extension] = list.Take(MaxRememberedPerExtension).ToList();
        SaveMap(map);
    }

    private static string Describe(string path)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            if (!string.IsNullOrWhiteSpace(info.FileDescription)) return info.FileDescription.Trim();
        }
        catch (Exception exception) when (exception is FileNotFoundException or UnauthorizedAccessException or IOException)
        {
        }
        return Path.GetFileNameWithoutExtension(path);
    }

    private static string? QueryDefaultApplication(string extension)
    {
        if (string.IsNullOrEmpty(extension)) return null;
        uint length = 1024;
        var buffer = new StringBuilder((int)length);
        var result = AssocQueryStringW(AssocFlags.None, AssocString.Executable, extension, null, buffer, ref length);
        return result == 0 ? buffer.ToString() : null;
    }

    private static List<string> QueryRegisteredApplications(string extension)
    {
        var candidates = new List<string>();

        try
        {
            using var userList = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\" + extension + @"\OpenWithList");
            CollectOpenWithList(userList, candidates);
        }
        catch (Exception exception) when (exception is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
        }

        try
        {
            using var classList = Registry.ClassesRoot.OpenSubKey(extension + @"\OpenWithList");
            CollectOpenWithList(classList, candidates);
        }
        catch (Exception exception) when (exception is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
        }

        var resolved = new List<string>();
        foreach (var name in candidates)
        {
            var path = ResolveExecutable(name);
            if (path is not null && !resolved.Contains(path, StringComparer.OrdinalIgnoreCase)) resolved.Add(path);
        }
        return resolved;
    }

    private static void CollectOpenWithList(RegistryKey? key, List<string> candidates)
    {
        if (key is null) return;
        foreach (var valueName in key.GetValueNames())
        {
            if (string.Equals(valueName, "MRUList", StringComparison.OrdinalIgnoreCase)) continue;
            if (key.GetValue(valueName) is string value && !string.IsNullOrWhiteSpace(value)) candidates.Add(value);
        }
    }

    private static string? ResolveExecutable(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        if (name.Contains('\\') || name.Contains('/'))
        {
            var direct = Environment.ExpandEnvironmentVariables(name.Trim().Trim('"'));
            return File.Exists(direct) ? direct : null;
        }

        var locations = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), name),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), name),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", name)
        };
        foreach (var candidate in locations)
        {
            if (File.Exists(candidate)) return candidate;
        }

        try
        {
            using var appPath = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + name);
            if (appPath?.GetValue(null) is string registered && File.Exists(registered)) return registered;
        }
        catch (Exception exception) when (exception is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
        }

        return null;
    }

    private static Dictionary<string, List<string>> LoadMap()
    {
        try
        {
            if (!File.Exists(StorePath)) return new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var map = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(File.ReadAllBytes(StorePath));
            if (map is null) return new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            return new Dictionary<string, List<string>>(map, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            Trace.TraceWarning("Remote CLI open-with store read failed: {0}", exception.Message);
            return new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static void SaveMap(Dictionary<string, List<string>> map)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
            File.WriteAllText(StorePath, JsonSerializer.Serialize(map), new UTF8Encoding(false));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning("Remote CLI open-with store write failed: {0}", exception.Message);
        }
    }

    private enum AssocFlags : uint
    {
        None = 0
    }

    private enum AssocString : uint
    {
        Executable = 2
    }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int AssocQueryStringW(AssocFlags flags, AssocString str, string assoc, string? extra, StringBuilder? output, ref uint outputLength);
}
