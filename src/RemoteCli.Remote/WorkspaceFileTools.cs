using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Common.Protocol;

namespace RemoteCli.Remote;

public static class WorkspaceFileTools
{
    private const int MaximumTextCharacters = 12_000;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static IReadOnlyList<IRemoteTool> Create(string workspaceRoot, bool restricted = true)
    {
        var root = Path.GetFullPath(workspaceRoot);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException("The selected remote workspace does not exist.");
        }

        root = Path.TrimEndingDirectorySeparator(root);
        var scope = new RemoteFileScope(root, restricted);
        return
        [
            new DelegateTool(
                new RemoteToolDescriptor(
                    "remote.file_read",
                    "1.0.0",
                    "Reads one UTF-8 text file from the workspace selected on the remote computer and returns its SHA-256 for safe edits.",
                    Schema("""
                    {"type":"object","properties":{"path":{"type":"string","maxLength":4096},"offset":{"type":"integer"},"maxChars":{"type":"integer"}},"required":["path"],"additionalProperties":false}
                    """),
                    null),
                "workspace.read",
                "读取所选工作区中的文本文件",
                (arguments, token) => ReadAsync(scope, arguments, token)),
            new DelegateTool(
                new RemoteToolDescriptor(
                    "remote.file_create",
                    "1.0.0",
                    "Creates one UTF-8 text file below the workspace selected on the remote computer. Existing files are never overwritten.",
                    Schema("""
                    {"type":"object","properties":{"path":{"type":"string","maxLength":4096},"content":{"type":"string","maxLength":12000}},"required":["path","content"],"additionalProperties":false}
                    """),
                    null),
                "workspace.write",
                "在所选工作区中创建文本文件（不会覆盖已有文件）",
                (arguments, token) => CreateAsync(scope, arguments, token)),
            new DelegateTool(
                new RemoteToolDescriptor(
                    "remote.file_edit",
                    "1.0.0",
                    "Replaces one exact text occurrence in a UTF-8 workspace file. The expected SHA-256 prevents overwriting changes made after the file was read.",
                    Schema("""
                    {"type":"object","properties":{"path":{"type":"string","maxLength":4096},"oldText":{"type":"string","maxLength":6000},"newText":{"type":"string","maxLength":6000},"expectedSha256":{"type":"string","maxLength":64}},"required":["path","oldText","newText","expectedSha256"],"additionalProperties":false}
                    """),
                    null),
                "workspace.write",
                "编辑所选工作区中的文本文件",
                (arguments, token) => EditAsync(scope, arguments, token))
        ];
    }

    private static async Task<JsonElement> ReadAsync(RemoteFileScope scope, JsonElement arguments, CancellationToken token)
    {
        var root = scope.Root;
        var fullPath = scope.Resolve(arguments.GetProperty("path").GetString()!);
        await using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        if (stream.Length > 4 * 1024 * 1024)
        {
            throw new RemoteToolException("file_too_large", "The file is too large for the bounded text tool.");
        }

        var bytes = new byte[stream.Length];
        await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        string content;
        try
        {
            content = StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            throw new RemoteToolException("file_not_utf8", "The selected file is not valid UTF-8 text.");
        }

        var offset = arguments.TryGetProperty("offset", out var offsetValue) ? offsetValue.GetInt32() : 0;
        var maximum = arguments.TryGetProperty("maxChars", out var maximumValue) ? maximumValue.GetInt32() : 2048;
        if (offset < 0 || offset > content.Length || maximum is < 1 or > 2048)
            throw new RemoteToolException("invalid_range", "offset 必须在文件范围内，maxChars 为 1–2048。");
        var count = Math.Min(maximum, content.Length - offset);

        return JsonSerializer.SerializeToElement(new
        {
            path = Path.GetRelativePath(root, fullPath),
            content = content.Substring(offset, count),
            sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            length = content.Length,
            nextOffset = offset + count,
            done = offset + count >= content.Length
        }, ProtocolJson.Options);
    }

    private static async Task<JsonElement> CreateAsync(RemoteFileScope scope, JsonElement arguments, CancellationToken token)
    {
        var root = scope.Root;
        var content = arguments.GetProperty("content").GetString() ?? string.Empty;
        var fullPath = scope.Resolve(arguments.GetProperty("path").GetString()!);
        var parent = Path.GetDirectoryName(fullPath) ?? root;
        scope.Resolve(parent, allowRoot: true);
        token.ThrowIfCancellationRequested();
        Directory.CreateDirectory(parent);
        try
        {
            await using var stream = new FileStream(fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous);
            var bytes = StrictUtf8.GetBytes(content);
            await stream.WriteAsync(bytes, token).ConfigureAwait(false);
            return JsonSerializer.SerializeToElement(new
            {
                path = Path.GetRelativePath(root, fullPath),
                created = true,
                sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()
            }, ProtocolJson.Options);
        }
        catch (IOException) when (File.Exists(fullPath))
        {
            throw new RemoteToolException("file_already_exists", "The file already exists and was not overwritten.");
        }
    }

    private static async Task<JsonElement> EditAsync(RemoteFileScope scope, JsonElement arguments, CancellationToken token)
    {
        var root = scope.Root;
        var fullPath = scope.Resolve(arguments.GetProperty("path").GetString()!);
        await using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.Asynchronous);
        if (stream.Length > MaximumTextCharacters * 4)
        {
            throw new RemoteToolException("file_too_large", "The file is too large for the bounded text tool.");
        }

        var bytes = new byte[stream.Length];
        await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        var currentHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var expectedHash = arguments.GetProperty("expectedSha256").GetString() ?? string.Empty;
        if (expectedHash.Length != 64)
        {
            throw new RemoteToolException("invalid_hash", "expectedSha256 must contain a 64-character SHA-256 value.");
        }

        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(currentHash),
                Encoding.ASCII.GetBytes(expectedHash.ToLowerInvariant())))
        {
            throw new RemoteToolException("file_changed", "The file changed after it was read. Read it again before editing.");
        }

        string content;
        try
        {
            content = StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            throw new RemoteToolException("file_not_utf8", "The selected file is not valid UTF-8 text.");
        }

        var oldText = arguments.GetProperty("oldText").GetString() ?? string.Empty;
        var newText = arguments.GetProperty("newText").GetString() ?? string.Empty;
        if (oldText.Length == 0)
        {
            throw new RemoteToolException("empty_match", "oldText must not be empty.");
        }

        var first = content.IndexOf(oldText, StringComparison.Ordinal);
        if (first < 0 || content.IndexOf(oldText, first + oldText.Length, StringComparison.Ordinal) >= 0)
        {
            throw new RemoteToolException("match_not_unique", "oldText must occur exactly once.");
        }

        var updated = content[..first] + newText + content[(first + oldText.Length)..];
        if (updated.Length > MaximumTextCharacters)
        {
            throw new RemoteToolException("file_too_large", "The edited file exceeds the text size limit.");
        }

        var updatedBytes = StrictUtf8.GetBytes(updated);
        stream.Position = 0;
        stream.SetLength(0);
        await stream.WriteAsync(updatedBytes, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
        return JsonSerializer.SerializeToElement(new
        {
            path = Path.GetRelativePath(root, fullPath),
            edited = true,
            sha256 = Convert.ToHexString(SHA256.HashData(updatedBytes)).ToLowerInvariant()
        }, ProtocolJson.Options);
    }

    private static string ResolvePath(string root, string relativePath, bool mustExist)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath) || relativePath.IndexOf('\0') >= 0)
        {
            throw new RemoteToolException("invalid_path", "The path must be a non-empty path relative to the selected workspace.");
        }

        var fullPath = Path.GetFullPath(Path.Combine(root, relativePath));
        var relative = Path.GetRelativePath(root, fullPath);
        if (relative == "." || Path.IsPathRooted(relative) || relative == ".." ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new RemoteToolException("path_outside_workspace", "The path resolves outside the selected workspace.");
        }

        EnsureExistingAncestorsAreSafe(root, mustExist ? fullPath : Path.GetDirectoryName(fullPath) ?? root);
        if (mustExist && !File.Exists(fullPath))
        {
            throw new RemoteToolException("file_not_found", "The requested workspace file does not exist.");
        }

        if (File.Exists(fullPath) && (File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
        {
            throw new RemoteToolException("reparse_point_denied", "Workspace links and reparse points are not available to remote tools.");
        }

        return fullPath;
    }

    private static void EnsureExistingAncestorsAreSafe(string root, string path)
    {
        var current = path;
        while (!string.Equals(current, root, StringComparison.OrdinalIgnoreCase))
        {
            if ((Directory.Exists(current) || File.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new RemoteToolException("reparse_point_denied", "Workspace links and reparse points are not available to remote tools.");
            }

            current = Path.GetDirectoryName(current) ?? root;
            if (!current.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                throw new RemoteToolException("path_outside_workspace", "The path resolves outside the selected workspace.");
            }
        }
    }

    private static JsonElement Schema(string json) => RemoteToolRegistry.ParseSchema(json);

    private sealed class DelegateTool(
        RemoteToolDescriptor descriptor,
        string approvalKey,
        string permissionDescription,
        Func<JsonElement, CancellationToken, Task<JsonElement>> execute) : IRemoteTool
    {
        public RemoteToolDescriptor Descriptor { get; } = descriptor;
        public string? ApprovalKey { get; } = approvalKey;
        public string PermissionDescription { get; } = permissionDescription;
        public Task<JsonElement> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken) =>
            execute(arguments, cancellationToken);
    }
}
