using System.Security.Cryptography;
using System.Buffers;

namespace RemoteCli.Remote;

/// <summary>Copies user files into OS-created private sandbox storage. Never changes source ACLs or labels.</summary>
public sealed class SandboxWorkspaceSnapshot
{
    private const long MaximumBytes = 512L * 1024 * 1024;
    private const int MaximumFiles = 20000;
    private readonly RemoteFileScope _source;
    private readonly RemoteFileScope _copy;
    private readonly Dictionary<string, string> _original = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _originalDirectories = new(StringComparer.OrdinalIgnoreCase);

    private SandboxWorkspaceSnapshot(RemoteFileScope source, RemoteFileScope copy) { _source = source; _copy = copy; }
    public string Directory => _copy.Root;

    public static async Task<SandboxWorkspaceSnapshot> CreateAsync(string workspace, string isolatedDirectory, CancellationToken token)
    {
        System.IO.Directory.CreateDirectory(isolatedDirectory);
        var snapshot = new SandboxWorkspaceSnapshot(new RemoteFileScope(workspace, true), new RemoteFileScope(isolatedDirectory, true));
        long bytes = 0;
        foreach (var (relative, directory) in snapshot.Enumerate(snapshot._source, token))
        {
            var target = snapshot._copy.Resolve(relative);
            if (directory) { System.IO.Directory.CreateDirectory(target); snapshot._originalDirectories.Add(relative); continue; }
            var source = snapshot._source.Resolve(relative);
            if (snapshot._original.Count >= MaximumFiles)
                throw new RemoteToolException("sandbox_workspace_too_large", "隔离工作区最多 512 MiB、20000 个文件。");
            var copied = await CopyAndHashAsync(source, target, MaximumBytes - bytes, token).ConfigureAwait(false);
            bytes += copied.Length;
            snapshot._original.Add(relative, copied.Hash);
        }
        return snapshot;
    }

    public string MapWorkingDirectory(string original) => _copy.Resolve(Path.GetRelativePath(_source.Root, original), allowRoot: true);

    public async Task CommitAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long bytes = 0;
        foreach (var (relative, directory) in Enumerate(_copy, token))
        {
            if (directory) { directories.Add(relative); continue; }
            var path = _copy.Resolve(relative);
            bytes += new FileInfo(path).Length;
            if (bytes > MaximumBytes || current.Count >= MaximumFiles)
                throw new RemoteToolException("sandbox_output_too_large", "隔离结果超出 512 MiB 或 20000 个文件。");
            current.Add(relative, await HashAsync(path, token).ConfigureAwait(false));
        }
        var changed = current.Where(pair => !_original.TryGetValue(pair.Key, out var hash) || hash != pair.Value).Select(pair => pair.Key)
            .Concat(_original.Keys.Where(path => !current.ContainsKey(path))).ToArray();
        foreach (var relative in changed)
        {
            var path = _source.Resolve(relative);
            var expected = _original.GetValueOrDefault(relative);
            var actual = File.Exists(path) ? await HashAsync(path, token).ConfigureAwait(false) : null;
            if (actual != expected || System.IO.Directory.Exists(path))
                throw new RemoteToolException("workspace_changed", "工作区文件在命令运行期间被更改，已拒绝覆盖：" + relative);
        }

