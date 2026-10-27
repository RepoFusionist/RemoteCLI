namespace RemoteCli.Remote;

public sealed class RemoteFileScope
{
    public string Root { get; }
    public bool Restricted { get; }

    public RemoteFileScope(string root, bool restricted)
    {
        Root = RemoteAccessController.NormalizeRoot(root) ?? throw new ArgumentException("Workspace is required.");
        Restricted = restricted;
    }

    public string Resolve(string path, bool allowRoot = false)
    {
        if (string.IsNullOrWhiteSpace(path) || path.IndexOf('\0') >= 0 || path.Contains(':') && !Path.IsPathFullyQualified(path))
            throw new RemoteToolException("invalid_path", "路径无效。");
        var full = Path.GetFullPath(path, Root);
        var relative = Path.GetRelativePath(Root, full);
        if (Restricted && (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
            throw new RemoteToolException("path_outside_workspace", "工作区限制已开启，无法访问工作区外路径。");
        if (!allowRoot && relative == ".") throw new RemoteToolException("workspace_root_protected", "不能对工作区根目录执行此操作。");
        // Alternate data streams and device namespaces are not file-tool paths.
        var withoutVolume = full[Path.GetPathRoot(full)!.Length..];
        if (withoutVolume.Contains(':') || full.StartsWith("\\\\", StringComparison.Ordinal))
            throw new RemoteToolException("invalid_path", "不支持设备、网络或备用数据流路径。");
        if (Restricted)
        {
            for (var current = full; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            {
                if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new RemoteToolException("reparse_point_denied", "工作区限制不允许符号链接或目录联接。");
                if (string.Equals(current, Root, StringComparison.OrdinalIgnoreCase)) break;
            }
        }
        return full;
    }
}
