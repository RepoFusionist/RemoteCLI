using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RemoteCli.Desktop;

// Checkpoints contain workspace files, not Git metadata. Never follow links or restore an
// unvalidated archive path. A separate checkpoint is made before any restore.
internal sealed class WorkspaceCheckpoints(string storageRoot)
{
    private const long MaximumBytes = 512L * 1024 * 1024;
    private const int MaximumEntries = 20000;
    private static readonly StringComparer Paths = StringComparer.OrdinalIgnoreCase;
    private sealed record FileRecord(string Path, long Size, string Hash);
    private sealed record Manifest(string Root, List<string> Directories, List<FileRecord> Files);
    internal sealed record Preview(string Fingerprint, int Added, int Changed, int Removed, int DirectoryChanges, IReadOnlyList<string> Changes);

    internal string Capture(string workspaceRoot)
    {
        var root = ValidateRoot(workspaceRoot);
        var storage = Path.GetFullPath(storageRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (IsWithin(root, storage)) throw new IOException("快照目录位于工作区内，请选择更具体的工作区。 ");
        var inventory = Inventory(root, calculateHashes: false);
        Directory.CreateDirectory(storage);
        var id = Guid.NewGuid().ToString("N");
        var temporary = Path.Combine(storage, id + ".tmp");
        var destination = Path.Combine(storage, id + ".zip");
        try
        {
            var files = new List<FileRecord>();
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                foreach (var file in inventory.Files)
                {
                    var fullPath = Resolve(root, file.Path);
                    EnsureNoLinks(root, fullPath);
                    var before = new FileInfo(fullPath);
                    var entry = archive.CreateEntry("files/" + file.Path, CompressionLevel.Fastest);
                    using var input = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                    using var output = entry.Open();
                    using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    var buffer = new byte[81920];
                    long written = 0;
                    int count;
                    while ((count = input.Read(buffer)) != 0)
                    {
                        written += count;
                        if (written > file.Size) throw new IOException("创建快照时工作区文件发生变化，请重试。");
                        hasher.AppendData(buffer, 0, count);
                        output.Write(buffer, 0, count);
                    }
                    before.Refresh();
                    if (written != file.Size || before.Length != file.Size || before.LastWriteTimeUtc != file.Modified)
                        throw new IOException("创建快照时工作区文件发生变化，请重试。");
                    files.Add(new FileRecord(file.Path, written, Convert.ToHexString(hasher.GetHashAndReset())));
                }
                var manifest = archive.CreateEntry("manifest.json", CompressionLevel.Fastest);
                using var writer = manifest.Open();
                JsonSerializer.Serialize(writer, new Manifest(root, inventory.Directories, files));
            }
            var after = Inventory(root, calculateHashes: true);
            if (!inventory.Directories.SequenceEqual(after.Directories, Paths) || files.Count != after.Files.Count ||
                files.Where((file, index) => !Paths.Equals(file.Path, after.Files[index].Path) || file.Size != after.Files[index].Size ||
                    !string.Equals(file.Hash, after.Files[index].Hash, StringComparison.Ordinal)).Any())
                throw new IOException("创建快照时工作区发生变化，请重试。");
            File.Move(temporary, destination);
            return id;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal Preview Inspect(string workspaceRoot, string checkpointId)
    {
        var root = ValidateRoot(workspaceRoot);
        using var archive = Open(checkpointId);
        var manifest = ReadManifest(archive, root, verifyContent: true);
        return Compare(manifest, Inventory(root, calculateHashes: true));
    }

    internal string Restore(string workspaceRoot, string checkpointId, string expectedFingerprint, Action<string>? backupCreated = null)
    {
        var root = ValidateRoot(workspaceRoot);
        using var archive = Open(checkpointId);
        var manifest = ReadManifest(archive, root, verifyContent: true);
        var current = Inventory(root, calculateHashes: true);
        if (!string.Equals(Compare(manifest, current).Fingerprint, expectedFingerprint, StringComparison.Ordinal))
            throw new IOException("工作区在预览后发生变化，请重新预览。");
        var backupId = Capture(root);
        backupCreated?.Invoke(backupId);
        current = Inventory(root, calculateHashes: true);
        if (!string.Equals(Compare(manifest, current).Fingerprint, expectedFingerprint, StringComparison.Ordinal))
            throw new IOException("工作区在备份期间发生变化，请重新预览。回退前快照已保留。");

        var desiredFiles = manifest.Files.ToDictionary(item => item.Path, Paths);
        var currentFiles = current.Files.ToDictionary(item => item.Path, Paths);
        foreach (var directory in manifest.Directories.OrderBy(item => item.Length))
        {
            var path = Resolve(root, directory);
            EnsureNoLinks(root, path);
            Directory.CreateDirectory(path);
        }
        foreach (var file in manifest.Files)
        {
            if (currentFiles.TryGetValue(file.Path, out var existing) && string.Equals(existing.Hash, file.Hash, StringComparison.Ordinal)) continue;
            var target = Resolve(root, file.Path);
            EnsureNoLinks(root, target);
            VerifyUnchanged(target, existing);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var temporary = target + ".longshell-" + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var source = archive.GetEntry("files/" + file.Path)!.Open())
                using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) source.CopyTo(output);
                EnsureNoLinks(root, target);
                File.Move(temporary, target, overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        foreach (var file in current.Files.Where(item => !desiredFiles.ContainsKey(item.Path)))
        {
            var target = Resolve(root, file.Path);
            EnsureNoLinks(root, target);
            VerifyUnchanged(target, file);
            File.Delete(target);
        }
        var desiredDirectories = manifest.Directories.ToHashSet(Paths);
        foreach (var directory in current.Directories.Where(item => !desiredDirectories.Contains(item)).OrderByDescending(item => item.Length))
        {
            var target = Resolve(root, directory);
            EnsureNoLinks(root, target);
            if (Directory.Exists(target) && !Directory.EnumerateFileSystemEntries(target).Any()) Directory.Delete(target);
        }
        return backupId;
    }

    private sealed record DiskFile(string Path, long Size, DateTime Modified, string? Hash);
    private sealed record InventoryResult(List<string> Directories, List<DiskFile> Files);
    private static InventoryResult Inventory(string root, bool calculateHashes)
    {
        var directories = new List<string>();
        var files = new List<DiskFile>();
        var queue = new Stack<string>();
        queue.Push(root);
        long bytes = 0;
        while (queue.Count != 0)
        {
            var parent = queue.Pop();
            EnsureNoLinks(root, parent);
            foreach (var path in Directory.EnumerateFileSystemEntries(parent))
            {
                if (string.Equals(Path.GetFileName(path), ".git", StringComparison.OrdinalIgnoreCase)) continue;
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("工作区包含链接或联接点，无法安全创建完整快照。");
                var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
                Resolve(root, relative);
                if ((attributes & FileAttributes.Directory) != 0) { directories.Add(relative); queue.Push(path); }
                else
                {
                    var info = new FileInfo(path);
                    bytes = checked(bytes + info.Length);
                    if (bytes > MaximumBytes) throw new IOException("工作区超过快照上限（512 MiB）。");
                    string? hash = null;
                    if (calculateHashes)
                    {
                        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                        hash = Convert.ToHexString(SHA256.HashData(input));
                        info.Refresh();
                        if (info.Length != input.Length) throw new IOException("读取工作区时文件发生变化，请重试。");
                    }
                    files.Add(new DiskFile(relative, info.Length, info.LastWriteTimeUtc, hash));
                }
                if (files.Count + directories.Count > MaximumEntries) throw new IOException("工作区超过快照上限（20,000 个项目）。");
            }
        }
        directories.Sort(Paths);
        files.Sort((a, b) => Paths.Compare(a.Path, b.Path));
        return new InventoryResult(directories, files);
    }

    private static Preview Compare(Manifest target, InventoryResult current)
    {
        var actual = current.Files.ToDictionary(item => item.Path, Paths);
        var desired = target.Files.ToDictionary(item => item.Path, Paths);
        var changes = new List<string>();
        var added = 0; var changed = 0; var removed = 0;
        foreach (var file in current.Files)
            if (!desired.ContainsKey(file.Path)) { added++; changes.Add("将删除：" + file.Path); }
        foreach (var file in target.Files)
        {
            if (!actual.TryGetValue(file.Path, out var existing)) { removed++; changes.Add("将恢复：" + file.Path); }
            else if (!string.Equals(file.Hash, existing.Hash, StringComparison.Ordinal)) { changed++; changes.Add("将还原：" + file.Path); }
        }
        var desiredDirectories = target.Directories.ToHashSet(Paths);
        var actualDirectories = current.Directories.ToHashSet(Paths);
        var extraDirectories = current.Directories.Where(dir => !desiredDirectories.Contains(dir)).ToArray();
        var missingDirectories = target.Directories.Where(dir => !actualDirectories.Contains(dir)).ToArray();
        foreach (var dir in extraDirectories) changes.Add("将移除新增空目录：" + dir);
        foreach (var dir in missingDirectories) changes.Add("将恢复目录：" + dir);
        var description = new StringBuilder();
        foreach (var dir in current.Directories) description.Append("D\0").Append(dir).Append('\0');
        foreach (var file in current.Files) description.Append("F\0").Append(file.Path).Append('\0').Append(file.Hash).Append('\0');
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(description.ToString())));
        return new Preview(fingerprint, added, changed, removed, extraDirectories.Length + missingDirectories.Length, changes.Take(30).ToArray());
    }

