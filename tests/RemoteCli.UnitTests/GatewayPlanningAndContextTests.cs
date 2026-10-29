using System.Text.Json;
using System.Text.Json.Nodes;
using RemoteCli.Desktop;

namespace RemoteCli.UnitTests;

/// <summary>计划、上下文压缩、上下文索引、审批策略、系统提示与模型参数读取的行为约束。</summary>
public sealed class GatewayPlanningAndContextTests
{
    private static JsonElement Arguments(object value) => JsonSerializer.SerializeToElement(value);

    private static JsonElement Json(string raw)
    {
        using var document = JsonDocument.Parse(raw);
        return document.RootElement.Clone();
    }

    private sealed class TempRoot : IDisposable
    {
        internal TempRoot(string prefix)
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), prefix + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        internal string Path { get; }

        public void Dispose()
        {
            try { if (Directory.Exists(Path)) Directory.Delete(Path, true); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }

    // ---------------------------------------------------------------- PlanStore

    [Fact]
    public void SaveReturnsThirtyTwoCharacterHexIdAndTryLoadRoundTripsTitleAndBody()
    {
        using var temp = new TempRoot("longshell-plan");
        var store = new PlanStore(temp.Path);

        var planId = store.Save("重构检索层", "## 步骤\n1. 读文件\n2. 写测试");

        Assert.Equal(32, planId.Length);
        Assert.All(planId, ch => Assert.True(ch is >= '0' and <= '9' or >= 'a' and <= 'f', "非十六进制字符：" + ch));
        Assert.True(PlanStore.TryParsePlanId(planId, out var parsed));
        Assert.NotEqual(Guid.Empty, parsed);

        Assert.True(store.TryLoad(planId, 100_000, out var content, out var error), error);
        Assert.Equal(string.Empty, error);
        Assert.Contains("# 重构检索层", content);
        Assert.Contains("## 步骤", content);
        Assert.Contains("2. 写测试", content);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("../secrets")]
    [InlineData("../../Windows/win.ini")]
    public void TryLoadRejectsInvalidPlanIdsWithoutEscapingRoot(string? planId)
    {
        using var temp = new TempRoot("longshell-plan");
        var store = new PlanStore(temp.Path);

        Assert.False(store.TryLoad(planId, 10_000, out var content, out var error));
        Assert.Equal(string.Empty, content);
        Assert.Contains("计划编号无效", error);
        Assert.Empty(Directory.GetFiles(temp.Path));
    }

    [Fact]
    public void TryLoadReportsMissingFile()
    {
        using var temp = new TempRoot("longshell-plan");
        var store = new PlanStore(temp.Path);
        var planId = Guid.NewGuid().ToString("N");

        Assert.False(store.TryLoad(planId, 10_000, out var content, out var error));
        Assert.Equal(string.Empty, content);
        Assert.Contains("未找到计划", error);
        Assert.Contains(planId, error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \r\n\t ")]
    public void TryLoadRejectsEmptyFile(string text)
    {
        using var temp = new TempRoot("longshell-plan");
        var store = new PlanStore(temp.Path);
        var planId = Guid.NewGuid().ToString("N");
        File.WriteAllText(Path.Combine(temp.Path, planId + ".md"), text);

        Assert.False(store.TryLoad(planId, 10_000, out var content, out var error));
        Assert.Equal(string.Empty, content);
        Assert.Contains("计划文件为空", error);
    }

    [Fact]
    public void TryLoadRejectsOverBudgetPlansWithoutExecutingAPrefix()
    {
        using var temp = new TempRoot("longshell-plan");
        var store = new PlanStore(temp.Path);
        var planId = store.Save("长计划", new string('x', 5_000));

        Assert.False(store.TryLoad(planId, 200, out var content, out var error));
        Assert.Empty(content);
        Assert.Contains("超过执行预算", error);
    }

    [Fact]
    public async Task PlanToolRejectsOutOfRangeTitleAndMarkdown()
    {
        using var temp = new TempRoot("longshell-plan");
        var tool = new PlanStore(temp.Path).Create();

        Assert.StartsWith("错误：计划标题", await tool.Execute(Arguments(new { title = string.Empty, markdown = "正文" }), CancellationToken.None));
        Assert.StartsWith("错误：计划标题", await tool.Execute(Arguments(new { title = new string('标', PlanStore.MaximumTitleCharacters + 1), markdown = "正文" }), CancellationToken.None));
        Assert.StartsWith("错误：计划正文", await tool.Execute(Arguments(new { title = "标题", markdown = string.Empty }), CancellationToken.None));
        Assert.StartsWith("错误：计划正文", await tool.Execute(Arguments(new { title = "标题", markdown = new string('文', PlanStore.MaximumMarkdownCharacters + 1) }), CancellationToken.None));
        Assert.StartsWith("错误：present_plan", await tool.Execute(Json("""{"title":"标题"}"""), CancellationToken.None));
        Assert.Empty(Directory.GetFiles(temp.Path));
    }

    [Fact]
    public async Task PlanToolSavesPlanInsideRootAndInvokesCallback()
    {
        using var temp = new TempRoot("longshell-plan");
        GatewayPlan? presented = null;
        var tool = new PlanStore(temp.Path).Create(plan => presented = plan);

        var message = await tool.Execute(Arguments(new { title = "  计划标题  ", markdown = "  第一步  " }), CancellationToken.None);

        Assert.Contains("计划已保存并展示给用户", message);
        Assert.NotNull(presented);
        Assert.Equal("计划标题", presented!.Title);
        Assert.Equal("第一步", presented.Markdown);
        Assert.Equal(32, presented.PlanId.Length);
        Assert.Equal(Path.GetFullPath(temp.Path), Path.GetDirectoryName(presented.Path));
        Assert.Single(Directory.GetFiles(temp.Path, "*.md"));
        Assert.Contains(presented.PlanId, message);
    }

    // ---------------------------------------------------------- ContextCompression

    [Fact]
    public void ResolveBudgetClampsToMinimumAndMaximum()
    {
        Assert.Equal(ContextCompression.DefaultContextWindow, ContextCompression.ResolveBudget(null));
        Assert.Equal(ContextCompression.MinimumContextWindow, ContextCompression.ResolveBudget(1_000));
        Assert.Equal(ContextCompression.MinimumContextWindow, ContextCompression.ResolveBudget(ContextCompression.MinimumContextWindow));
        Assert.Equal(64_000, ContextCompression.ResolveBudget(64_000));
        Assert.Equal(1_000_000, ContextCompression.ResolveBudget(1_000_000));
        Assert.Equal(ContextCompression.MaximumContextWindow, ContextCompression.ResolveBudget(3_000_000));
    }

    [Theory]
    [InlineData(7, 100_000_000, false)]
    [InlineData(8, 83_200, false)]
    [InlineData(8, 83_201, true)]
    public void ShouldCompressRequiresBothEnoughMessagesAndEnoughTokens(int messageCount, long tokens, bool expected) =>
        Assert.Equal(expected, ContextCompression.ShouldCompress(tokens, null, messageCount));

    [Fact]
    public void ShouldCompressUsesClampedBudgetForSmallContextWindow()
    {
        var threshold = (long)(ContextCompression.MinimumContextWindow * ContextCompression.TriggerRatio);

        Assert.False(ContextCompression.ShouldCompress(threshold, 4_096, 10));
        Assert.True(ContextCompression.ShouldCompress(threshold + 1, 4_096, 10));
    }

    [Fact]
    public void SelectPrefixCountSkipsCutThatWouldSplitToolCalls()
    {
        var messages = new List<ContextMessage>
        {
            new("user", false, null),               // 0
            new("assistant", true, null),           // 1 工具调用
            new("tool", false, "call_1"),           // 2 工具结果
            new("assistant", false, null),          // 3
            new("user", false, null),               // 4 安全切点
            new("assistant", true, null),           // 5 工具调用（不可切）
            new("tool", false, "call_2"),           // 6 工具结果
            new("assistant", false, null),          // 7
            new("user", false, null),               // 8
            new("assistant", false, null),          // 9
            new("user", false, null)                // 10
        };

        var cut = ContextCompression.SelectPrefixCount(messages);

        Assert.Equal(4, cut);
        Assert.Equal("user", messages[cut].Role);
        Assert.Null(messages[cut].ToolCallId);
        Assert.False(messages[cut].HasToolCalls);
        Assert.False(messages[cut - 1].HasToolCalls);
    }

    [Fact]
    public void SelectPrefixCountKeepsRecentMessagesAndRejectsTooShortHistory()
    {
        var messages = new List<ContextMessage>();
        for (var i = 0; i < 10; i++) messages.Add(new("user", false, null));

        var cut = ContextCompression.SelectPrefixCount(messages);

        Assert.Equal(4, cut);
        Assert.Equal(ContextCompression.KeepRecentMessages, messages.Count - cut);

        var tooShort = messages.Take(ContextCompression.MinimumMessages - 1).ToArray();
        Assert.Equal(0, ContextCompression.SelectPrefixCount(tooShort));
    }

    [Fact]
    public void SelectPrefixCountCanCutBetweenCompletedToolGroups()
    {
        // 前缀全是工具往返消息，唯一可切的位置都被 KeepRecentMessages 排除。
        var messages = new List<ContextMessage>
        {
            new("assistant", true, null),
            new("tool", false, "call_1"),
            new("assistant", true, null),
            new("tool", false, "call_2"),
            new("assistant", true, null),
            new("tool", false, "call_3"),
            new("assistant", true, null),
            new("tool", false, "call_4"),
            new("assistant", true, null),
            new("tool", false, "call_5")
        };

        Assert.Equal(4, ContextCompression.SelectPrefixCount(messages));
    }

    [Fact]
    public void EstimateTokensIsZeroForEmptyInputAndMonotonicPerScript()
    {
        Assert.Equal(0, ContextCompression.EstimateTokens([]));
        Assert.Equal(0, ContextCompression.EstimateTokens([null, string.Empty]));

        var shortAscii = ContextCompression.EstimateTokens(["hello"]);
        var longAscii = ContextCompression.EstimateTokens(["hello world hello world"]);
        var shortCjk = ContextCompression.EstimateTokens(["你好"]);
        var longCjk = ContextCompression.EstimateTokens(["你好世界你好世界"]);

        Assert.True(shortAscii > 0);
        Assert.True(longAscii > shortAscii);
        Assert.True(shortCjk > 0);
        Assert.True(longCjk > shortCjk);
        Assert.True(ContextCompression.EstimateTokens(["a"]) >= 1);
    }

    // -------------------------------------------------------- ContextIndexStore

    [Fact]
    public void AppendSearchAndReadRoundTrip()
    {
        using var temp = new TempRoot("longshell-index");
        var store = new ContextIndexStore(temp.Path);

        Assert.True(store.Append("conv-1", "实现了登录页面并接入鉴权", ["src/Login.cs"], 0, 12, 4321));
        Assert.True(store.Append("conv-1", "修复了导出报表的编码问题", ["src/Export.cs"], 12, 20, 999));

        var entries = store.Read("conv-1");
        Assert.Equal(2, entries.Count);
        Assert.Equal("实现了登录页面并接入鉴权", entries[0].Summary);
        Assert.Equal(new[] { "src/Login.cs" }, entries[0].Changes);
        Assert.Equal(0, entries[0].StartMessage);
        Assert.Equal(12, entries[0].EndMessage);
        Assert.Equal(4321L, entries[0].Tokens);
        Assert.Equal(32, entries[0].Id.Length);

        var hits = store.Search("conv-1", "登录", 3);
        Assert.Single(hits);
        Assert.Equal("实现了登录页面并接入鉴权", hits[0].Summary);
    }

    [Fact]
    public void SearchUsesCjkBigramsAndFallsBackToRecentEntries()
    {
        using var temp = new TempRoot("longshell-index");
        var store = new ContextIndexStore(temp.Path);
        store.Append("conv-cjk", "重构了配置读取", [], 0, 1, 10);
        store.Append("conv-cjk", "修复了登录超时", [], 1, 2, 10);

        Assert.Single(store.Search("conv-cjk", "超时", 5));
        Assert.Single(store.Search("conv-cjk", "登录超", 5));
        Assert.Equal(new[] { "修复了登录超时", "重构了配置读取" }, store.Search("conv-cjk", "完全无关的词语", 5).Select(entry => entry.Summary));
    }

    [Fact]
    public void EmptyQueryReturnsMostRecentEntriesFirst()
    {
        using var temp = new TempRoot("longshell-index");
        var store = new ContextIndexStore(temp.Path);
        for (var i = 0; i < 5; i++) store.Append("conv-recent", "摘要 " + i, [], i, i + 1, i);

        var entries = store.Search("conv-recent", "", 2);

        Assert.Equal(2, entries.Count);
        Assert.Equal("摘要 4", entries[0].Summary);
        Assert.Equal("摘要 3", entries[1].Summary);
        Assert.Equal("摘要 4", store.Search("conv-recent", "   ", 1)[0].Summary);
    }

    [Fact]
    public void AppendTruncatesOldestEntriesAtMaximum()
    {
        using var temp = new TempRoot("longshell-index");
        var store = new ContextIndexStore(temp.Path);
        var total = ContextIndexStore.MaximumEntries + 2;

        for (var i = 0; i < total; i++) Assert.True(store.Append("conv-max", "摘要 " + i, [], i, i + 1, i));

        var entries = store.Read("conv-max");
        Assert.Equal(ContextIndexStore.MaximumEntries, entries.Count);
        Assert.Equal("摘要 2", entries[0].Summary);
        Assert.Equal("摘要 " + (total - 1), entries[^1].Summary);
        Assert.DoesNotContain(entries, entry => entry.Summary is "摘要 0" or "摘要 1");
    }

    [Fact]
    public void CorruptIndexFileReturnsEmptyInsteadOfThrowing()
    {
        using var temp = new TempRoot("longshell-index");
        var store = new ContextIndexStore(temp.Path);
        File.WriteAllText(store.PathFor("conv-broken"), "{ this is not json");

        Assert.Empty(store.Read("conv-broken"));
        Assert.Empty(store.Search("conv-broken", "登录", 3));
        Assert.Equal("当前对话还没有上下文摘要记录；只有较早的对话被压缩后才会生成摘要。", store.Execute(Arguments(new { query = "登录" }), "conv-broken", CancellationToken.None));

        Assert.True(store.Append("conv-broken", "重新写入的摘要", [], 0, 1, 5));
        Assert.Single(store.Read("conv-broken"));
    }

    [Fact]
    public async Task IndexEntriesWithNullChangesAreIgnoredInsteadOfThrowing()
    {
        using var temp = new TempRoot("longshell-index");
        var store = new ContextIndexStore(temp.Path);
        File.WriteAllText(store.PathFor("conv-null"), """
            [{"id":"abc","at":"2026-01-01T00:00:00+00:00","summary":"旧摘要","changes":null,"startMessage":0,"endMessage":1,"tokens":10}]
            """);
        var tool = store.Create("conv-null");

        Assert.Empty(store.Read("conv-null"));
        Assert.Empty(store.Search("conv-null", "旧摘要", 3));
        Assert.Equal("当前对话还没有上下文摘要记录；只有较早的对话被压缩后才会生成摘要。", await tool.Execute(Arguments(new { query = "" }), CancellationToken.None));
    }

    [Fact]
    public void ReadAndAppendRejectEmptyInput()
    {
        using var temp = new TempRoot("longshell-index");
        var store = new ContextIndexStore(temp.Path);

        Assert.Empty(store.Read(""));
        Assert.Empty(store.Search("   ", "", 3));
        Assert.False(store.Append("", "摘要", [], 0, 1, 1));
        Assert.False(store.Append("conv-empty", "   ", [], 0, 1, 1));
        Assert.Empty(store.Read("conv-empty"));
    }

    [Fact]
    public void ExtractChangesCollectsToolNamesPatchPathsAndDeduplicates()
    {
        var patch = "*** Begin Patch\n*** Add File: src/One.cs\n+class One {}\n*** Update File: src/Two.cs\n*** End Patch";
        var changes = ContextIndexStore.ExtractChanges(
        [
            ("apply_patch", JsonSerializer.Serialize(new { patch })),
            ("apply_patch", JsonSerializer.Serialize(new { patch })),
            ("write_file", JsonSerializer.Serialize(new { path = "docs/readme.md" })),
            ("write_file", JsonSerializer.Serialize(new { path = "docs/readme.md" })),
            ("write_file", "not json")
        ]);

        Assert.Equal(new[] { "apply_patch", "src/One.cs", "src/Two.cs", "write_file", "docs/readme.md" }, changes);
    }

    [Fact]
    public void ExtractChangesStopsAtMaximum()
    {
        var calls = Enumerable.Range(0, ContextIndexStore.MaximumChanges + 10)
            .Select(i => ("write_file", JsonSerializer.Serialize(new { path = "f" + i + ".txt" })))
            .ToArray();

        var changes = ContextIndexStore.ExtractChanges(calls);

        Assert.Equal(ContextIndexStore.MaximumChanges, changes.Count);
        Assert.Equal("write_file", changes[0]);
        Assert.Equal("f18.txt", changes[^1]);
    }

    [Fact]
    public async Task SearchContextToolSchemaAndArgumentValidation()
    {
        using var temp = new TempRoot("longshell-index");
        var store = new ContextIndexStore(temp.Path);
        var tool = store.Create("conv-tool");

        Assert.Equal("search_context", tool.Name);
        Assert.Contains("\"required\":[\"query\"]", tool.SchemaJson);
        Assert.Contains("\"limit\"", tool.SchemaJson);

        Assert.Equal("当前对话还没有上下文摘要记录；只有较早的对话被压缩后才会生成摘要。", await tool.Execute(Arguments(new { query = "" }), CancellationToken.None));
        Assert.Contains("limit 必须为 1 至 5", await tool.Execute(Arguments(new { query = "", limit = 0 }), CancellationToken.None));
        Assert.Contains("limit 必须为 1 至 5", await tool.Execute(Arguments(new { query = "", limit = 6 }), CancellationToken.None));
        Assert.Contains("limit 必须为 1 至 5", await tool.Execute(Arguments(new { query = "", limit = 2.5 }), CancellationToken.None));
        Assert.Contains("检索关键词超过 1000 个字符", await tool.Execute(Arguments(new { query = new string('字', ContextIndexStore.MaximumQueryCharacters + 1) }), CancellationToken.None));
    }

    [Fact]
    public async Task SearchContextToolDefaultsToThreeMostRecentEntries()
    {
        using var temp = new TempRoot("longshell-index");
        var store = new ContextIndexStore(temp.Path);
        for (var i = 0; i < 5; i++) store.Append("conv-default", "摘要" + i, ["file" + i + ".cs"], i, i + 1, i);
        var tool = store.Create("conv-default");

        var output = await tool.Execute(Json("""{"query":""}"""), CancellationToken.None);

        Assert.Contains("上下文摘要索引", output);
        Assert.Contains("摘要4", output);
        Assert.Contains("摘要3", output);
        Assert.Contains("摘要2", output);
        Assert.DoesNotContain("摘要1", output);
        Assert.DoesNotContain("摘要0", output);
        Assert.Contains("file4.cs", output);
        Assert.Equal(ContextIndexStore.DefaultLimit, output.Split("涉及：").Length - 1);

        var single = await tool.Execute(Json("""{"query":"摘要4","limit":1}"""), CancellationToken.None);
        Assert.Contains("摘要4", single);
        Assert.Equal(1, single.Split("涉及：").Length - 1);
    }

    // ------------------------------------------------------ GatewayApprovalPolicy

    // 枚举是 internal 的，不能出现在 public 测试方法签名里，这里用名字在体内转换。
    private static GatewayApprovalCategory Category(string name) => Enum.Parse<GatewayApprovalCategory>(name);

    [Theory]
    [InlineData("WorkspaceWrite")]
    [InlineData("Command")]
    [InlineData("CommandInput")]
    [InlineData("Network")]
    [InlineData("Mcp")]
    public void LowTierNeverAutoApproves(string category) =>
        Assert.False(GatewayApprovalPolicy.ShouldAutoApprove(Category(category), GatewayApprovalPolicy.Low, false, "git status"));

    [Theory]
    [InlineData("WorkspaceWrite", true)]
    [InlineData("Network", true)]
    [InlineData("Command", false)]
    [InlineData("CommandInput", false)]
    [InlineData("Mcp", false)]
    public void MediumTierOnlyApprovesWorkspaceWritesAndNetwork(string category, bool expected) =>
        Assert.Equal(expected, GatewayApprovalPolicy.ShouldAutoApprove(Category(category), GatewayApprovalPolicy.Medium, false, "git status"));

    [Theory]
    [InlineData("git status", true)]
    [InlineData("dotnet build tests/RemoteCli.UnitTests", false)]
    [InlineData("ls -la", true)]
    [InlineData("", true)]
    [InlineData("rm -rf /", false)]
    [InlineData("rm -fr build", false)]
    [InlineData("sudo rm -r -f /var/tmp", false)]
    [InlineData("git reset --hard HEAD~3", false)]
    [InlineData("git clean -fdx", false)]
    [InlineData("format c:", false)]
    [InlineData("diskpart", false)]
    [InlineData("shutdown /s /t 0", false)]
    [InlineData("Remove-Item -Recurse -Force ./out", false)]
    [InlineData("reg delete HKLM\\Software\\Foo /f", false)]
    [InlineData("del /q *.log", false)]
    [InlineData("rd /s /q build", false)]
    public void HighTierApprovesOrdinaryCommandsButRejectsDestructiveOnes(string command, bool expected)
    {
        Assert.Equal(!expected, GatewayApprovalPolicy.IsDestructiveCommand(command));
        Assert.Equal(expected, GatewayApprovalPolicy.ShouldAutoApprove(GatewayApprovalCategory.Command, GatewayApprovalPolicy.High, false, command));
        Assert.False(GatewayApprovalPolicy.ShouldAutoApprove(GatewayApprovalCategory.CommandInput, GatewayApprovalPolicy.High, false, command));
    }

    [Theory]
    [InlineData("WorkspaceWrite")]
    [InlineData("Command")]
    [InlineData("Network")]
    [InlineData("Mcp")]
    public void ReadOnlyModeBlocksEveryCategory(string category)
    {
        var parsed = Category(category);
        Assert.False(GatewayApprovalPolicy.ShouldAutoApprove(parsed, GatewayApprovalPolicy.High, true, "git status"));
        Assert.False(GatewayApprovalPolicy.ShouldAutoApprove(parsed, GatewayApprovalPolicy.Medium, true, "git status"));
        Assert.False(GatewayApprovalPolicy.ShouldAutoApprove(parsed, GatewayApprovalPolicy.Low, true, "git status"));
    }

    [Theory]
    [InlineData(null, GatewayApprovalPolicy.Medium)]
    [InlineData("", GatewayApprovalPolicy.Medium)]
    [InlineData("  ", GatewayApprovalPolicy.Medium)]
    [InlineData("LOW", GatewayApprovalPolicy.Low)]
    [InlineData(" low ", GatewayApprovalPolicy.Low)]
    [InlineData("HIGH", GatewayApprovalPolicy.High)]
    [InlineData("High", GatewayApprovalPolicy.High)]
    [InlineData("medium", GatewayApprovalPolicy.Medium)]
    [InlineData("unlimited", GatewayApprovalPolicy.Medium)]
    [InlineData("auto", GatewayApprovalPolicy.Medium)]
    public void NormalizeTierFallsBackToMedium(string? tier, string expected) =>
        Assert.Equal(expected, GatewayApprovalPolicy.NormalizeTier(tier));

    // -------------------------------------------------------- GatewaySystemPrompt

    private static GatewayEnvironment Environment(string? workspaceRoot = @"C:\work\demo", params string[] tools) =>
        new("Microsoft Windows 11", "DEV-PC", "tester", "2026-01-02 03:04:05 +08:00", workspaceRoot, "chat", tools);

    [Fact]
    public void BuildIncludesOperatingSystemWorkspaceAndTools()
    {
        var prompt = GatewaySystemPrompt.Build(null, Environment(@"C:\work\demo", "read_file", "write_file"));

        Assert.Contains("Remote CLI", prompt);
        Assert.Contains("Microsoft Windows 11", prompt);
        Assert.Contains("DEV-PC", prompt);
        Assert.Contains("tester", prompt);
        Assert.Contains(@"C:\work\demo", prompt);
        Assert.Contains("read_file、write_file", prompt);
        Assert.DoesNotContain("[用户附加的系统提示", prompt);
    }

    [Fact]
    public void BuildExplainsMissingWorkspaceAndEmptyToolList()
    {
        var prompt = GatewaySystemPrompt.Build(null, Environment(null));

        Assert.Contains("（未选择工作区，文件工具不可用）", prompt);
        Assert.Contains("（无）", prompt);
    }

    [Fact]
    public void BuildTruncatesUserPromptAndKeepsBoundaryMarkers()
    {
        var longPrompt = "前置内容" + new string('a', GatewaySystemPrompt.MaximumUserPromptCharacters + 500);

        var prompt = GatewaySystemPrompt.Build("  " + longPrompt + "  ", Environment());

        Assert.Contains("[用户附加的系统提示", prompt);
        Assert.EndsWith("[/用户附加的系统提示]", prompt);
        Assert.Contains(new string('a', GatewaySystemPrompt.MaximumUserPromptCharacters - "前置内容".Length), prompt);
        Assert.DoesNotContain(new string('a', GatewaySystemPrompt.MaximumUserPromptCharacters), prompt);
        Assert.True(prompt.IndexOf("[用户附加的系统提示", StringComparison.Ordinal) < prompt.IndexOf("前置内容", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildPlacesEnvironmentBeforeUserPrompt()
    {
        var prompt = GatewaySystemPrompt.Build("请始终用中文回答。", Environment());

        var environmentIndex = prompt.IndexOf("当前环境", StringComparison.Ordinal);
        var userIndex = prompt.IndexOf("[用户附加的系统提示", StringComparison.Ordinal);

        Assert.True(environmentIndex >= 0);
        Assert.True(userIndex > environmentIndex);
        Assert.Contains("请始终用中文回答。", prompt);
    }

    [Fact]
    public void BuildIgnoresWhitespaceOnlyUserPrompt()
    {
        var prompt = GatewaySystemPrompt.Build("   \r\n  ", Environment());

        Assert.DoesNotContain("[用户附加的系统提示", prompt);
        Assert.EndsWith("使用用户使用的语言回答。", prompt);
    }

    [Fact]
    public void EnvironmentDropsBlankAndDuplicatedToolNames()
    {
        var environment = GatewayEnvironment.Create(@"C:\work", "plan", ["read_file", "read_file", " ", "", "write_file"]);

        Assert.Equal(new[] { "read_file", "write_file" }, environment.ToolNames);
        Assert.Equal("plan", environment.ComposerMode);
        Assert.Equal(@"C:\work", environment.WorkspaceRoot);
        Assert.Null(GatewayEnvironment.Create("  ", "chat", []).WorkspaceRoot);
        Assert.False(string.IsNullOrWhiteSpace(environment.OperatingSystem));
    }

    // ------------------------------------------------------- GatewayModelOptions

    [Fact]
    public void ReadReturnsNullsWhenModelOptionsMissingOrNotAnObject()
    {
        Assert.Equal(new GatewayModelOptions(null, null, null), GatewayModelOptions.Read(Json("""{"model":"x"}""")));
        Assert.Equal(new GatewayModelOptions(null, null, null), GatewayModelOptions.Read(Json("""{"modelOptions":"fast"}""")));
        Assert.Equal(new GatewayModelOptions(null, null, null), GatewayModelOptions.Read(Json("""{"modelOptions":{}}""")));
    }

    [Theory]
    [InlineData("0", 0d)]
    [InlineData("0.7", 0.7d)]
    [InlineData("2", 2d)]
    public void ReadAcceptsTemperatureWithinRange(string raw, double expected) =>
        Assert.Equal(expected, GatewayModelOptions.Read(Json("{\"modelOptions\":{\"temperature\":" + raw + "}}")).Temperature);

    [Theory]
    [InlineData("-0.1")]
    [InlineData("2.01")]
    [InlineData("100")]
    public void ReadRejectsOutOfRangeTemperature(string raw) =>
        Assert.Null(GatewayModelOptions.Read(Json("{\"modelOptions\":{\"temperature\":" + raw + "}}")).Temperature);

    [Theory]
    [InlineData("0", 0d)]
    [InlineData("1", 1d)]
    public void ReadAcceptsTopPWithinRange(string raw, double expected) =>
        Assert.Equal(expected, GatewayModelOptions.Read(Json("{\"modelOptions\":{\"topP\":" + raw + "}}")).TopP);

    [Theory]
    [InlineData("-0.5")]
    [InlineData("1.0001")]
    public void ReadRejectsOutOfRangeTopP(string raw) =>
        Assert.Null(GatewayModelOptions.Read(Json("{\"modelOptions\":{\"topP\":" + raw + "}}")).TopP);

    [Theory]
    [InlineData("1", 1)]
    [InlineData("1000000", 1_000_000)]
    public void ReadAcceptsMaxOutputTokensWithinRange(string raw, int expected) =>
        Assert.Equal(expected, GatewayModelOptions.Read(Json("{\"modelOptions\":{\"maxOutputTokens\":" + raw + "}}")).MaxOutputTokens);

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("1000001")]
    [InlineData("12.5")]
    [InlineData("\"4096\"")]
    public void ReadRejectsInvalidMaxOutputTokens(string raw) =>
        Assert.Null(GatewayModelOptions.Read(Json("{\"modelOptions\":{\"maxOutputTokens\":" + raw + "}}")).MaxOutputTokens);

    [Theory]
    [InlineData("8192", 8192)]
    [InlineData("128000", 128_000)]
    [InlineData("2000000", 2_000_000)]
    public void ReadAcceptsContextWindowWithinRange(string raw, int expected) =>
        Assert.Equal(expected, GatewayModelOptions.Read(Json("{\"modelOptions\":{\"contextWindow\":" + raw + "}}")).ContextWindow);

    [Theory]
    [InlineData("8191")]
    [InlineData("2000001")]
    [InlineData("0")]
    public void ReadRejectsOutOfRangeContextWindow(string raw) =>
        Assert.Null(GatewayModelOptions.Read(Json("{\"modelOptions\":{\"contextWindow\":" + raw + "}}")).ContextWindow);

    [Fact]
    public void ApplyWritesOnlyProvidedFields()
    {
        var body = new JsonObject { ["model"] = "deepseek", ["temperature"] = 0.3 };
        var options = GatewayModelOptions.Read(Json("""{"modelOptions":{"topP":0.9,"maxOutputTokens":512}}"""));

        options.Apply(body);

        Assert.Equal(0.3, body["temperature"]!.GetValue<double>());
        Assert.Equal(0.9, body["top_p"]!.GetValue<double>());
        Assert.Equal(512, body["max_tokens"]!.GetValue<int>());
        Assert.Equal("deepseek", body["model"]!.GetValue<string>());
        Assert.False(body.ContainsKey("contextWindow"));

        var untouched = new JsonObject { ["temperature"] = 0.3 };
        new GatewayModelOptions(null, null, null).Apply(untouched);
        Assert.Single(untouched);
        Assert.Equal(0.3, untouched["temperature"]!.GetValue<double>());
    }
}
