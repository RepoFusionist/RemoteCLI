using System.Text;

namespace RemoteCli.Desktop;

// Line-window reader shared by the initial file preview and follow-up page requests.
internal static class TextPreviewPages
{
    internal const long MaxFileBytes = 16L * 1024 * 1024;
    internal const long MaxHtmlBytes = 4L * 1024 * 1024;
    internal const int DefaultLines = 2000;
    internal const int MaxLines = 5000;
    internal const int MaxChars = 2 * 1024 * 1024;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly UnicodeEncoding StrictUtf16Le = new(false, false, true);
    private static readonly UnicodeEncoding StrictUtf16Be = new(true, false, true);

    internal static (string Text, string EncodingName) Decode(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return (StrictUtf8.GetString(bytes, 3, bytes.Length - 3), "utf-8");
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return (StrictUtf16Le.GetString(bytes, 2, bytes.Length - 2), "utf-16le");
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return (StrictUtf16Be.GetString(bytes, 2, bytes.Length - 2), "utf-16be");
        }

        try
        {
            return (StrictUtf8.GetString(bytes), "utf-8");
        }
        catch (DecoderFallbackException)
        {
            throw new InvalidDataException("文件不是 UTF-8 文本，暂不支持预览。");
        }
    }

    internal static List<string> SplitLines(string text)
    {
        var lines = new List<string>();
        var start = 0;
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (character is not ('\r' or '\n'))
            {
                continue;
            }

            lines.Add(text[start..index]);
            if (character == '\r' && index + 1 < text.Length && text[index + 1] == '\n')
            {
                index++;
            }

            start = index + 1;
        }

        lines.Add(text[start..]);
        return lines;
    }

    internal static (string Content, int Count, bool Eof) Slice(List<string> lines, int offset, int limit)
    {
        var start = Math.Clamp(offset - 1, 0, lines.Count);
        var end = start;
        var used = 0;
        while (end < lines.Count && end - start < limit)
        {
            var lineLength = lines[end].Length;
            if (end == start && lineLength > MaxChars)
            {
                throw new InvalidDataException("文件中存在超过 2 MiB 的单行，暂不支持预览。");
            }

            if (end > start && used + lineLength + 1 > MaxChars)
            {
                break;
            }

            used += lineLength + 1;
            end++;
        }

        return (string.Join("\n", lines.GetRange(start, end - start)), end - start, end >= lines.Count);
    }

    internal static byte[] ReadAllBytesShared(string path, long maxBytes)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > maxBytes)
        {
            throw new InvalidDataException("文件大小已超过预览上限。");
        }

        using var buffer = new MemoryStream((int)Math.Min(stream.Length, maxBytes));
        stream.CopyTo(buffer);
        if (buffer.Length > maxBytes)
        {
            throw new InvalidDataException("文件大小已超过预览上限。");
        }

        return buffer.ToArray();
    }

    internal static string VersionTag(FileInfo info) => info.Length + ":" + info.LastWriteTimeUtc.Ticks;
}
