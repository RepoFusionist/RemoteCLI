using System.Text.Json;
using RemoteCli.Desktop;
using RemoteCli.Local;

namespace RemoteCli.UnitTests;

public sealed class GatewayReliabilityTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "remotecli-reliability-" + Guid.NewGuid().ToString("N"));
    public GatewayReliabilityTests() => Directory.CreateDirectory(root);

    [Theory]
    [InlineData("write_file", "{\"path\":\"a.txt\",\"content\":\"changed\"}")]
    [InlineData("edit_file", "{\"path\":\"a.txt\",\"old_text\":\"original\",\"new_text\":\"changed\"}")]
    [InlineData("append_file", "{\"path\":\"a.txt\",\"content\":\"changed\"}")]
    [InlineData("delete_file", "{\"path\":\"a.txt\"}")]
    public async Task AllFileMutationsRequireApprovalAndDenialLeavesBytesUnchanged(string name, string json)
    {
        File.WriteAllText(Path.Combine(root, "a.txt"), "original");
        var raw = LocalChatTools.Create(root, [name]).Single();
        var approvals = 0;
        var tool = GatewayApprovalPolicy.Protect(raw, (category, content, _) =>
        {
            Assert.Equal(GatewayApprovalCategory.WorkspaceWrite, category);
            Assert.Contains("a.txt", content); approvals++;
            return Task.FromResult(false);
        });
        using var args = JsonDocument.Parse(json);
        Assert.StartsWith("错误：", await tool.Execute(args.RootElement, CancellationToken.None));
        Assert.Equal(1, approvals);
        Assert.Equal("original", File.ReadAllText(Path.Combine(root, "a.txt")));
    }

    [Fact]
    public async Task CancellationAfterApprovalCannotWriteAndMediumApprovalCanWrite()
    {
        var raw = LocalChatTools.Create(root, ["write_file"]).Single();
        using var args = JsonDocument.Parse("""{"path":"a.txt","content":"approved"}""");
        using var cts = new CancellationTokenSource();
        var cancelled = GatewayApprovalPolicy.Protect(raw, (_, _, _) => { cts.Cancel(); return Task.FromResult(true); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.Execute(args.RootElement, cts.Token));
        Assert.False(File.Exists(Path.Combine(root, "a.txt")));
        var approved = GatewayApprovalPolicy.Protect(raw, (category, content, _) => Task.FromResult(
            GatewayApprovalPolicy.ShouldAutoApprove(category, "medium", false, content)));
        await approved.Execute(args.RootElement, CancellationToken.None);
        Assert.Equal("approved", File.ReadAllText(Path.Combine(root, "a.txt")));
    }

    [Fact]
    public async Task FetchDenialHappensBeforeAnyNetworkCall()
    {
        var tool = GatewayApprovalPolicy.Protect(LocalChatTools.Create(root, ["web_fetch"]).Single(),
            (category, _, _) => { Assert.Equal(GatewayApprovalCategory.Network, category); return Task.FromResult(false); });
        using var args = JsonDocument.Parse("""{"url":"https://example.invalid/never-contact"}""");
        Assert.Contains("拒绝", await tool.Execute(args.RootElement, CancellationToken.None));
    }

    [Theory]
    [InlineData("git.exe reset --hard HEAD")]
    [InlineData("# 终端模式：管道；最长运行：1000 毫秒\ngit.exe reset --hard HEAD")]
    [InlineData("git -C . reset --hard HEAD")]
    [InlineData("& 'C:\\Program Files\\Git\\cmd\\git.exe' reset --hard HEAD")]
    [InlineData("rm -r -force .\\out")]
    [InlineData("Remove-Item .\\out -Recurse:$true -Force")]
    [InlineData("ri .\\out -r -fo")]
    [InlineData("powershell -EncodedCommand YQ==")]
    [InlineData("cmd /c del /s /q out")]
    [InlineData("& ('git' + '.exe') reset --hard")]
    [InlineData("Invoke-Expression 'Remove-Item out -Recurse'")]
    [InlineData("[IO.Directory]::Delete('out',$true)")]
    [InlineData("git -c core.pager=evil log")]
    [InlineData("git diff --output=important.txt")]
    [InlineData("rg --pre evil query")]
    [InlineData("dotnet build")]
    [InlineData("echo x > important.txt")]
    [InlineData("$cmd = 'rm'; & $cmd -rf out")]
    public void ActualPowerShellParserRequiresApprovalForDestructionAndIndirectExecution(string command) =>
        Assert.True(GatewayApprovalPolicy.IsDestructiveCommand(command));

    [Theory]
    [InlineData("git.exe status --short")]
    [InlineData("# 终端模式：管道；最长运行：1000 毫秒\ngit.exe status --short")]
    [InlineData("git -C . diff")]
    [InlineData("Get-ChildItem -Force | Select-Object Name")]
    [InlineData("Get-Content 'with spaces.txt'")]
    [InlineData("dotnet --info")]
    public void ActualPowerShellParserAllowsKnownSimpleReadCommands(string command) =>
        Assert.False(GatewayApprovalPolicy.IsDestructiveCommand(command));

    [Fact]
    public void InteractiveInputCannotBypassCommandClassificationInFragments() =>
        Assert.False(GatewayApprovalPolicy.ShouldAutoApprove(GatewayApprovalCategory.CommandInput, "high", false, "git reset --"));

    [Fact]
    public void UntypedMcpPluginsCannotBypassHighTierCommandApproval() =>
        Assert.False(GatewayApprovalPolicy.ShouldAutoApprove(GatewayApprovalCategory.Mcp, "high", false, "arbitrary plugin command"));

    [Fact]
    public void HistorySurvivesCompressionNextTurnAndStoreRecreation()
    {
        var store = new GatewayHistoryStore(root);
        var history = new List<ChatTurn> { new() { Role = "user", Content = "original constraint" } };
        var first = store.Save("conversation", "workspace", history, 0, null);
        var compact = new List<ChatTurn> { new() { Role = "user", Content = "summary retains constraint" },
            new() { Role = "assistant", ToolCalls = [new("call", "read_file", "{}")] },
            new() { Role = "tool", Content = "result", ToolCallId = "call" } };
        var compressed = store.CommitCompression("conversation", "workspace", compact, 12, first, () => true);
        var restored = new GatewayHistoryStore(root).Load("conversation", "workspace", compressed);
        Assert.Equal(12, restored.IndexedMessages);
        Assert.Contains("constraint", restored.History[0].Content);
        Assert.Equal("call", restored.History[1].ToolCalls!.Single().Id);
        Assert.Equal("call", restored.History[2].ToolCallId);
        restored.History.Add(new ChatTurn { Content = "next turn" });
        var next = store.Save("conversation", "workspace", restored.History, 12, compressed);
        Assert.Equal(4, new GatewayHistoryStore(root).Load("conversation", "workspace", next).History.Count);
        Assert.Throws<IOException>(() => store.Save("conversation", "workspace", history, 0, first));
        Assert.Throws<IOException>(() => store.Load("conversation", "different workspace", next));
    }

    [Fact]
    public void IndexWriteFailureNeverCommitsCompactedHistory()
    {
        var store = new GatewayHistoryStore(root);
        var history = new List<ChatTurn> { new() { Content = "full original context" } };
        var revision = store.Save("c", "w", history, 0, null);
        var blockedRoot = Path.Combine(root, "not-a-directory"); File.WriteAllText(blockedRoot, "blocked");
        var index = new ContextIndexStore(blockedRoot);
        Assert.Throws<IOException>(() => store.CommitCompression("c", "w", [new() { Content = "summary" }], 1, revision,
            () => index.Append("c", "summary", [], 0, 1, 100)));
        Assert.Equal("full original context", store.Load("c", "w", revision).History.Single().Content);
    }

    [Fact]
    public async Task SummaryCoversEveryMessageAndTailWithoutTruncatingTranscript()
    {
        var prefix = new[] { new ChatTurn { Content = new string('x', 40_000) + "IMPORTANT_TAIL" },
            new ChatTurn { Role = "assistant", ToolCalls = [new("id", "write_file", "TOOL_ARGUMENT_TAIL")] } };
        var received = new List<string>();
        var summary = await GatewayHistorySummarizer.SummarizeAsync(prefix, 8192, (chunk, _) =>
        { received.Add(chunk); return Task.FromResult(new GatewaySummary("part", 10)); }, CancellationToken.None);
        Assert.Contains("IMPORTANT_TAIL", string.Concat(received));
        Assert.Contains("TOOL_ARGUMENT_TAIL", string.Concat(received));
        Assert.All(received, chunk => Assert.True(chunk.Length <= 3072));
        Assert.Equal(received.Count * 10, summary.TotalTokens);
        Assert.EndsWith("IMPORTANT_TAIL", prefix[0].Content);
    }

    [Fact]
    public async Task SummaryFailureIsObservableAndDoesNotBecomeAnExcerptSuccess()
    {
        var original = new ChatTurn { Content = "retain all of this" };
        await Assert.ThrowsAsync<HttpRequestException>(() => GatewayHistorySummarizer.SummarizeAsync([original], 8192,
            (_, _) => throw new HttpRequestException("model unavailable"), CancellationToken.None));
        Assert.Equal("retain all of this", original.Content);
        await Assert.ThrowsAsync<IOException>(() => GatewayHistorySummarizer.SummarizeAsync([original], 8192,
            (_, _) => Task.FromResult(new GatewaySummary("", null)), CancellationToken.None));
    }

    [Theory]
    [InlineData(8192, 8192)]
    [InlineData(128000, 128000)]
    [InlineData(1000000, 1000000)]
    public void CompressionUsesActualWindowAndReservesOutput(int configured, int expected)
    {
        Assert.Equal(expected, ContextCompression.ResolveBudget(configured));
        Assert.Equal(expected - 2048, ContextCompression.InputBudget(configured, 2048));
    }

    [Fact]
    public void RequestEstimateIncludesSystemToolsAndImages()
    {
        var text = new ChatTurn { Content = "hello" };
        var withImage = new ChatTurn { Content = "hello", Images = [new("image", "image/png", "AA==")] };
        var plain = ContextCompression.EstimateRequest([text], []);
        Assert.True(ContextCompression.EstimateRequest([text], [new string('s', 6000)]) > plain + 1000);
        Assert.True(ContextCompression.EstimateRequest([withImage], []) >= plain + 2048);
    }

    [Fact]
    public async Task MaximumAcceptedPlanExecutesWholeAndChangedPlanIsRejected()
    {
        var store = new PlanStore(root); GatewayPlan? shown = null;
        var markdown = new string('x', PlanStore.MaximumMarkdownCharacters - 20) + "FINAL_REQUIRED_STEP";
        await store.Create(plan => shown = plan).Execute(JsonSerializer.SerializeToElement(new { title = "Plan", markdown }), CancellationToken.None);
        Assert.NotNull(shown);
        Assert.True(store.TryLoad(shown.PlanId, PlanStore.MaximumPromptCharacters, out var full, out var error, shown.Hash), error);
        Assert.Contains("FINAL_REQUIRED_STEP", full);
        File.AppendAllText(shown.Path, "changed");
        Assert.False(store.TryLoad(shown.PlanId, PlanStore.MaximumPromptCharacters, out _, out _, shown.Hash));
    }

    [Fact]
    public void RemotePromptRetriesAfterTransientReadFailureWithoutCachingFailedMetadata()
    {
        var path = Path.Combine(root, "ui-state.json");
        File.WriteAllText(path, """{"state":{"settings":{"remoteSystemPrompt":"first"}}}""");
        var store = new RemotePromptStore(path);
        Assert.Equal("first", store.Read());
        File.WriteAllText(path, """{"state":{"settings":{"remoteSystemPrompt":"other"}}}""");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(3));
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.Equal("first", store.Read());
        Assert.Equal("other", store.Read());
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
