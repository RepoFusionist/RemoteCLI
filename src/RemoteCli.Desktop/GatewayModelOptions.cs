using System.Text.Json;
using System.Text.Json.Nodes;

namespace RemoteCli.Desktop;

internal sealed record GatewayModelOptions(double? Temperature, double? TopP, int? MaxOutputTokens, int? ContextWindow = null)
{
    internal static GatewayModelOptions Read(JsonElement request)
    {
        if (!request.TryGetProperty("modelOptions", out var value) || value.ValueKind != JsonValueKind.Object)
            return new(null, null, null);
        return new(Number(value, "temperature", 0, 2), Number(value, "topP", 0, 1),
            value.TryGetProperty("maxOutputTokens", out var max) && max.ValueKind == JsonValueKind.Number && max.TryGetInt32(out var parsed) && parsed is >= 1 and <= 1000000 ? parsed : null,
            value.TryGetProperty("contextWindow", out var window) && window.ValueKind == JsonValueKind.Number && window.TryGetInt32(out var tokens) && tokens is >= 8192 and <= 2000000 ? tokens : null);
    }
    private static double? Number(JsonElement value, string name, double min, double max) =>
        value.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.Number &&
        element.TryGetDouble(out var number) && double.IsFinite(number) && number >= min && number <= max ? number : null;

    internal void Apply(JsonObject body)
    {
        if (Temperature is { } temperature) body["temperature"] = temperature;
        if (TopP is { } topP) body["top_p"] = topP;
        if (MaxOutputTokens is { } maxTokens) body["max_tokens"] = maxTokens;
    }
}
