using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace RemoteCli.Local;

public sealed record ConversationMessageInput(
    string MessageId,
    string Role,
    DateTimeOffset Timestamp,
    long Sequence,
    string Content,
    Guid? SessionId,
    Guid? CommandId);

public sealed record ConversationLogEntry(
    string Kind,
    string ConversationId,
    string ClientName,
    string ClientInstanceId,
    DateTimeOffset SyncedAt,
    bool IsCompleteSnapshot,
    bool IsPossiblyIncomplete,
    string? MessageId,
    string? Role,
    DateTimeOffset? Timestamp,
    long? Sequence,
    string? Content,
    Guid? SessionId,
    Guid? CommandId);

public sealed record ConversationStatus(
    string ConversationId,
    string Mode,
    string ClientName,
    int MessageCount,
    bool IsPossiblyIncomplete,
    DateTimeOffset LastSyncAt);

public sealed class ConversationStore
{
    private const int MaximumMessageCountPerSync = 1000;
    private const int MaximumMessageCharacters = 256 * 1024;
    private const int MaximumSyncCharacters = 4 * 1024 * 1024;
    private const long MaximumConversationBytes = 128L * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 24
    };
    private static readonly Regex LabeledSecretPattern = new(
        "(?<label>(?:one[\\s_-]*time[\\s_-]*(?:password|secret|code)|invite[\\s_-]*(?:password|secret)|invitation[\\s_-]*(?:password|secret)|pairing[\\s_-]*(?:password|secret)|session[\\s_-]*(?:key|secret)|auth(?:entication)?[\\s_-]*(?:token|secret|key)|client[\\s_-]*secret|password|secret|private[\\s_-]*key|access[\\s_-]*token|refresh[\\s_-]*token|token|proof|nonce|challenge|credential|certificate)\\s*[\"']?\\s*[:=]\\s*[\"']?)(?<value>[^\"'\\s,;}\\]]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly string _rootDirectory;
    private readonly bool _enabled;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public ConversationStore(string installDirectory, bool enabled)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installDirectory);
        _rootDirectory = Path.Combine(Path.GetFullPath(installDirectory), "data", "conversations");
        _enabled = enabled;
    }

    public bool IsEnabled => _enabled;

    public async Task<int> SyncAsync(
        string conversationId,
        string clientName,
        string clientInstanceId,
        bool isCompleteSnapshot,
        bool isPossiblyIncomplete,
        IReadOnlyCollection<ConversationMessageInput> messages,
        CancellationToken cancellationToken)
    {
        if (!_enabled)
        {
            throw new InvalidOperationException("Conversation storage is disabled. Start the local MCP server with --enable-conversation-storage to opt in.");
        }

        ValidateIdentifier(conversationId, nameof(conversationId));
        ValidateDisplayValue(clientName, nameof(clientName), 128);
        ValidateIdentifier(clientInstanceId, nameof(clientInstanceId));
        ArgumentNullException.ThrowIfNull(messages);
        if (messages.Count > MaximumMessageCountPerSync)
        {
            throw new ArgumentOutOfRangeException(nameof(messages), "A sync may contain at most 1000 messages.");
        }

        var characterCount = 0;
        foreach (var message in messages)
        {
            ValidateMessage(message);
            characterCount = checked(characterCount + message.Content.Length);
            if (characterCount > MaximumSyncCharacters)
            {
                throw new ArgumentOutOfRangeException(nameof(messages), "The sync exceeds the total content limit.");
            }
        }

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureStorageDirectory();
            var path = GetConversationPath(conversationId);
            if (File.Exists(path) && new FileInfo(path).Length > MaximumConversationBytes)
            {
                throw new IOException("Conversation storage limit reached for this conversation.");
            }

            var existingIds = await ReadMessageIdsAsync(path, cancellationToken).ConfigureAwait(false);
            var syncTime = DateTimeOffset.UtcNow;
            var newMessages = messages.Where(message => !existingIds.Contains(message.MessageId)).ToArray();

            using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read, 16 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough);
            await WriteLineAsync(stream, new ConversationLogEntry(
                "sync", conversationId, clientName.Trim(), clientInstanceId.Trim(), syncTime,
                isCompleteSnapshot, isPossiblyIncomplete, null, null, null, null, null, null, null), cancellationToken).ConfigureAwait(false);

            foreach (var message in newMessages)
            {
                var content = RedactSecrets(message.Content);
                await WriteLineAsync(stream, new ConversationLogEntry(
                    "message", conversationId, clientName.Trim(), clientInstanceId.Trim(), syncTime,
                    isCompleteSnapshot, isPossiblyIncomplete, message.MessageId, message.Role,
                    message.Timestamp, message.Sequence, content, message.SessionId, message.CommandId), cancellationToken).ConfigureAwait(false);
            }

            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
            return newMessages.Length;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<IReadOnlyList<ConversationStatus>> ListAsync(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_rootDirectory))
        {
            return [];
        }

        EnsureStorageDirectory();
        var files = Directory.GetFiles(_rootDirectory, "*.jsonl", SearchOption.TopDirectoryOnly);
        var statuses = new List<ConversationStatus>(files.Length);
        foreach (var file in files)
        {
            var entries = await ReadEntriesAsync(file, cancellationToken).ConfigureAwait(false);
            var sync = entries.LastOrDefault(entry => entry.Kind == "sync");
            if (sync is null)
            {
                continue;
            }

            statuses.Add(new ConversationStatus(
                sync.ConversationId,
                sync.IsCompleteSnapshot ? "FullReported" : "ToolCallsOnly",
                sync.ClientName,
                entries.Count(entry => entry.Kind == "message"),
                sync.IsPossiblyIncomplete,
                sync.SyncedAt));
        }

        return statuses.OrderByDescending(status => status.LastSyncAt).ToArray();
    }

    public async Task<IReadOnlyList<ConversationLogEntry>> ReadAsync(string conversationId, CancellationToken cancellationToken)
    {
        ValidateIdentifier(conversationId, nameof(conversationId));
        var path = GetConversationPath(conversationId);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Conversation was not found.");
        }

        EnsureStorageDirectory();
        var entries = await ReadEntriesAsync(path, cancellationToken).ConfigureAwait(false);
        if (entries.Any(entry => !string.Equals(entry.ConversationId, conversationId, StringComparison.Ordinal)))
        {
            throw new InvalidDataException("Conversation identifier does not match its storage record.");
        }

        return entries;
    }

    public async Task ExportAsync(string conversationId, string format, TextWriter writer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(writer);
        var entries = await ReadAsync(conversationId, cancellationToken).ConfigureAwait(false);
        if (string.Equals(format, "json", StringComparison.OrdinalIgnoreCase))
        {
            await writer.WriteLineAsync(JsonSerializer.Serialize(entries, JsonOptions)).ConfigureAwait(false);
            return;
        }

        if (!string.Equals(format, "markdown", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Format must be json or markdown.", nameof(format));
        }

        var lastSync = entries.LastOrDefault(entry => entry.Kind == "sync");
        await writer.WriteLineAsync($"# Conversation {conversationId}").ConfigureAwait(false);
        await writer.WriteLineAsync().ConfigureAwait(false);
        await writer.WriteLineAsync($"- Mode: `{(lastSync?.IsCompleteSnapshot == true ? "FullReported" : "ToolCallsOnly")}`").ConfigureAwait(false);
        await writer.WriteLineAsync($"- Client: {lastSync?.ClientName ?? "unknown"}").ConfigureAwait(false);
        await writer.WriteLineAsync($"- Possibly incomplete: `{lastSync?.IsPossiblyIncomplete ?? true}`").ConfigureAwait(false);
        await writer.WriteLineAsync().ConfigureAwait(false);

        foreach (var message in entries.Where(entry => entry.Kind == "message"))
        {
            await writer.WriteLineAsync($"## {message.Role} · {message.Timestamp:O}").ConfigureAwait(false);
            await writer.WriteLineAsync().ConfigureAwait(false);
            await writer.WriteLineAsync(message.Content ?? string.Empty).ConfigureAwait(false);
            await writer.WriteLineAsync().ConfigureAwait(false);
        }
    }

    private static void ValidateMessage(ConversationMessageInput? message)
    {
        ArgumentNullException.ThrowIfNull(message);
        ValidateIdentifier(message.MessageId, "messageId");
        ValidateDisplayValue(message.Role, "role", 32);
        if (message.Sequence < 0 || message.Content is null || message.Content.Length > MaximumMessageCharacters)
        {
            throw new ArgumentOutOfRangeException(nameof(message), "Message sequence or content length is invalid.");
        }
    }

    private static void ValidateIdentifier(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256 || value.Any(char.IsControl))
        {
            throw new ArgumentException("Identifier is empty, too long, or contains control characters.", parameterName);
        }
    }

    private static void ValidateDisplayValue(string value, string parameterName, int maximumCharacters)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumCharacters || value.Any(char.IsControl))
        {
            throw new ArgumentException("Value is empty, too long, or contains control characters.", parameterName);
        }
    }

    private void EnsureStorageDirectory()
    {
        var installDirectory = Path.GetFullPath(Path.GetDirectoryName(Path.GetDirectoryName(_rootDirectory)!)!);
        RejectReparsePoint(installDirectory);
        var dataDirectory = Path.Combine(installDirectory, "data");
        if (Directory.Exists(dataDirectory))
        {
            RejectReparsePoint(dataDirectory);
        }
        else
        {
            Directory.CreateDirectory(dataDirectory);
        }

        if (Directory.Exists(_rootDirectory))
        {
            RejectReparsePoint(_rootDirectory);
        }
        else
        {
            Directory.CreateDirectory(_rootDirectory);
        }
    }

    private static void RejectReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("Conversation storage path must not contain a symbolic link or reparse point.");
        }
    }

    private string GetConversationPath(string conversationId)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(conversationId));
        var fileName = Convert.ToHexString(digest).ToLowerInvariant() + ".jsonl";
        return Path.Combine(_rootDirectory, fileName);
    }

    private static async Task<HashSet<string>> ReadMessageIdsAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in await ReadEntriesAsync(path, cancellationToken).ConfigureAwait(false))
        {
            if (entry.Kind == "message" && entry.MessageId is not null)
            {
                ids.Add(entry.MessageId);
            }
        }

        return ids;
    }

    private static async Task<List<ConversationLogEntry>> ReadEntriesAsync(string path, CancellationToken cancellationToken)
    {
        var result = new List<ConversationLogEntry>();
        using var reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 16 * 1024, FileOptions.Asynchronous));
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (line.Length > MaximumSyncCharacters * 2)
            {
                throw new InvalidDataException("Conversation log contains an oversized record.");
            }

            var entry = JsonSerializer.Deserialize<ConversationLogEntry>(line, JsonOptions)
                ?? throw new InvalidDataException("Conversation log contains an empty record.");
            result.Add(entry);
        }

        return result;
    }

    private static async Task WriteLineAsync(Stream stream, ConversationLogEntry entry, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(entry, JsonOptions);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync("\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
    }

    private static string RedactSecrets(string content)
    {
        return LabeledSecretPattern.Replace(content, match => match.Groups["label"].Value + "[REDACTED]");
    }
}