    private static void VerifyUnchanged(string path, DiskFile? expected)
    {
        if (expected is null)
        {
            if (File.Exists(path) || Directory.Exists(path)) throw new IOException("回退期间出现了新文件，请重新预览。回退前快照已保留。");
            return;
        }
        if (!File.Exists(path)) throw new IOException("回退期间文件已被移动或删除，请重新预览。回退前快照已保留。");
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (!string.Equals(Convert.ToHexString(SHA256.HashData(input)), expected.Hash, StringComparison.Ordinal))
            throw new IOException("回退期间文件已改变，请重新预览。回退前快照已保留。");
    }

    private ZipArchive Open(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new IOException("无效的工作区快照编号。");
        return ZipFile.OpenRead(Path.Combine(storageRoot, id + ".zip"));
    }
    private static Manifest ReadManifest(ZipArchive archive, string root, bool verifyContent)
    {
        var manifestEntry = archive.GetEntry("manifest.json") ?? throw new IOException("工作区快照缺少清单。");
        if (manifestEntry.Length > 8 * 1024 * 1024) throw new IOException("工作区快照清单过大。");
        using var input = manifestEntry.Open();
        var manifest = JsonSerializer.Deserialize<Manifest>(input) ?? throw new IOException("工作区快照清单无效。");
        if (!Paths.Equals(root, manifest.Root) || manifest.Files is null || manifest.Directories is null ||
            manifest.Files.Count + manifest.Directories.Count > MaximumEntries) throw new IOException("快照与当前工作区不匹配。");
        var seen = new HashSet<string>(Paths);
        long bytes = 0;
        foreach (var directory in manifest.Directories) { Resolve(root, directory); if (!seen.Add(directory)) throw new IOException("快照路径重复。"); }
        foreach (var file in manifest.Files)
        {
            Resolve(root, file.Path);
            if (!seen.Add(file.Path) || file.Size < 0 || file.Size > MaximumBytes - bytes) throw new IOException("快照文件清单无效。");
            bytes += file.Size;
            var entry = archive.GetEntry("files/" + file.Path) ?? throw new IOException("快照文件缺失。");
            if (entry.Length != file.Size) throw new IOException("快照文件大小不匹配。");
            if (verifyContent)
            {
                using var data = entry.Open();
                if (!string.Equals(Convert.ToHexString(SHA256.HashData(data)), file.Hash, StringComparison.Ordinal)) throw new IOException("快照文件校验失败。");
            }
        }
        return manifest;
    }
    private static string ValidateRoot(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new IOException("工作区路径为空。");
        var root = Path.GetFullPath(value).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!Directory.Exists(root) || Paths.Equals(root, Path.GetPathRoot(root)?.TrimEnd(Path.DirectorySeparatorChar)))
            throw new IOException("工作区目录不存在或为磁盘根目录。");
        EnsureRootChain(root);
        return root;
    }
    private static bool IsWithin(string root, string path) => Paths.Equals(root, path) ||
        path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static string Resolve(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains('\\') || relative.Contains(':') || relative.Contains('\0') ||
            relative.Split('/').Any(part => part is "" or "." or ".." || string.Equals(part, ".git", StringComparison.OrdinalIgnoreCase)))
            throw new IOException("快照包含无效路径。");
        var full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsWithin(root, full) || Paths.Equals(root, full)) throw new IOException("快照路径超出工作区。");
        return full;
    }
    private static void EnsureNoLinks(string root, string target)
    {
        if (!IsWithin(root, target)) throw new IOException("路径超出工作区。");
        EnsureRootChain(root);
        var current = root;
        foreach (var part in Path.GetRelativePath(root, target).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (part == ".") continue;
            current = Path.Combine(current, part);
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("工作区包含链接或联接点，无法安全回退。");
        }
    }
    private static void EnsureRootChain(string root)
    {
        for (var directory = new DirectoryInfo(root); directory is not null; directory = directory.Parent)
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("工作区路径包含链接或联接点，无法安全回退。");
    }
}
