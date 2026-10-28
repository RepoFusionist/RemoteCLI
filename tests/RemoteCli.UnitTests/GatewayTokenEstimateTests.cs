using RemoteCli.Desktop;

namespace RemoteCli.UnitTests;

public sealed class GatewayTokenEstimateTests
{
    [Fact]
    public void CountsRepeatedHistoryAndToolResultsForEachRequest()
    {
        var estimates = new GatewayTokenEstimate();
        var first = estimates.AddRequest(["hello world", "你好"]);
        var second = estimates.AddRequest(["hello world", "你好", "list_files", "{\"path\":\".\"}", "file.txt"]);

        Assert.Equal(1, first.Round);
        Assert.Equal(5, first.InputTokens);
        Assert.Equal(first.InputTokens, first.TotalInputTokens);
        Assert.Equal(2, second.Round);
        Assert.True(second.InputTokens > first.InputTokens);
        Assert.Equal(first.InputTokens + second.InputTokens, second.TotalInputTokens);
    }

    [Fact]
    public void EmptyAndShortTextStayBoundedAndEachRequestIsIndependent()
    {
        var estimates = new GatewayTokenEstimate();
        var empty = estimates.AddRequest([null, ""]);
        var shortText = estimates.AddRequest(["a"]);
        var another = new GatewayTokenEstimate().AddRequest(["a"]);

        Assert.Equal(0, empty.InputTokens);
        Assert.Equal(1, shortText.InputTokens);
        Assert.Equal(1, shortText.TotalInputTokens);
        Assert.Equal((1, 1L, 1L), another);
    }
}
