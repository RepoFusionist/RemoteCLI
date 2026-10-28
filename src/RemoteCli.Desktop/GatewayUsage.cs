using System.Text.Json;

namespace RemoteCli.Desktop;

// Streaming providers may report input and output in separate events.
internal sealed class GatewayUsage
{
    private long? _input, _output, _total, _cacheRead, _cacheCreation;

    internal void Read(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return;
        if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object) ReadUsage(usage);
        if (root.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object &&
            message.TryGetProperty("usage", out var nested) && nested.ValueKind == JsonValueKind.Object) ReadUsage(nested);
    }

    private void ReadUsage(JsonElement usage)
    {
        _input = Number(usage, "input_tokens") ?? Number(usage, "prompt_tokens") ?? _input;
        _output = Number(usage, "output_tokens") ?? Number(usage, "completion_tokens") ?? _output;
        _total = Number(usage, "total_tokens") ?? _total;
        _cacheRead = Number(usage, "cache_read_input_tokens") ?? _cacheRead;
        _cacheCreation = Number(usage, "cache_creation_input_tokens") ?? _cacheCreation;
    }

    private static long? Number(JsonElement item, string key) =>
        item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt64(out var count) && count >= 0 && count <= 1000000000000 ? count : null;

    internal long? Input => _input is null ? null : _input + (_cacheRead ?? 0) + (_cacheCreation ?? 0);
    internal long? Output => _output;
    internal long? Total => _total ?? (Input is not null && Output is not null ? Input + Output : null);
}
