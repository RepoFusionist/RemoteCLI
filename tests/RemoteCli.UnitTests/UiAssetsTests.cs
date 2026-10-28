using RemoteCli.Desktop;

namespace RemoteCli.UnitTests;

public sealed class UiAssetsTests
{
    [Fact]
    public void EmbeddedTerminalAssetsReplaceExactlyOneMarkerEach()
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "RemoteCli.Desktop", "UI", "longshell-ui.html"));
        var html = File.ReadAllText(path);
        var assembled = UiAssets.Inject(html);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(assembled) < 2 * 1024 * 1024,
            "NavigateToString content must stay below WebView2's 2 MB limit.");
        Assert.DoesNotContain("/*LONGSHELL_XTERM_CSS*/", assembled);
        Assert.DoesNotContain("/*LONGSHELL_XTERM_JS*/", assembled);
        Assert.DoesNotContain("/*LONGSHELL_XTERM_SERIALIZE_JS*/", assembled);
        Assert.True(assembled.Length > html.Length + 400000);
        Assert.Throws<InvalidOperationException>(() => UiAssets.Inject(html + "/*LONGSHELL_XTERM_JS*/"));
    }
}
