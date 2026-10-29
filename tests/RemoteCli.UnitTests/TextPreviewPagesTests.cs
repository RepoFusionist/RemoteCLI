using System.Text;
using RemoteCli.Desktop;

namespace RemoteCli.UnitTests;

public sealed class TextPreviewPagesTests
{
    [Fact]
    public void DecodeHandlesUtf8BomAndStrictUtf8()
    {
        var plain = TextPreviewPages.Decode(Encoding.UTF8.GetBytes("hello 世界"));
        Assert.Equal("hello 世界", plain.Text);
        Assert.Equal("utf-8", plain.EncodingName);

        var bom = TextPreviewPages.Decode(new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("bom")).ToArray());
        Assert.Equal("bom", bom.Text);
        Assert.Equal("utf-8", bom.EncodingName);
    }

    [Fact]
    public void DecodeHandlesUtf16Bom()
    {
        var little = TextPreviewPages.Decode(new byte[] { 0xFF, 0xFE }.Concat(Encoding.Unicode.GetBytes("line")).ToArray());
        Assert.Equal("line", little.Text);
        Assert.Equal("utf-16le", little.EncodingName);

        var big = TextPreviewPages.Decode(new byte[] { 0xFE, 0xFF }.Concat(Encoding.BigEndianUnicode.GetBytes("line")).ToArray());
        Assert.Equal("line", big.Text);
        Assert.Equal("utf-16be", big.EncodingName);
    }

    [Fact]
    public void DecodeRejectsNonUtf8Bytes()
    {
        var invalid = new byte[] { 0xC3, 0x28 };
        var error = Assert.Throws<InvalidDataException>(() => TextPreviewPages.Decode(invalid));
        Assert.Contains("UTF-8", error.Message);
    }

    [Fact]
    public void SplitLinesKeepsEveryLineEndingAndTrailingEmptyLine()
    {
        Assert.Equal(new[] { "a", "b", "c", "c" }, TextPreviewPages.SplitLines("a\r\nb\rc\nc"));
        Assert.Equal(new[] { "a", "" }, TextPreviewPages.SplitLines("a\n"));
        Assert.Equal(new[] { "" }, TextPreviewPages.SplitLines(""));
    }

    [Fact]
    public void SlicePagesByLineAndReportsEndOfFile()
    {
        var lines = TextPreviewPages.SplitLines("1\n2\n3\n4\n5");
        var first = TextPreviewPages.Slice(lines, 1, 2);
        Assert.Equal("1\n2", first.Content);
        Assert.Equal(2, first.Count);
        Assert.False(first.Eof);

        var second = TextPreviewPages.Slice(lines, 3, 2);
        Assert.Equal("3\n4", second.Content);
        Assert.False(second.Eof);

        var last = TextPreviewPages.Slice(lines, 5, 2);
        Assert.Equal("5", last.Content);
        Assert.Equal(1, last.Count);
        Assert.True(last.Eof);
    }

    [Fact]
    public void SliceClampsOffsetsAndCapsByCharacters()
    {
        var lines = TextPreviewPages.SplitLines(new string('x', 1024 * 1024) + "\n" + new string('y', 1024 * 1024) + "\nz");
        var page = TextPreviewPages.Slice(lines, 1, 5000);
        Assert.Equal(1, page.Count);
        Assert.False(page.Eof);

        var beyond = TextPreviewPages.Slice(lines, 99, 10);
        Assert.Equal("", beyond.Content);
        Assert.Equal(0, beyond.Count);
        Assert.True(beyond.Eof);
    }

    [Fact]
    public void SliceRejectsASingleOversizedLine()
    {
        var lines = TextPreviewPages.SplitLines(new string('x', TextPreviewPages.MaxChars + 1));
        var error = Assert.Throws<InvalidDataException>(() => TextPreviewPages.Slice(lines, 1, 10));
        Assert.Contains("单行", error.Message);
    }

    [Fact]
    public void ReadAllBytesSharedRejectsOversizedFiles()
    {
        var path = Path.Combine(Path.GetTempPath(), "remotecli-text-" + Guid.NewGuid().ToString("N"));
        File.WriteAllBytes(path, new byte[64]);
        try
        {
            Assert.Equal(64, TextPreviewPages.ReadAllBytesShared(path, 64).Length);
            Assert.Throws<InvalidDataException>(() => TextPreviewPages.ReadAllBytesShared(path, 32));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
