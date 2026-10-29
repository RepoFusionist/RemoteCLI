namespace RemoteCli.Desktop;

/// <summary>
/// Shared policy for the terminal composer modes. The UI supplies the mode for
/// usability, but the desktop process remains the authority for tool exposure.
/// </summary>
internal static class ComposerModePolicy
{
    internal const string Default = "default";
    internal const string Goal = "goal";
    internal const string Plan = "plan";
    internal const string Qa = "qa";
    internal const string GoalCompletionMarker = "[[REMOTECLI_GOAL_COMPLETE]]";

    internal static readonly IReadOnlySet<string> ReadOnlyTools = new HashSet<string>(StringComparer.Ordinal)
    {
        "list_files",
        "read_file",
        "search_text",
        "glob",
        "grep",
        "web_search",
        "web_fetch",
        "exa_search",
        "present",
        "present_plan",
        "search_context"
    };

    internal static string Normalize(string? mode, bool legacyPlanMode, string? target)
    {
        if (string.Equals(mode, Qa, StringComparison.OrdinalIgnoreCase)) return Qa;
        if (string.Equals(mode, Plan, StringComparison.OrdinalIgnoreCase) || legacyPlanMode) return Plan;
        if (string.Equals(mode, Goal, StringComparison.OrdinalIgnoreCase) || !string.IsNullOrWhiteSpace(target)) return Goal;
        return Default;
    }

    internal static bool IsReadOnly(string mode) => mode is Plan or Qa;

    internal static IReadOnlyList<string> FilterTools(string mode, IEnumerable<string> requested)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var filtered = new List<string>();
        foreach (var name in requested)
        {
            if (string.IsNullOrWhiteSpace(name) || !seen.Add(name)) continue;
            if (IsReadOnly(mode) && !ReadOnlyTools.Contains(name)) continue;
            filtered.Add(name);
        }

        return filtered;
    }

    internal static string BuildContext(string mode, string? target)
    {
        var context = new System.Text.StringBuilder();
        switch (mode)
        {
            case Qa:
                context.AppendLine("[仅问答模式：可以使用只读工具核对资料，但不得执行命令、写入、修改、删除文件，也不得调用其他有副作用的工具。回答问题后结束。]");
                break;
            case Plan:
                context.AppendLine("[计划模式：只允许只读探查，不得执行命令、写入、修改、删除文件，也不得调用其他有副作用的工具。完成必要探查后，必须调用 present_plan 提交计划（标题 + Markdown 正文），由用户在执行或取消之间选择；只有 present_plan 不可用时，才输出 <proposed_plan> 包裹的编号计划列表。提交计划后不要开始实施。]");
                break;
            case Goal:
                context.AppendLine("[目标模式：持续工作并验证结果，不能因为一次解释或中间状态就结束。只有在目标和完成条件确实满足时，才在最终答复末尾输出 " + GoalCompletionMarker + "；未满足时继续使用工具或说明仍缺少什么。]");
                break;
        }

        if (!string.IsNullOrWhiteSpace(target))
            context.Append("[用户目标：").Append(target.Trim()).AppendLine("]");

        return context.ToString().TrimEnd();
    }

    internal static bool HasGoalCompletionMarker(string? response) =>
        !string.IsNullOrWhiteSpace(response) && response.Contains(GoalCompletionMarker, StringComparison.Ordinal);

    internal static string RemoveGoalCompletionMarker(string response) =>
        response.Replace(GoalCompletionMarker, string.Empty, StringComparison.Ordinal).TrimEnd();

    internal static string BuildGoalContinuationPrompt() =>
        "目标尚未被明确完成。请继续检查完成条件并执行剩余工作；不要给出最终答复，直到目标确实完成。完成时才输出 " + GoalCompletionMarker + "。";
}
