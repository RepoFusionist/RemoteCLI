namespace RemoteCli.Desktop;

// Estimates text actually present in each model request. This is not provider billing usage.
internal sealed class GatewayTokenEstimate
{
    private long _total;
    private int _round;

    public (int Round, long InputTokens, long TotalInputTokens) AddRequest(IEnumerable<string?> texts)
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
        _total += input;
        return (++_round, input, _total);
    }
}
