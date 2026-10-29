using RemoteCli.Desktop;

namespace RemoteCli.UnitTests;

public sealed class BrowserAddressTests
{
    [Theory]
    [InlineData("example.com", "https://example.com/")]
    [InlineData("  example.com/path?q=1  ", "https://example.com/path?q=1")]
    [InlineData("//example.com/x", "https://example.com/x")]
    [InlineData("http://example.com:8080/", "http://example.com:8080/")]
    [InlineData("HTTPS://Example.COM/", "https://example.com/")]
    public void NormalizeAddsHttpsAndKeepsHttp(string input, string expected)
        => Assert.Equal(expected, BrowserAddress.Normalize(input));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///C:/Windows/win.ini")]
    [InlineData("data:text/html,hello")]
    [InlineData("https://user:secret@example.com/")]
    [InlineData("not a host")]
    [InlineData("http:///missing-host")]
    public void NormalizeRejectsUnsafeAddresses(string? input)
        => Assert.Null(BrowserAddress.Normalize(input));

    [Fact]
    public void NormalizeRejectsOverlongInput()
        => Assert.Null(BrowserAddress.Normalize("https://example.com/" + new string('a', BrowserAddress.MaxLength)));

    [Theory]
    [InlineData("about:blank", true)]
    [InlineData("about:config", false)]
    [InlineData("https://example.com/", true)]
    [InlineData("http://127.0.0.1:5765/", true)]
    [InlineData("mailto:x@example.com", false)]
    public void IsAllowedNavigationOnlyPermitsHttpAndBlank(string? url, bool allowed)
        => Assert.Equal(allowed, BrowserAddress.IsAllowedNavigation(url));
}
