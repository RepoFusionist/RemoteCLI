using System.Text.Json;
using System.Text.Json.Nodes;

namespace RemoteCli.Desktop;

internal sealed record GatewayImage(string Name, string MediaType, string Base64)
{
    internal string DataUrl => "data:" + MediaType + ";base64," + Base64;
}

/// <summary>Validates user-selected images and converts them into model-specific content blocks.</summary>
internal sealed class GatewayImages
{
    internal const int MaximumImages = 4;
    internal const int MaximumImageBytes = 1024 * 1024;
    internal const int MaximumTotalBytes = 3 * 1024 * 1024;
    private int _count;
    private int _totalBytes;

    // One instance per request; calling for every user message enforces a request-wide budget.
    internal IReadOnlyList<GatewayImage> ParseMessageImages(JsonElement message)
    {
        if (!message.TryGetProperty("images", out var images)) return [];
        if (images.ValueKind != JsonValueKind.Array) throw new ArgumentException("图片附件格式无效。");
        var parsed = new List<GatewayImage>();
        foreach (var image in images.EnumerateArray())
        {
            if (image.ValueKind != JsonValueKind.Object) throw new ArgumentException("图片附件格式无效。");
            var url = ReadString(image, "dataUrl");
            var mediaType = ReadString(image, "mime").ToLowerInvariant();
            var prefix = "data:" + mediaType + ";base64,";
            if (!IsSupported(mediaType) || !url.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("图片必须为 PNG、JPEG、GIF 或 WebP。");
            var base64 = url[prefix.Length..];
            if (base64.Length > ((MaximumImageBytes + 2) / 3) * 4) throw new ArgumentException("图片超过 1 MiB。");
            byte[] bytes;
            try { bytes = Convert.FromBase64String(base64); }
            catch (FormatException) { throw new ArgumentException("图片数据不是有效的 Base64。" ); }
            Validate(mediaType, bytes);
            _count++;
            _totalBytes += bytes.Length;
            if (_count > MaximumImages || _totalBytes > MaximumTotalBytes) throw new ArgumentException("每次请求最多 4 张图片、总计 3 MiB。" );
            var name = Path.GetFileName(ReadString(image, "name"));
            if (name.Length > 255) name = name[..255];
            parsed.Add(new GatewayImage(name, mediaType, Convert.ToBase64String(bytes)));
        }
        return parsed;
    }

    // Intended for files explicitly selected by the user in the native picker.
    internal static async Task<IReadOnlyList<GatewayImage>> LoadFilesAsync(IEnumerable<string> files, CancellationToken token)
    {
        var selected = files.Take(MaximumImages + 1).ToArray();
        if (selected.Length > MaximumImages) throw new ArgumentException("每次最多选择 4 张图片。" );
        var results = new List<GatewayImage>(); var total = 0;
        foreach (var file in selected)
        {
            token.ThrowIfCancellationRequested();
            var info = new FileInfo(file);
            if (!info.Exists || info.Length is <= 0 or > MaximumImageBytes) throw new ArgumentException("图片不存在或超过 1 MiB。" );
            var mediaType = info.Extension.ToLowerInvariant() switch
            {
                ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".gif" => "image/gif", ".webp" => "image/webp",
                _ => throw new ArgumentException("仅支持 PNG、JPEG、GIF 和 WebP。" )
            };
            await using var stream = new FileStream(info.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, FileOptions.Asynchronous);
            if (stream.Length is <= 0 or > MaximumImageBytes) throw new ArgumentException("图片不存在或超过 1 MiB。" );
            var bytes = new byte[(int)stream.Length];
            await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
            Validate(mediaType, bytes);
            total += bytes.Length;
            if (total > MaximumTotalBytes) throw new ArgumentException("图片总计不能超过 3 MiB。" );
            results.Add(new GatewayImage(info.Name, mediaType, Convert.ToBase64String(bytes)));
        }
        return results;
    }

    internal static JsonArray BuildOpenAiContent(string? text, IReadOnlyList<GatewayImage> images)
    {
        var content = new JsonArray();
        if (!string.IsNullOrEmpty(text)) content.Add(new JsonObject { ["type"] = "text", ["text"] = text });
        foreach (var image in images) content.Add(new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = image.DataUrl } });
        return content;
    }

    internal static JsonArray BuildAnthropicContent(string? text, IReadOnlyList<GatewayImage> images)
    {
        var content = new JsonArray();
        if (!string.IsNullOrEmpty(text)) content.Add(new JsonObject { ["type"] = "text", ["text"] = text });
        foreach (var image in images) content.Add(new JsonObject
        {
            ["type"] = "image", ["source"] = new JsonObject { ["type"] = "base64", ["media_type"] = image.MediaType, ["data"] = image.Base64 }
        });
        return content;
    }

    private static bool IsSupported(string value) => value is "image/png" or "image/jpeg" or "image/gif" or "image/webp";
    private static string ReadString(JsonElement item, string name) => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    private static void Validate(string mime, byte[] bytes)
    {
        if (bytes.Length is <= 0 or > MaximumImageBytes) throw new ArgumentException("图片为空或超过 1 MiB。" );
        var valid = mime switch
        {
            "image/png" => bytes.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }),
            "image/jpeg" => bytes.Length >= 3 && bytes[0] == 0xff && bytes[1] == 0xd8 && bytes[2] == 0xff,
            "image/gif" => bytes.AsSpan().StartsWith("GIF87a"u8) || bytes.AsSpan().StartsWith("GIF89a"u8),
            "image/webp" => bytes.Length >= 12 && bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) && bytes.AsSpan(8, 4).SequenceEqual("WEBP"u8),
            _ => false
        };
        if (!valid) throw new ArgumentException("图片内容与文件类型不匹配。" );
    }
}
