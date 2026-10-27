using System.IO.Enumeration;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Common.Protocol;

namespace RemoteCli.Remote;

public static class AgentFileTools
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    public static IReadOnlyList<IRemoteTool> Create(RemoteFileScope scope) =>
    [
        Make("remote.workspace_info", "Returns the active remote workspace and whether paths are restricted to it.", "", "", false,
            (_, token) => Result(new { workspace = scope.Root, workspaceRestricted = scope.Restricted }, token)),
        Make("remote.directory_list", "Lists one remote directory, with a bounded result and offset pagination.",
            "\"path\":{\"type\":\"string\",\"maxLength\":4096},\"offset\":{\"type\":\"integer\"}", "\"path\"", false,
            (args, token) => ListAsync(scope, args, token)),
        Make("remote.file_search", "Finds files using a wildcard pattern against relative paths (including **/); results are bounded.",
            "\"path\":{\"type\":\"string\",\"maxLength\":4096},\"pattern\":{\"type\":\"string\",\"maxLength\":512}", "\"path\",\"pattern\"", false,
            (args, token) => SearchAsync(scope, args, false, token)),
        Make("remote.text_search", "Searches UTF-8 files for text or a timeout-bounded regular expression; returns matching lines.",
            "\"path\":{\"type\":\"string\",\"maxLength\":4096},\"pattern\":{\"type\":\"string\",\"maxLength\":512},\"regex\":{\"type\":\"boolean\"}", "\"path\",\"pattern\"", false,
            (args, token) => SearchAsync(scope, args, true, token)),
        Make("remote.file_info", "Returns remote file or directory metadata without reading its content.", PathProperties, "\"path\"", false,
            (args, token) => InfoAsync(scope, args, token)),
        Make("remote.directory_create", "Creates a directory in the currently permitted remote filesystem scope.", PathProperties, "\"path\"", true,
            (args, token) => { var target = scope.Resolve(PathArg(args)); token.ThrowIfCancellationRequested(); Directory.CreateDirectory(target);
                return Result(new { path = target, created = true }, token); }),
        Make("remote.file_write", "Writes UTF-8 text. Overwriting an existing file requires its current expectedSha256.",
            PathProperties + ",\"content\":{\"type\":\"string\",\"maxLength\":12000},\"expectedSha256\":{\"type\":\"string\",\"maxLength\":64}", "\"path\",\"content\"", true,
            (args, token) => WriteAsync(scope, args, token)),
        Make("remote.file_move", "Moves a remote file or directory without overwriting the destination.", PairProperties, "\"source\",\"destination\"", true,
            (args, token) => MoveOrCopyAsync(scope, args, false, token)),
        Make("remote.file_copy", "Copies a remote file without overwriting the destination.", PairProperties, "\"source\",\"destination\"", true,
            (args, token) => MoveOrCopyAsync(scope, args, true, token)),
        Make("remote.file_delete", "Deletes a remote file, or an empty directory. Recursive directory deletion must be explicit.",
            PathProperties + ",\"recursive\":{\"type\":\"boolean\"}", "\"path\"", true,
            (args, token) => DeleteAsync(scope, args, token))
    ];

    private const string PathProperties = "\"path\":{\"type\":\"string\",\"maxLength\":4096}";
    private const string PairProperties = "\"source\":{\"type\":\"string\",\"maxLength\":4096},\"destination\":{\"type\":\"string\",\"maxLength\":4096}";
    private static string PathArg(JsonElement args) => args.GetProperty("path").GetString()!;
    private static Task<JsonElement> Result(object value, CancellationToken token) { token.ThrowIfCancellationRequested(); return Task.FromResult(JsonSerializer.SerializeToElement(value, ProtocolJson.Options)); }
    private static IRemoteTool Make(string name, string description, string properties, string required, bool write,
        Func<JsonElement, CancellationToken, Task<JsonElement>> run) => new Tool(new RemoteToolDescriptor(name, "2.0.0", description,
            RemoteToolRegistry.ParseSchema("{\"type\":\"object\",\"properties\":{" + properties + "},\"required\":[" + required + "],\"additionalProperties\":false}"), null), write, run);

    private static Task<JsonElement> ListAsync(RemoteFileScope scope, JsonElement args, CancellationToken token)
    {
        var directory = scope.Resolve(PathArg(args), allowRoot: true);
        var offset = args.TryGetProperty("offset", out var value) ? value.GetInt32() : 0;
        if (offset < 0 || offset > 100000) throw new RemoteToolException("invalid_offset", "目录偏移量无效。");
        var entries = new List<JsonElement>(); var used = 0; var scanned = 0; var done = true; var nextOffset = offset;
        foreach (var item in Directory.EnumerateFileSystemEntries(directory))
        {
            token.ThrowIfCancellationRequested();
            if (scanned++ < offset) continue;
            nextOffset = scanned - 1;
            if (entries.Count == 64) { done = false; break; }
            try
            {
                var full = scope.Resolve(item);
                var attributes = File.GetAttributes(full);
                var entry = JsonSerializer.SerializeToElement(new { name = Path.GetFileName(full), path = full,
                    directory = (attributes & FileAttributes.Directory) != 0, reparsePoint = (attributes & FileAttributes.ReparsePoint) != 0 }, ProtocolJson.Options);
                var bytes = JsonSerializer.SerializeToUtf8Bytes(entry, ProtocolJson.Options).Length;
                if (used + bytes > 12000) { done = false; break; }
                entries.Add(entry); used += bytes; nextOffset = scanned;
            }
            catch (RemoteToolException) { nextOffset = scanned; }
        }
        return Result(new { entries, nextOffset, done }, token);
    }

    private static IEnumerable<string> Walk(RemoteFileScope scope, string directory, CancellationToken token, int depth = 0)
    {
        if (depth > 32) yield break;
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            token.ThrowIfCancellationRequested();
            if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0) continue;
            var full = scope.Resolve(entry);
            if (Directory.Exists(full))
            {
                foreach (var nested in Walk(scope, full, token, depth + 1)) yield return nested;
            }
            else yield return full;
        }
    }

    private static async Task<JsonElement> SearchAsync(RemoteFileScope scope, JsonElement args, bool text, CancellationToken token)
    {
        var directory = scope.Resolve(PathArg(args), allowRoot: true);
        var pattern = args.GetProperty("pattern").GetString()!;
        if (pattern.Length == 0) throw new RemoteToolException("empty_pattern", "搜索内容不能为空。");
        Regex? expression = null;
        if (text && args.TryGetProperty("regex", out var flag) && flag.GetBoolean())
        {
            try { expression = new Regex(pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250)); }
            catch (ArgumentException) { throw new RemoteToolException("invalid_regex", "正则表达式无效。"); }
        }
        var matches = new List<JsonElement>(); var scanned = 0; var used = 0; var truncated = false;
        bool Add(object item)
        {
            var element = JsonSerializer.SerializeToElement(item, ProtocolJson.Options);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(element, ProtocolJson.Options).Length;
            if (matches.Count >= 64 || used + bytes > 12000) { truncated = true; return false; }
            matches.Add(element); used += bytes; return true;
        }
        foreach (var file in Walk(scope, directory, token))
        {
            if (++scanned > 2000) { truncated = true; break; }
            var relative = Path.GetRelativePath(directory, file).Replace('\\', '/');
            if (!text)
            {
                var wildcard = pattern.Replace('\\', '/');
                if ((FileSystemName.MatchesSimpleExpression(wildcard, relative, true) ||
                    wildcard.StartsWith("**/", StringComparison.Ordinal) && FileSystemName.MatchesSimpleExpression(wildcard.AsSpan(3), relative, true)) &&
                    !Add(new { path = file })) break;
                continue;
            }
            if (new FileInfo(file).Length > 2 * 1024 * 1024) continue;
            try
            {
                using var reader = new StreamReader(file, Utf8, detectEncodingFromByteOrderMarks: true);
                var lineNumber = 0;
                while (await reader.ReadLineAsync(token).ConfigureAwait(false) is { } line)
                {
                    lineNumber++;
                    if (expression?.IsMatch(line) ?? line.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                        if (!Add(new { path = file, line = lineNumber, text = line.Length > 200 ? line[..200] : line })) break;
                }
            }
            catch (DecoderFallbackException) { }
            catch (RegexMatchTimeoutException) { throw new RemoteToolException("regex_timeout", "正则搜索超时。"); }
            if (truncated) break;
        }
        return JsonSerializer.SerializeToElement(new { matches, scannedFiles = scanned, truncated }, ProtocolJson.Options);
    }

    private static Task<JsonElement> InfoAsync(RemoteFileScope scope, JsonElement args, CancellationToken token)
    {
        var path = scope.Resolve(PathArg(args), true);
        if (Directory.Exists(path)) { var directory = new DirectoryInfo(path); return Result(new { path, directory = true, modifiedUtc = directory.LastWriteTimeUtc }, token); }
        var file = new FileInfo(path);
        if (!file.Exists) throw new RemoteToolException("file_not_found", "文件不存在。");
        return Result(new { path, directory = false, length = file.Length, modifiedUtc = file.LastWriteTimeUtc }, token);
    }

    private static async Task<JsonElement> WriteAsync(RemoteFileScope scope, JsonElement args, CancellationToken token)
    {
        var path = scope.Resolve(PathArg(args));
        var bytes = Utf8.GetBytes(args.GetProperty("content").GetString()!);
        Directory.CreateDirectory(scope.Resolve(Path.GetDirectoryName(path)!, true));
        var exists = File.Exists(path);
        await using var stream = new FileStream(path, exists ? FileMode.Open : FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.Asynchronous);
        if (exists)
        {
            if (!args.TryGetProperty("expectedSha256", out var expected)) throw new RemoteToolException("expected_hash_required", "覆盖文件需要当前 SHA-256。");
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false));
            if (!string.Equals(hash, expected.GetString(), StringComparison.OrdinalIgnoreCase)) throw new RemoteToolException("file_changed", "文件已更改，请重新读取。");
        }
        token.ThrowIfCancellationRequested(); stream.Position = 0;
        await stream.WriteAsync(bytes, token).ConfigureAwait(false); stream.SetLength(bytes.Length);
        return JsonSerializer.SerializeToElement(new { path, written = true, sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() }, ProtocolJson.Options);
    }

    private static async Task<JsonElement> MoveOrCopyAsync(RemoteFileScope scope, JsonElement args, bool copy, CancellationToken token)
    {
        var source = scope.Resolve(args.GetProperty("source").GetString()!);
        var destination = scope.Resolve(args.GetProperty("destination").GetString()!);
        token.ThrowIfCancellationRequested();
        Directory.CreateDirectory(scope.Resolve(Path.GetDirectoryName(destination)!, true));
        if (Directory.Exists(source))
        {
            if (copy) throw new RemoteToolException("file_required", "复制工具只接受单个文件。");
            foreach (var file in Walk(scope, source, token)) scope.Resolve(file);
            Directory.Move(source, destination);
        }
        else if (copy)
        {
            try
            {
                await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, FileOptions.Asynchronous);
                await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, FileOptions.Asynchronous);
                await input.CopyToAsync(output, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                scope.Resolve(destination);
                if (File.Exists(destination)) File.Delete(destination);
                throw;
            }
        }
        else File.Move(source, destination, overwrite: false);
        return await Result(new { source, destination, copied = copy, moved = !copy }, token).ConfigureAwait(false);
    }

    private static Task<JsonElement> DeleteAsync(RemoteFileScope scope, JsonElement args, CancellationToken token)
    {
        var path = scope.Resolve(PathArg(args)); token.ThrowIfCancellationRequested();
        if (Directory.Exists(path))
        {
            var recursive = args.TryGetProperty("recursive", out var value) && value.GetBoolean();
            if (recursive)
            {
                // Preflight every descendant, including links; do not traverse a junction during deletion.
                var pending = new Stack<string>(); pending.Push(path); var count = 0;
                while (pending.TryPop(out var directory))
                    foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
                    {
                        token.ThrowIfCancellationRequested();
                        if (++count > 2000) throw new RemoteToolException("delete_limit", "单次递归删除最多 2000 项。");
                        scope.Resolve(entry);
                        if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0) throw new RemoteToolException("reparse_point_denied", "递归删除不允许链接。");
                        if (Directory.Exists(entry)) pending.Push(entry);
                    }
            }
            token.ThrowIfCancellationRequested(); Directory.Delete(path, recursive);
        }
        else { if (!File.Exists(path)) throw new RemoteToolException("file_not_found", "文件不存在。"); File.Delete(path); }
        return Result(new { path, deleted = true }, token);
    }

    private sealed class Tool(RemoteToolDescriptor descriptor, bool write, Func<JsonElement, CancellationToken, Task<JsonElement>> run) : IRemoteTool
    {
        public RemoteToolDescriptor Descriptor => descriptor;
        public string? ApprovalKey => write ? "workspace.write" : "workspace.read";
        public string PermissionDescription => write ? "修改当前许可范围内的远程文件和目录" : "读取当前许可范围内的远程文件和目录";
        public Task<JsonElement> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken) => run(arguments, cancellationToken);
    }
}
