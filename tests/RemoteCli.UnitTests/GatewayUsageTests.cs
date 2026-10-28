using System.Text.Json;
using System.Text.Json.Nodes;
using RemoteCli.Desktop;

namespace RemoteCli.UnitTests;

public sealed class GatewayUsageTests
{
    [Fact]
    public void ReadsActualOpenAiUsageWithoutEstimatingMissingValues()
    {
        var usage = new GatewayUsage();
        using var empty = JsonDocument.Parse("{\"choices\":[]}");
        usage.Read(empty.RootElement);
        Assert.Null(usage.Total);
        using var response = JsonDocument.Parse("{\"usage\":{\"prompt_tokens\":120,\"completion_tokens\":30,\"total_tokens\":150}}");
        usage.Read(response.RootElement);
        Assert.Equal(120, usage.Input);
        Assert.Equal(150, usage.Total);
    }

    [Fact]
    public void MergesAnthropicStreamUsageAndCachedInput()
    {
        var usage = new GatewayUsage();
        using var start = JsonDocument.Parse("{\"message\":{\"usage\":{\"input_tokens\":100,\"cache_read_input_tokens\":400,\"cache_creation_input_tokens\":20}}}");
        using var delta = JsonDocument.Parse("{\"usage\":{\"output_tokens\":30}}");
        usage.Read(start.RootElement);
        Assert.Null(usage.Total);
        usage.Read(delta.RootElement);
        Assert.Equal(520, usage.Input);
        Assert.Equal(550, usage.Total);
    }

    [Fact]
    public void ReadsOnlyValidModelSamplingOptions()
    {
        using var good = JsonDocument.Parse("{\"modelOptions\":{\"temperature\":0.7,\"topP\":0.9,\"maxOutputTokens\":4096}}");
        var options = GatewayModelOptions.Read(good.RootElement);
        Assert.Equal(0.7, options.Temperature);
        Assert.Equal(0.9, options.TopP);
        Assert.Equal(4096, options.MaxOutputTokens);
        var body = new JsonObject(); options.Apply(body);
        Assert.Equal(0.7, body["temperature"]!.GetValue<double>());
        Assert.Equal(0.9, body["top_p"]!.GetValue<double>());
        Assert.Equal(4096, body["max_tokens"]!.GetValue<int>());
        using var invalid = JsonDocument.Parse("{\"modelOptions\":{\"temperature\":5,\"topP\":-1,\"maxOutputTokens\":\"wrong\"}}");
        Assert.Equal(new GatewayModelOptions(null, null, null), GatewayModelOptions.Read(invalid.RootElement));
    }
}
