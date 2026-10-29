namespace RemoteCli.Desktop;

internal static class BrowserAddress
{
    internal const int MaxLength = 16 * 1024;

    internal static string? Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var text = input.Trim();
        if (text.Length > MaxLength) return null;
        if (text.StartsWith("//", StringComparison.Ordinal)) text = "https:" + text;
        else if (!text.Contains("://", StringComparison.Ordinal)) text = "https://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return null;
        if (!string.IsNullOrEmpty(uri.UserInfo)) return null;
        if (uri.HostNameType == UriHostNameType.Unknown) return null;
        return uri.AbsoluteUri;
    }

    internal static bool IsAllowedNavigation(string? url)
        => string.Equals(url, "about:blank", StringComparison.OrdinalIgnoreCase) || Normalize(url) is not null;
}
