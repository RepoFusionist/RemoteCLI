using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RemoteCli.Desktop;

internal sealed record ToolCall(string Id, string Name, string Arguments);
internal sealed class ChatTurn
{
    public string Role { get; init; } = "user";
    public string? Content { get; init; }
    public IReadOnlyList<ToolCall>? ToolCalls { get; init; }
    public string? ToolCallId { get; init; }
    public IReadOnlyList<GatewayImage>? Images { get; init; }
}

internal sealed record GatewayHistoryState(string Revision, string WorkspaceRoot, List<ChatTurn> History, int IndexedMessages);

internal sealed class GatewayHistoryStore(string? root = null)
{
    private readonly string directory = root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RemoteCLI", "gateway-history");
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    private string PathFor(string id) => Path.Combine(directory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id))) + ".json");

    internal GatewayHistoryState Load(string id, string workspace, string revision)
    {
        try
        {
            var state = JsonSerializer.Deserialize<GatewayHistoryState>(File.ReadAllBytes(PathFor(id)), Options);
            if (state is null || state.Revision != revision || state.History is null ||
                !string.Equals(state.WorkspaceRoot, workspace, StringComparison.OrdinalIgnoreCase))
                throw new IOException("对话历史版本或工作区不匹配。请重试以从对话记录恢复。");
            return state;
        }
        catch (JsonException ex) { throw new IOException("对话历史文件已损坏，请重试以从对话记录恢复。", ex); }
    }

    internal string Save(string id, string workspace, IReadOnlyList<ChatTurn> history, int indexedMessages, string? expectedRevision)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new IOException("缺少对话编号，无法安全保存上下文。");
        Directory.CreateDirectory(directory);
        var path = PathFor(id);
        using var mutex = new Mutex(false, "Local\\RemoteCLI-History-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path)))));
        var acquired = false;
        try
        {
            try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(10)); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) throw new IOException("对话历史正在其他进程中保存，请重试。");
            if (!string.IsNullOrEmpty(expectedRevision)) Load(id, workspace, expectedRevision);
            var revision = Guid.NewGuid().ToString("N");
            var state = new GatewayHistoryState(revision, workspace, history.ToList(), indexedMessages);
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temp, JsonSerializer.Serialize(state, Options), new UTF8Encoding(false));
                File.Move(temp, path, overwrite: true);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
            return revision;
        }
        finally { if (acquired) mutex.ReleaseMutex(); }
    }

    internal string CommitCompression(string id, string workspace, IReadOnlyList<ChatTurn> compact, int indexedMessages,
        string? revision, Func<bool> persistIndex)
    {
        if (!persistIndex()) throw new IOException("无法保存摘要索引，原上下文已保留。");
        return Save(id, workspace, compact, indexedMessages, revision);
    }
}

internal sealed record GatewaySummary(string Text, long? TotalTokens);

internal static class GatewayHistorySummarizer
{
    internal static IEnumerable<string> Chunks(string text, int size)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(size, 2);
        for (var offset = 0; offset < text.Length;)
        {
            var length = Math.Min(size, text.Length - offset);
            if (offset + length < text.Length && char.IsHighSurrogate(text[offset + length - 1])) length--;
            yield return text.Substring(offset, length);
            offset += length;
        }
    }

    internal static async Task<GatewaySummary> SummarizeAsync(IReadOnlyList<ChatTurn> prefix, int contextWindow,
        Func<string, CancellationToken, Task<GatewaySummary>> summarize, CancellationToken token)
    {
        // Include the whole selected prefix. Do not remove messages whose tail never reached the summarizer.
        var transcript = string.Join("\n", prefix.Select(turn => JsonSerializer.Serialize(new
        { turn.Role, turn.Content, turn.ToolCalls, turn.ToolCallId, Images = turn.Images?.Select(image => image.Name) })));
        var chunkSize = Math.Clamp((contextWindow - 2048) / 2, 512, 12000);
        long total = 0; var completeUsage = true;
        for (var pass = 0; pass < 8; pass++)
        {
            var summaries = new List<string>();
            foreach (var chunk in Chunks(transcript, chunkSize))
            {
                token.ThrowIfCancellationRequested();
                var result = await summarize(chunk, token).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(result.Text)) throw new IOException("摘要模型未返回有效摘要，原上下文已保留。");
                summaries.Add(result.Text.Trim());
                if (result.TotalTokens is { } usage) total += usage; else completeUsage = false;
            }
            var joined = string.Join("\n\n", summaries);
            if (joined.Length <= ContextCompression.MaximumSummaryCharacters) return new(joined, completeUsage ? total : null);
            if (joined.Length >= transcript.Length) throw new IOException("摘要没有缩短上下文，原消息已保留。");
            transcript = joined;
        }
        throw new IOException("摘要合并达到上限，原消息已保留。");
    }
}
