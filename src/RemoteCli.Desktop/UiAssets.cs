using System.Reflection;
using System.Text;

namespace RemoteCli.Desktop;

/// <summary>Inlines the shipped terminal library into the WebView document without network access.</summary>
internal static class UiAssets
{
    internal static string Inject(string html, Assembly? assembly = null)
    {
        assembly ??= Assembly.GetExecutingAssembly();
        html = Replace(html, "/*LONGSHELL_XTERM_CSS*/", Read(assembly, "xterm.css"), "</style");
        html = Replace(html, "/*LONGSHELL_XTERM_JS*/", Read(assembly, "xterm.js"), "</script");
        return Replace(html, "/*LONGSHELL_XTERM_SERIALIZE_JS*/", Read(assembly, "addon-serialize.js"), "</script");
    }

    private static string Read(Assembly assembly, string name)
    {
        using var stream = assembly.GetManifestResourceStream("RemoteCli.Desktop.UI.vendor." + name)
            ?? throw new InvalidOperationException("Embedded terminal asset is missing: " + name);
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true));
        return reader.ReadToEnd();
    }

    private static string Replace(string html, string marker, string asset, string forbiddenClosingTag)
    {
        var first = html.IndexOf(marker, StringComparison.Ordinal);
        if (first < 0 || html.IndexOf(marker, first + marker.Length, StringComparison.Ordinal) >= 0 ||
            asset.Contains(forbiddenClosingTag, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Embedded terminal asset marker is invalid: " + marker);
        return html.Replace(marker, asset, StringComparison.Ordinal);
    }
}
