using System.Net;
using System.Text.Json;
using RemoteCli.Desktop;

namespace RemoteCli.UnitTests;

public sealed class LocalChatToolsTests
{
    [Theory]
    [InlineData("127.0.0.1", false)]
    [InlineData("10.0.0.1", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("::1", false)]
    [InlineData("::ffff:192.168.1.1", false)]
    [InlineData("fd00::1", false)]
    [InlineData("8.8.8.8", true)]
    [InlineData("2606:4700:4700::1111", true)]
    public void WebFetchAllowsOnlyPublicAddresses(string value, bool expected)
        => Assert.Equal(expected, PublicWebFetch.IsPublic(IPAddress.Parse(value)));

    [Fact]
    public async Task ToolsRespectEnablementAndEditRequiresUniqueMatch()
    {
        var root = Path.Combine(Path.GetTempPath(), "longshell-tools-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "sample.txt");
            await File.WriteAllTextAsync(path, "first\nneedle\nneedle\n");
            var tools = LocalChatTools.Create(root, [LocalChatTools.SearchText, LocalChatTools.EditFile]);
            Assert.Equal(2, tools.Count);
            var search = tools.Single(tool => tool.Name == LocalChatTools.SearchText);
            using var searchArgs = JsonDocument.Parse("{\"query\":\"needle\"}");
            var found = await search.Execute(searchArgs.RootElement, CancellationToken.None);
            Assert.Contains("sample.txt:2:", found);
            Assert.Contains("sample.txt:3:", found);

            var edit = tools.Single(tool => tool.Name == LocalChatTools.EditFile);
            using var duplicate = JsonDocument.Parse("{\"path\":\"sample.txt\",\"old_text\":\"needle\",\"new_text\":\"changed\"}");
            Assert.StartsWith("错误：", await edit.Execute(duplicate.RootElement, CancellationToken.None));
            Assert.Equal("first\nneedle\nneedle\n", await File.ReadAllTextAsync(path));
            using var unique = JsonDocument.Parse("{\"path\":\"sample.txt\",\"old_text\":\"first\",\"new_text\":\"changed\"}");
            Assert.StartsWith("已修改：", await edit.Execute(unique.RootElement, CancellationToken.None));
            Assert.Equal("changed\nneedle\nneedle\n", await File.ReadAllTextAsync(path));
            using var escape = JsonDocument.Parse("{\"path\":\"../outside\",\"old_text\":\"x\",\"new_text\":\"y\"}");
            Assert.StartsWith("错误：", await edit.Execute(escape.RootElement, CancellationToken.None));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task FetchRejectsUnsafeUrlsBeforeConnecting()
    {
        Assert.StartsWith("错误：", await PublicWebFetch.FetchAsync("http://127.0.0.1/", CancellationToken.None));
        Assert.StartsWith("错误：", await PublicWebFetch.FetchAsync("https://127.0.0.1/", CancellationToken.None));
        Assert.StartsWith("错误：", await PublicWebFetch.FetchAsync("https://user:password@example.com/", CancellationToken.None));
        Assert.StartsWith("错误：", await PublicWebFetch.FetchAsync("https://example.com:8443/", CancellationToken.None));
    }
}