        var journal = new List<(string Path, string? Backup)>();
        var createdDirectories = new List<string>();
        var removedDirectories = new List<string>();
        var backupRoot = System.IO.Directory.CreateTempSubdirectory("longshell-commit-").FullName;
        try
        {
            // All paths were checked before applying any change. Keep a rollback journal for revocation or I/O failure.
            foreach (var relative in directories.OrderBy(path => path.Length))
            {
                token.ThrowIfCancellationRequested();
                var path = _source.Resolve(relative);
                if (!System.IO.Directory.Exists(path)) { System.IO.Directory.CreateDirectory(path); createdDirectories.Add(path); }
            }
            foreach (var relative in changed)
            {
                token.ThrowIfCancellationRequested();
                var destination = _source.Resolve(relative);
                string? backup = null;
                if (File.Exists(destination))
                {
                    backup = Path.Combine(backupRoot, journal.Count.ToString());
                    await CopyAsync(destination, backup, token).ConfigureAwait(false);
                }
                journal.Add((destination, backup));
                if (!current.ContainsKey(relative)) File.Delete(destination);
                else
                {
                    var parent = _source.Resolve(Path.GetDirectoryName(destination)!, true);
                    System.IO.Directory.CreateDirectory(parent);
                    var temporary = _source.Resolve(Path.Combine(parent, ".longshell-commit-" + Guid.NewGuid().ToString("N")));
                    try
                    {
                        await CopyAsync(_copy.Resolve(relative), temporary, token).ConfigureAwait(false);
                        token.ThrowIfCancellationRequested();
                        _source.Resolve(destination);
                        File.Move(temporary, destination, overwrite: true);
                    }
                    finally { if (File.Exists(temporary)) File.Delete(temporary); }
                }
            }
            foreach (var relative in _originalDirectories.Except(directories).OrderByDescending(path => path.Length))
            {
                token.ThrowIfCancellationRequested();
                var directory = _source.Resolve(relative);
                if (System.IO.Directory.Exists(directory) && !System.IO.Directory.EnumerateFileSystemEntries(directory).Any())
                { System.IO.Directory.Delete(directory); removedDirectories.Add(directory); }
            }
        }
        catch
        {
            foreach (var directory in removedDirectories.OrderBy(path => path.Length))
                System.IO.Directory.CreateDirectory(_source.Resolve(directory));
            foreach (var item in journal.AsEnumerable().Reverse())
            {
                _source.Resolve(item.Path);
                if (item.Backup is null) File.Delete(item.Path);
                else { System.IO.Directory.CreateDirectory(Path.GetDirectoryName(item.Path)!); File.Copy(item.Backup, item.Path, overwrite: true); }
            }
            foreach (var directory in createdDirectories.AsEnumerable().Reverse())
                if (System.IO.Directory.Exists(directory) && !System.IO.Directory.EnumerateFileSystemEntries(directory).Any()) System.IO.Directory.Delete(directory);
            throw;
        }
        finally { System.IO.Directory.Delete(backupRoot, recursive: true); }
    }

    private IEnumerable<(string Relative, bool Directory)> Enumerate(RemoteFileScope scope, CancellationToken token)
    {
        var pending = new Stack<string>(); pending.Push(scope.Root); var count = 0;
        while (pending.TryPop(out var directory))
        {
            foreach (var entry in System.IO.Directory.EnumerateFileSystemEntries(directory))
            {
                token.ThrowIfCancellationRequested();
                if (++count > MaximumFiles * 2) throw new RemoteToolException("sandbox_workspace_too_large", "隔离目录项过多。");
                scope.Resolve(entry); // Reject every junction and symlink, including directory-only links.
                var isDirectory = System.IO.Directory.Exists(entry);
                yield return (Path.GetRelativePath(scope.Root, entry), isDirectory);
                if (isDirectory) pending.Push(entry);
            }
        }
    }

    private static async Task<string> HashAsync(string path, CancellationToken token)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false));
    }

    private static async Task CopyAsync(string source, string destination, CancellationToken token)
    {
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await using var input = File.OpenRead(source);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, FileOptions.Asynchronous);
        await input.CopyToAsync(output, token).ConfigureAwait(false);
    }

    private static async Task<(long Length, string Hash)> CopyAndHashAsync(
        string source, string destination, long remainingBytes, CancellationToken token)
    {
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
            81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var sourceLength = input.Length;
        if (sourceLength > remainingBytes)
            throw new RemoteToolException("sandbox_workspace_too_large", "隔离工作区最多 512 MiB、20000 个文件。");

        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(81920);
        long copied = 0;
        try
        {
            while (true)
            {
                var count = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), token).ConfigureAwait(false);
                if (count == 0) break;
                copied = checked(copied + count);
                if (copied > remainingBytes)
                    throw new RemoteToolException("sandbox_workspace_too_large", "隔离工作区最多 512 MiB、20000 个文件。");
                hash.AppendData(buffer, 0, count);
                await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
            }
            if (copied != sourceLength)
                throw new IOException("A workspace file changed while its sandbox snapshot was being copied.");
            await output.FlushAsync(token).ConfigureAwait(false);
            return (copied, Convert.ToHexString(hash.GetHashAndReset()));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
