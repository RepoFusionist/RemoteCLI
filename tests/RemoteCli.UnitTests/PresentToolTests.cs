using System.Text.Json;
using RemoteCli.Desktop;

namespace RemoteCli.UnitTests;

public sealed class PresentToolTests : IDisposable
{
    private readonly string _root;

    public PresentToolTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "remotecli-present-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private LocalChatTool Tool(Action<IReadOnlyList<PresentedFile>>? onPresented = null)
        => LocalChatTools.Create(_root, new[] { LocalChatTools.Present }, null, onPresented)
            .Single(tool => tool.Name == LocalChatTools.Present);

    private static async Task<string> RunAsync(LocalChatTool tool, string json)
    {
        using var document = JsonDocument.Parse(json);
        return await tool.Execute(document.RootElement, CancellationToken.None);
    }

    private static string FilesJson(string path, string? description = null)
    {
        var payload = description is null
            ? "{\"path\":" + JsonSerializer.Serialize(path) + "}"
            : "{\"path\":" + JsonSerializer.Serialize(path) + ",\"description\":" + JsonSerializer.Serialize(description) + "}";
        return "{\"files\":[" + payload + "]}";
    }

    [Fact]
    public void PresentIsRegisteredOnlyWhenEnabled()
    {
        var withoutPresent = LocalChatTools.Create(_root, new[] { LocalChatTools.ReadFile });
        Assert.DoesNotContain(withoutPresent, tool => tool.Name == LocalChatTools.Present);
        Assert.Contains(LocalChatTools.All, name => name == LocalChatTools.Present);
    }

    [Fact]
    public async Task PresentsAnExistingFileWithItsAbsolutePath()
    {
        var file = Path.Combine(_root, "report.md");
        await File.WriteAllTextAsync(file, "# report");
        IReadOnlyList<PresentedFile>? presented = null;

        var output = await RunAsync(Tool(files => presented = files), FilesJson("report.md", "评估报告"));

        Assert.Equal("Presented " + file, output);
        Assert.NotNull(presented);
        Assert.Equal(file, presented![0].Path);
        Assert.Equal("评估报告", presented[0].Description);
    }

    [Fact]
    public async Task AcceptsAnAbsolutePathInsideTheWorkspace()
    {
        var file = Path.Combine(_root, "nested.txt");
        await File.WriteAllTextAsync(file, "content");

        var output = await RunAsync(Tool(), FilesJson(file));

        Assert.Equal("Presented " + file, output);
    }

    [Fact]
    public async Task RejectsAMissingFileWithoutNotifyingTheUi()
    {
        var notified = false;

        var output = await RunAsync(Tool(_ => notified = true), FilesJson("missing.md"));

        Assert.StartsWith("错误：文件不存在", output);
        Assert.False(notified);
    }

    [Fact]
    public async Task RejectsDirectories()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        var notified = false;

        var output = await RunAsync(Tool(_ => notified = true), FilesJson("src"));

        Assert.StartsWith("错误：不能交付目录", output);
        Assert.False(notified);
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("..")]
    public async Task RejectsPathsOutsideTheWorkspace(string path)
    {
        var output = await RunAsync(Tool(), FilesJson(path));

        Assert.StartsWith("错误：无法声明", output);
    }

    [Fact]
    public async Task RejectsEmptyListsAndTooManyFiles()
    {
        var empty = await RunAsync(Tool(), "{\"files\":[]}");
        Assert.Equal("错误：present 需要至少一个文件。", empty);

        var many = string.Join(",", Enumerable.Range(0, 9).Select(index => "{\"path\":\"f" + index + ".txt\"}"));
        var tooMany = await RunAsync(Tool(), "{\"files\":[" + many + "]}");
        Assert.Equal("错误：present 每次最多 8 个文件。", tooMany);
    }

    [Fact]
    public async Task RejectsMalformedArguments()
    {
        var missing = await RunAsync(Tool(), "{}");
        Assert.Equal("错误：files 必须是数组。", missing);

        var blank = await RunAsync(Tool(), FilesJson("   "));
        Assert.Equal("错误：文件路径不能为空。", blank);
    }

    [Fact]
    public async Task DescriptionsAreOptional()
    {
        var file = Path.Combine(_root, "plain.txt");
        await File.WriteAllTextAsync(file, "x");

        var output = await RunAsync(Tool(), FilesJson("plain.txt"));

        Assert.Equal("Presented " + file, output);
    }
}
