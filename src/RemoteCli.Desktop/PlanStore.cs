using System.Text;
using System.Security.Cryptography;
using System.Text.Json;

namespace RemoteCli.Desktop;

/// <summary>计划模式下由 present_plan 生成、并可由用户批准执行的本地计划。</summary>
internal sealed record GatewayPlan(string PlanId, string Title, string Markdown, string Path, string Hash);

/// <summary>
/// 计划保存在 %LOCALAPPDATA%\RemoteCLI\plans 下，不属于工作区，也不会授予任何工作区权限。
/// </summary>
internal sealed class PlanStore
{
    internal const int MaximumTitleCharacters = 200;
    internal const int MaximumMarkdownCharacters = 30_000;
    internal const int MaximumPromptCharacters = 32_000;

    private readonly string root;

    internal PlanStore(string? root = null)
    {
        this.root = string.IsNullOrWhiteSpace(root)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RemoteCLI", "plans")
            : Path.GetFullPath(root);
    }

    internal static bool TryParsePlanId(string? value, out Guid planId) =>
        Guid.TryParse(value?.Trim(), out planId) && planId != Guid.Empty;

    internal string Save(string title, string markdown)
    {
        var planId = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(root);
        var builder = new StringBuilder();
        builder.Append("# ").Append(title.Trim()).AppendLine().AppendLine().Append(markdown.TrimEnd()).AppendLine();
        WriteAtomic(Path.Combine(root, planId + ".md"), builder.ToString());
        return planId;
    }

    internal bool TryLoad(string? planIdValue, int maxCharacters, out string content, out string error, string? expectedHash = null)
    {
        content = string.Empty;
        error = string.Empty;
        if (!TryParsePlanId(planIdValue, out var planId))
        {
            error = "错误：计划编号无效。";
            return false;
        }

        var path = Path.Combine(root, planId.ToString("N") + ".md");
        if (!File.Exists(path))
        {
            error = "错误：未找到计划 " + planId.ToString("N") + "，它可能已被删除。请重新生成计划。";
            return false;
        }

        try
        {
            var text = File.ReadAllText(path, Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(text))
            {
                error = "错误：计划文件为空。";
                return false;
            }

            if (text.Length > maxCharacters)
            {
                error = "错误：计划超过执行预算，请缩短计划并重新批准；没有执行截断内容。";
                return false;
            }
            if (expectedHash is not null && !string.Equals(Hash(text), expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                error = "错误：计划正文已改变或批准记录无效，请重新生成并批准计划。";
                return false;
            }
            content = text;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            error = "错误：无法读取计划文件：" + exception.Message;
            return false;
        }
    }

    internal LocalChatTool Create(Action<GatewayPlan>? onPresented = null) => new(
        "present_plan",
        "把实施计划提交给用户审阅并决定是否执行。桌面端会把计划保存为本地 Markdown 并显示成可滚动的计划卡片，附带执行/取消操作。计划模式下必须用本工具提交计划，不要只回复文字。",
        """{"type":"object","properties":{"title":{"type":"string","description":"计划的简短标题"},"markdown":{"type":"string","description":"完整计划正文，使用 Markdown，包含步骤、涉及文件和验证方式"}},"required":["title","markdown"],"additionalProperties":false}""",
        (arguments, token) => PresentAsync(arguments, token, onPresented));

    private Task<string> PresentAsync(JsonElement arguments, CancellationToken token, Action<GatewayPlan>? onPresented)
    {
        token.ThrowIfCancellationRequested();
        if (arguments.ValueKind != JsonValueKind.Object ||
            !arguments.TryGetProperty("title", out var titleElement) || titleElement.ValueKind != JsonValueKind.String ||
            !arguments.TryGetProperty("markdown", out var markdownElement) || markdownElement.ValueKind != JsonValueKind.String)
            return Task.FromResult("错误：present_plan 需要标题和 Markdown 正文。");

        var title = titleElement.GetString()?.Trim() ?? string.Empty;
        var markdown = markdownElement.GetString()?.Trim() ?? string.Empty;
        if (title.Length is 0 or > MaximumTitleCharacters) return Task.FromResult("错误：计划标题必须为 1 至 200 个字符。");
        if (markdown.Length is 0 or > MaximumMarkdownCharacters) return Task.FromResult("错误：计划正文必须为 1 至 30000 个字符。");

        string planId;
        try
        {
            planId = Save(title, markdown);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return Task.FromResult("错误：无法保存计划：" + exception.Message);
        }

        var path = Path.Combine(root, planId + ".md");
        onPresented?.Invoke(new GatewayPlan(planId, title, markdown, path, Hash(File.ReadAllText(path, Encoding.UTF8))));
        return Task.FromResult("计划已保存并展示给用户（planId: " + planId + "）。请用一两句话说明计划要点，然后等待用户选择执行或取消；不要开始实施，也不要重复粘贴整份计划。");
    }

    internal static string Hash(string content) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    private static void WriteAtomic(string path, string content)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, content, new UTF8Encoding(false));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }
}
