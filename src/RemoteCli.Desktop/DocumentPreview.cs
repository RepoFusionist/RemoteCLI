using System.IO.Compression;
using System.Text;
using System.Text.Json.Serialization;
using System.Xml;
using System.Xml.Linq;

namespace RemoteCli.Desktop;

// Read-only, bounded previews. Office files never execute macros or external relationships.
internal static class DocumentPreview
{
    internal const int MaxFileBytes = 16 * 1024 * 1024;
    private const long MaxExpandedBytes = 48 * 1024 * 1024;
    private const long MaxXmlBytes = 8 * 1024 * 1024;
    private const int MaxCharacters = 500_000;
    private const int MaxRows = 250;
    private const int MaxColumns = 48;

    internal sealed record WordBlock([property: JsonPropertyName("kind")] string Kind, [property: JsonPropertyName("text")] string Text, [property: JsonPropertyName("rows")] IReadOnlyList<IReadOnlyList<string>>? Rows = null);
    internal sealed record WordResult([property: JsonPropertyName("blocks")] IReadOnlyList<WordBlock> Blocks, [property: JsonPropertyName("truncated")] bool Truncated);
    internal sealed record SheetRow([property: JsonPropertyName("number")] int Number, [property: JsonPropertyName("cells")] IReadOnlyList<string> Cells);
    internal sealed record Sheet([property: JsonPropertyName("name")] string Name, [property: JsonPropertyName("rows")] IReadOnlyList<SheetRow> Rows, [property: JsonPropertyName("truncated")] bool Truncated);
    internal sealed record Workbook([property: JsonPropertyName("sheets")] IReadOnlyList<Sheet> Sheets, [property: JsonPropertyName("truncated")] bool Truncated);

    internal static bool IsPdf(byte[] bytes) => bytes.AsSpan().StartsWith("%PDF-"u8);

