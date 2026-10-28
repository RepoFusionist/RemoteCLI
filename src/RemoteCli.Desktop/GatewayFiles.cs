using System.Text.Json;

namespace RemoteCli.Desktop;

internal sealed record GatewayFile(string Name, string MediaType, long Size, string? Content);

/// <summary>Validates lightweight attachments sent from the WebView. Binary files remain metadata-only; text files are supplied as context.</summary>
internal static class GatewayFiles
{
    internal const int MaximumFiles = 4;
    internal const int MaximumFileBytes = 1024 * 1024;
    internal const int MaximumTotalBytes = 3 * 1024 * 1024;

    internal static IReadOnlyList<GatewayFile> ParseMessageFiles(JsonElement message)
    {
        if (!message.TryGetProperty("files", out var files)) return [];
        if (files.ValueKind != JsonValueKind.Array) throw new ArgumentException("文件附件格式无效。");
        var parsed = new List<GatewayFile>();
        var total = 0L;
        foreach (var file in files.EnumerateArray())
        {
            if (file.ValueKind != JsonValueKind.Object) throw new ArgumentException("文件附件格式无效。");
            var name = Path.GetFileName(ReadString(file, "name"));
            var mime = ReadString(file, "mime").Trim().ToLowerInvariant();
            var size = file.TryGetProperty("size", out var sizeElement) && sizeElement.TryGetInt64(out var parsedSize) ? parsedSize : 0L;
            var content = file.TryGetProperty("content", out var contentElement) && contentElement.ValueKind == JsonValueKind.String
                ? contentElement.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(name) || name.Length > 255 || size <= 0 || size > MaximumFileBytes)
                throw new ArgumentException("文件为空或超过 1 MiB。");
            if (content is not null && content.Length > MaximumFileBytes)
                throw new ArgumentException("文本文件内容超过 1 MiB。");
            total += size;
            if (parsed.Count >= MaximumFiles || total > MaximumTotalBytes)
                throw new ArgumentException("每次请求最多 4 个文件、总计 3 MiB。");
            parsed.Add(new GatewayFile(name, string.IsNullOrWhiteSpace(mime) ? "application/octet-stream" : mime, size, content));
        }
        return parsed;
    }

    private static string ReadString(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
}
