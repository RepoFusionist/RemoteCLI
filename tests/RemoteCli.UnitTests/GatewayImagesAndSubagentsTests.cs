using System.Text.Json;
using RemoteCli.Desktop;

namespace RemoteCli.UnitTests;

public sealed class GatewayImagesAndSubagentsTests
{
    private static JsonElement Args(object value) => JsonSerializer.SerializeToElement(value);
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a86cAAAAASUVORK5CYII=");

    [Fact]
    public void ImagesAreRenderedForBothSupportedGatewayFormats()
    {
        var message = Args(new { images = new[] { new { name = "pixel.png", mime = "image/png", dataUrl = "data:image/png;base64," + Convert.ToBase64String(Png) } } });
        var images = new GatewayImages().ParseMessageImages(message);
        Assert.Single(images);
        var openAi = GatewayImages.BuildOpenAiContent("what is this?", images);
        Assert.Equal("text", (string?)openAi[0]!["type"]);
        Assert.Equal("image_url", (string?)openAi[1]!["type"]);
        Assert.StartsWith("data:image/png;base64,", (string?)openAi[1]!["image_url"]!["url"]);
        var anthropic = GatewayImages.BuildAnthropicContent("what is this?", images);
        Assert.Equal("image", (string?)anthropic[1]!["type"]);
        Assert.Equal("image/png", (string?)anthropic[1]!["source"]!["media_type"]);
        Assert.Equal(Convert.ToBase64String(Png), (string?)anthropic[1]!["source"]!["data"]);
    }

    [Fact]
    public void ImageBudgetAppliesAcrossAllUserMessagesAndRejectsInvalidData()
    {
        var budget = new GatewayImages();
        var valid = new { name = "x.png", mime = "image/png", dataUrl = "data:image/png;base64," + Convert.ToBase64String(Png) };
        Assert.Equal(3, budget.ParseMessageImages(Args(new { images = new[] { valid, valid, valid } })).Count);
        Assert.Single(budget.ParseMessageImages(Args(new { images = new[] { valid } })));
        Assert.Throws<ArgumentException>(() => budget.ParseMessageImages(Args(new { images = new[] { valid } })));
        Assert.Throws<ArgumentException>(() => new GatewayImages().ParseMessageImages(Args(new { images = new[] { new { name = "x.png", mime = "image/png", dataUrl = "data:image/png;base64,AAAA" } } })));
        Assert.Throws<ArgumentException>(() => new GatewayImages().ParseMessageImages(Args(new { images = new[] { new { name = "x.svg", mime = "image/svg+xml", dataUrl = "data:image/svg+xml;base64,AAAA" } } })));
        var maxBytes = new byte[GatewayImages.MaximumImageBytes];
        Array.Copy(Png, maxBytes, Png.Length);
        var large = new { name = "large.png", mime = "image/png", dataUrl = "data:image/png;base64," + Convert.ToBase64String(maxBytes) };
        var sizeBudget = new GatewayImages();
        Assert.Equal(3, sizeBudget.ParseMessageImages(Args(new { images = new[] { large, large, large } })).Count);
        Assert.Throws<ArgumentException>(() => sizeBudget.ParseMessageImages(Args(new { images = new[] { valid } })));
    }