    internal static void ValidateOfficeContainer(byte[] bytes, string extension)
    {
        if (bytes.Length > MaxFileBytes) throw new InvalidDataException("文件超过 16 MiB，暂不支持预览。");
        if (extension is ".doc" or ".xls")
        {
            if (!bytes.AsSpan().StartsWith(new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }))
                throw new InvalidDataException("旧版 Office 文件无效或已损坏。");
            return;
        }
        using var stream = new MemoryStream(bytes, writable: false);
        using var archive = OpenArchive(stream);
        var required = extension == ".docx" ? "word/document.xml" : "xl/workbook.xml";
        if (archive.GetEntry(required) is null) throw new InvalidDataException("Office 文件缺少必要内容。");
    }

    internal static byte[] ReadBounded(Stream input)
    {
        using var output = new MemoryStream();
        var buffer = new byte[64 * 1024];
        int count;
        while ((count = input.Read(buffer, 0, buffer.Length)) != 0)
        {
            if (output.Length + count > MaxFileBytes) throw new InvalidDataException("文件超过 16 MiB，暂不支持预览。");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }

    // Apply print scaling only to the temporary rendering copy; never change the workbook on disk.
    internal static byte[] FitExcelPreviewToWidth(byte[] bytes)
    {
        using var source = new MemoryStream(bytes, writable: false);
        using var archive = OpenArchive(source);
        using var output = new MemoryStream();
        using (var result = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var entry in archive.Entries)
            {
                var target = result.CreateEntry(entry.FullName, CompressionLevel.Optimal);
                using var targetStream = target.Open();
                if (entry.FullName.StartsWith("xl/worksheets/", StringComparison.Ordinal) &&
                    entry.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) &&
                    !entry.FullName.Contains("/_rels/", StringComparison.Ordinal) && entry.Length <= MaxXmlBytes)
                {
                    var xml = LoadXml(archive, entry.FullName);
                    var root = xml.Root;
                    if (root is not null && root.Name.LocalName == "worksheet")
                    {
                        var ns = root.Name.Namespace;
                        var sheetPr = root.Element(ns + "sheetPr");
                        if (sheetPr is null) { sheetPr = new XElement(ns + "sheetPr"); root.AddFirst(sheetPr); }
                        var pageSetUpPr = sheetPr.Element(ns + "pageSetUpPr");
                        if (pageSetUpPr is null) { pageSetUpPr = new XElement(ns + "pageSetUpPr"); sheetPr.Add(pageSetUpPr); }
                        pageSetUpPr.SetAttributeValue("fitToPage", "1");
                        var pageSetup = root.Element(ns + "pageSetup");
                        if (pageSetup is null)
                        {
                            pageSetup = new XElement(ns + "pageSetup");
                            var after = root.Elements().FirstOrDefault(e => e.Name.LocalName is "headerFooter" or "rowBreaks" or "colBreaks" or "customProperties" or "cellWatches" or "ignoredErrors" or "smartTags" or "drawing" or "legacyDrawing" or "legacyDrawingHF" or "picture" or "oleObjects" or "controls" or "webPublishItems" or "tableParts" or "extLst");
                            if (after is null) root.Add(pageSetup); else after.AddBeforeSelf(pageSetup);
                        }
                        pageSetup.SetAttributeValue("fitToWidth", "1");
                        pageSetup.SetAttributeValue("fitToHeight", "0");
                    }
                    xml.Save(targetStream, SaveOptions.DisableFormatting);
                }
                else { using var original = entry.Open(); original.CopyTo(targetStream); }
            }
        }
        // A recompressed document can be larger; the original remains a valid fallback.
        return output.Length <= MaxFileBytes ? output.ToArray() : bytes;
    }

    internal static WordResult ReadWord(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        using var archive = OpenArchive(stream);
        var document = LoadXml(archive, "word/document.xml");
        var body = document.Descendants().FirstOrDefault(e => e.Name.LocalName == "body")
            ?? throw new InvalidDataException("Word 文档缺少正文。");
        var blocks = new List<WordBlock>();
        var characters = 0;
        var truncated = false;
        foreach (var element in body.Elements())
        {
            if (blocks.Count >= 1000 || characters >= MaxCharacters) { truncated = true; break; }
            if (element.Name.LocalName == "p")
            {
                var text = ParagraphText(element);
                characters += text.Length;
                if (characters > MaxCharacters) { truncated = true; break; }
                blocks.Add(new("paragraph", text));
            }
            else if (element.Name.LocalName == "tbl")
            {
                var rows = new List<IReadOnlyList<string>>();
                foreach (var row in element.Elements().Where(e => e.Name.LocalName == "tr"))
                {
                    if (rows.Count >= MaxRows || characters >= MaxCharacters) { truncated = true; break; }
                    var cells = new List<string>();
                    foreach (var cell in row.Elements().Where(e => e.Name.LocalName == "tc"))
                    {
                        if (cells.Count >= MaxColumns) { truncated = true; break; }
                        var text = string.Join("\n", cell.Descendants().Where(e => e.Name.LocalName == "p").Select(ParagraphText));
                        characters += text.Length;
                        if (characters > MaxCharacters) { truncated = true; break; }
                        cells.Add(text);
                    }
                    rows.Add(cells);
                }
                blocks.Add(new("table", "", rows));
            }
        }
        return new(blocks, truncated);
    }

    internal static Workbook ReadExcel(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        using var archive = OpenArchive(stream);
        var workbook = LoadXml(archive, "xl/workbook.xml");
        var relationships = LoadXml(archive, "xl/_rels/workbook.xml.rels");
        var targets = relationships.Descendants().Where(e => e.Name.LocalName == "Relationship" &&
                !string.Equals((string?)e.Attribute("TargetMode"), "External", StringComparison.OrdinalIgnoreCase))
            .Where(e => e.Attribute("Id") is not null && e.Attribute("Target") is not null)
            .ToDictionary(e => (string)e.Attribute("Id")!, e => (string)e.Attribute("Target")!, StringComparer.Ordinal);
        var stringsEntry = archive.GetEntry("xl/sharedStrings.xml");
        var strings = stringsEntry is null ? [] : LoadXml(archive, "xl/sharedStrings.xml").Descendants()
            .Where(e => e.Name.LocalName == "si")
            .Select(e => string.Concat(e.Descendants().Where(t => t.Name.LocalName == "t").Select(t => t.Value)))
            .Take(100_000).ToArray();
        var sheets = new List<Sheet>();
        var truncated = false;
        var characters = 0;
        foreach (var element in workbook.Descendants().Where(e => e.Name.LocalName == "sheet"))
        {
            if (sheets.Count >= 12) { truncated = true; break; }
            var id = element.Attributes().FirstOrDefault(a => a.Name.LocalName == "id")?.Value;
            if (id is null || !targets.TryGetValue(id, out var target)) continue;
            var uri = new Uri(new Uri("https://preview.invalid/xl/"), target);
            if (uri.Host != "preview.invalid" || !uri.AbsolutePath.StartsWith("/xl/worksheets/", StringComparison.Ordinal))
                throw new InvalidDataException("工作表路径无效。");
            var xml = LoadXml(archive, Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/')));
            var rows = new List<SheetRow>();
            var sheetTruncated = false;
            foreach (var row in xml.Descendants().Where(e => e.Name.LocalName == "row"))
            {
                if (rows.Count >= MaxRows || characters >= MaxCharacters) { sheetTruncated = true; break; }
                var rowNumber = int.TryParse((string?)row.Attribute("r"), out var n) && n > 0 ? n : rows.Count + 1;
                var cells = new List<string>();
                foreach (var cell in row.Elements().Where(e => e.Name.LocalName == "c"))
                {
                    var col = ColumnIndex((string?)cell.Attribute("r"), cells.Count);
                    if (col >= MaxColumns) { sheetTruncated = true; continue; }
                    while (cells.Count <= col) cells.Add("");
                    var type = (string?)cell.Attribute("t");
                    var raw = cell.Elements().FirstOrDefault(e => e.Name.LocalName == "v")?.Value ?? "";
                    var value = type == "s" && int.TryParse(raw, out var index) && index >= 0 && index < strings.Length ? strings[index]
                        : type == "inlineStr" ? string.Concat(cell.Descendants().Where(e => e.Name.LocalName == "t").Select(e => e.Value))
                        : type == "b" ? (raw == "1" ? "TRUE" : "FALSE") : raw;
                    characters += value.Length;
                    if (characters > MaxCharacters) { sheetTruncated = true; break; }
                    cells[col] = value;
                }
                rows.Add(new(rowNumber, cells));
            }
            sheets.Add(new((string?)element.Attribute("name") ?? "工作表", rows, sheetTruncated));
            truncated |= sheetTruncated;
        }
        if (sheets.Count == 0) throw new InvalidDataException("Excel 工作簿没有可预览的工作表。");
        return new(sheets, truncated);
    }

    private static ZipArchive OpenArchive(Stream stream)
    {
        if (stream.Length > MaxFileBytes) throw new InvalidDataException("文件超过预览上限（16 MiB）。");
        var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        try
        {
            if (archive.Entries.Count > 2000)
                throw new InvalidDataException("文档解压后过大，无法预览。");
            long expanded = 0;
            foreach (var entry in archive.Entries)
            {
                if (entry.Length < 0 || entry.Length > MaxExpandedBytes - expanded)
                    throw new InvalidDataException("文档解压后过大，无法预览。");
                expanded += entry.Length;
            }
            return archive;
        }
        catch { archive.Dispose(); throw; }
    }

    private static XDocument LoadXml(ZipArchive archive, string name)
    {
        var entry = archive.GetEntry(name) ?? throw new InvalidDataException("文档缺少必要内容。");
        if (entry.Length > MaxXmlBytes) throw new InvalidDataException("文档内容过大，无法预览。");
        using var source = entry.Open();
        using var reader = XmlReader.Create(source, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxXmlBytes });
        return XDocument.Load(reader);
    }

    private static string ParagraphText(XElement paragraph)
    {
        var builder = new StringBuilder();
        foreach (var element in paragraph.Descendants())
        {
            if (element.Name.LocalName == "t") builder.Append(element.Value);
            else if (element.Name.LocalName == "tab") builder.Append('\t');
            else if (element.Name.LocalName is "br" or "cr") builder.Append('\n');
        }
        return builder.ToString();
    }

    private static int ColumnIndex(string? reference, int fallback)
    {
        if (string.IsNullOrEmpty(reference)) return fallback;
        var value = 0;
        foreach (var ch in reference)
        {
            if (ch is >= 'A' and <= 'Z') value = Math.Min(MaxColumns + 1, value * 26 + ch - 'A' + 1);
            else if (ch is >= 'a' and <= 'z') value = Math.Min(MaxColumns + 1, value * 26 + ch - 'a' + 1);
            else break;
        }
        return value > 0 ? value - 1 : fallback;
    }
}
