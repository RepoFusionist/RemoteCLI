namespace RemoteCli.Desktop;

/// <summary>压缩判定只需要消息形状，不依赖具体历史对象。</summary>
internal sealed record ContextMessage(string Role, bool HasToolCalls, string? ToolCallId);

internal static class ContextCompression
{
    internal const int DefaultContextWindow = 128_000;
    internal const int MinimumContextWindow = 8_192;
    internal const int MaximumContextWindow = 2_000_000;
    internal const double TriggerRatio = 0.65;
    internal const int MinimumMessages = 8;
    internal const int KeepRecentMessages = 6;
    internal const int MinimumPrefixMessages = 4;
    internal const int MaximumCompressionsPerRequest = 3;
    internal const int MaximumSummaryCharacters = 8_000;
    internal const int MaximumPrefixCharacters = 60_000;

    internal static int ResolveBudget(int? contextWindow) =>
        Math.Clamp(contextWindow ?? DefaultContextWindow, MinimumContextWindow, MaximumContextWindow);

    internal static int InputBudget(int? contextWindow, int? maxOutputTokens) =>
        Math.Max(1, ResolveBudget(contextWindow) - (maxOutputTokens ?? 4096));

    internal static long EstimateRequest(IEnumerable<ChatTurn> history, IEnumerable<string> fixedTexts) =>
        EstimateTokens(history.SelectMany(turn => new[] { turn.Content }
            .Concat(turn.ToolCalls?.SelectMany(call => new[] { call.Name, call.Arguments }) ?? []))) +
        EstimateTokens(fixedTexts) + history.Sum(turn => (long)(turn.Images?.Count ?? 0) * 2048);

    internal static bool ShouldCompress(long estimatedTokens, int? contextWindow, int messageCount) =>
        messageCount >= MinimumMessages && estimatedTokens > ResolveBudget(contextWindow) * TriggerRatio;

    /// <summary>返回要被摘要的前缀长度；0 表示不能安全压缩。</summary>
    internal static int SelectPrefixCount(IReadOnlyList<ContextMessage> messages)
    {
        if (messages.Count < MinimumMessages) return 0;
        var lastPossible = messages.Count - KeepRecentMessages;
        for (var cut = lastPossible; cut >= MinimumPrefixMessages; cut--)
        {
            if (messages[cut].Role == "user" && IsSafeCut(messages, cut)) return cut;
        }
        for (var cut = lastPossible; cut >= MinimumPrefixMessages; cut--)
            if (IsSafeCut(messages, cut)) return cut;

        return 0;
    }

    // 只允许切在普通用户发言前，避免把 assistant 的 tool_calls 与其工具结果拆开。
    private static bool IsSafeCut(IReadOnlyList<ContextMessage> messages, int cut) =>
        cut > 0 && cut < messages.Count && messages[cut].ToolCallId is null &&
        // A new assistant tool-call group is a safe boundary after the preceding tool results.
        (messages[cut].Role == "user" || messages[cut].Role == "assistant") &&
        !messages[cut - 1].HasToolCalls;

    internal static long EstimateTokens(IEnumerable<string?> texts)
    {
        long ascii = 0, cjk = 0, other = 0;
        foreach (var text in texts)
        {
            if (text is null) continue;
            foreach (var ch in text)
            {
                if (ch <= '\u007f') ascii++;
                else if (ch is >= '\u3400' and <= '\u9fff' or >= '\u3040' and <= '\u30ff' or >= '\uac00' and <= '\ud7af') cjk++;
                else other++;
            }
        }

        var input = (long)Math.Floor((ascii + other) / 3.3 + cjk / 1.7 + 0.5);
        if (input == 0 && ascii + cjk + other > 0) input = 1;
        return input;
    }
}