    [Fact]
    public async Task NativeImageLoaderValidatesBytesAndReturnsDataUrl()
    {
        var path = Path.Combine(Path.GetTempPath(), "longshell-image-" + Guid.NewGuid().ToString("N") + ".png");
        try
        {
            await File.WriteAllBytesAsync(path, Png);
            var image = Assert.Single(await GatewayImages.LoadFilesAsync([path], CancellationToken.None));
            Assert.Equal("image/png", image.MediaType);
            Assert.Equal("data:image/png;base64," + Convert.ToBase64String(Png), image.DataUrl);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task SubagentSpawnsAndReturnsIndependentResult()
    {
        var sawTask = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var session = new GatewaySubagents(async (task, token) =>
        {
            sawTask.TrySetResult(task);
            await release.Task.WaitAsync(token);
            return "independent answer";
        }, CancellationToken.None);
        var tools = session.Create();
        var start = await tools[0].Execute(Args(new { task = "inspect code" }), CancellationToken.None);
        using var spawned = JsonDocument.Parse(start);
        var id = spawned.RootElement.GetProperty("agent_id").GetString();
        Assert.Equal("inspect code", await sawTask.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        var pending = await tools[1].Execute(Args(new { agent_id = id, wait_ms = 0 }), CancellationToken.None);
        Assert.Contains("running", pending);
        release.SetResult();
        var result = await tools[1].Execute(Args(new { agent_id = id, wait_ms = 20000 }), CancellationToken.None);
        Assert.Contains("completed", result);
        Assert.Contains("independent answer", result);
    }

    [Fact]
    public async Task SubagentLifecyclePublishesTraceUpdates()
    {
        var updates = new System.Collections.Concurrent.ConcurrentQueue<GatewaySubagentUpdate>();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var session = new GatewaySubagents(async (_, token) =>
        {
            await release.Task.WaitAsync(token);
            return "trace result";
        }, CancellationToken.None, updates.Enqueue);
        var tools = session.Create();
        using var spawned = JsonDocument.Parse(await tools[0].Execute(Args(new { task = "trace this" }), CancellationToken.None));
        var id = spawned.RootElement.GetProperty("agent_id").GetString();
        Assert.Contains(updates, update => update.AgentId == id && update.Task == "trace this" && update.Status == "running");
        release.SetResult();
        await tools[1].Execute(Args(new { agent_id = id, wait_ms = 20000 }), CancellationToken.None);
        Assert.Contains(updates, update => update.AgentId == id && update.Status == "completed" && update.Result == "trace result");
    }

    [Fact]
    public async Task SubagentCancellationAndOwnershipAreEnforced()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var session = new GatewaySubagents(async (_, token) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return "should not run";
        }, CancellationToken.None);
        var tools = session.Create();
        Assert.StartsWith("错误：", await tools[1].Execute(Args(new { agent_id = "unknown", wait_ms = 0 }), CancellationToken.None));
        using var spawned = JsonDocument.Parse(await tools[0].Execute(Args(new { task = "task" }), CancellationToken.None));
        var id = spawned.RootElement.GetProperty("agent_id").GetString();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains("cancellation_requested", await tools[2].Execute(Args(new { agent_id = id }), CancellationToken.None));
        var result = await tools[1].Execute(Args(new { agent_id = id, wait_ms = 20000 }), CancellationToken.None);
        Assert.Contains("cancelled", result);
    }

    [Fact]
    public async Task ParentCancellationStopsChildrenAndConcurrencyIsBounded()
    {
        using var parent = new CancellationTokenSource();
        var starts = 0;
        var allStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var session = new GatewaySubagents(async (_, token) =>
        {
            if (Interlocked.Increment(ref starts) == 3) allStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return "unexpected";
        }, parent.Token);
        var tools = session.Create();
        var ids = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            using var spawned = JsonDocument.Parse(await tools[0].Execute(Args(new { task = "task " + i }), CancellationToken.None));
            ids.Add(spawned.RootElement.GetProperty("agent_id").GetString()!);
        }
        await allStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.StartsWith("错误：", await tools[0].Execute(Args(new { task = "fourth" }), CancellationToken.None));
        parent.Cancel();
        foreach (var id in ids)
        {
            var result = await tools[1].Execute(Args(new { agent_id = id, wait_ms = 20000 }), CancellationToken.None);
            Assert.Contains("cancelled", result);
        }
        Assert.StartsWith("错误：", await tools[0].Execute(Args(new { task = "after cancellation" }), CancellationToken.None));
    }
}
