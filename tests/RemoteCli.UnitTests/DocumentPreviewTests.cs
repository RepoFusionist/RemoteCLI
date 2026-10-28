using System.IO.Compression;
using System.Text;
using System.Text.Json;
using RemoteCli.Desktop;

namespace RemoteCli.UnitTests;

public sealed class DocumentPreviewTests
{
    [Fact]
    public void WordPreviewReadsParagraphsAndTablesAsText()
    {
        var bytes = Archive(("word/document.xml", """
            <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body>
              <w:p><w:r><w:t>Hello </w:t></w:r><w:r><w:t>世界</w:t><w:tab/><w:t>again</w:t></w:r></w:p>
              <w:tbl><w:tr><w:tc><w:p><w:r><w:t>A</w:t></w:r></w:p></w:tc><w:tc><w:p><w:r><w:t>B</w:t></w:r></w:p></w:tc></w:tr></w:tbl>
            </w:body></w:document>
            """));
        var result = DocumentPreview.ReadWord(bytes);
        Assert.False(result.Truncated);
        Assert.Equal("Hello 世界\tagain", result.Blocks[0].Text);
        Assert.Equal("table", result.Blocks[1].Kind);
        Assert.Equal(new[] { "A", "B" }, result.Blocks[1].Rows![0]);
        Assert.Equal("Hello 世界\tagain", JsonSerializer.SerializeToElement(result).GetProperty("blocks")[0].GetProperty("text").GetString());
    }

    [Fact]
    public void ExcelPreviewReadsSheetsSharedStringsAndCachedFormulaValues()
    {
        var bytes = Archive(
            ("xl/workbook.xml", """
             <workbook xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="第一页" r:id="rId1"/><sheet name="第二页" r:id="rId2"/></sheets></workbook>
             """),
            ("xl/_rels/workbook.xml.rels", """
             <Relationships><Relationship Id="rId1" Target="worksheets/sheet1.xml"/><Relationship Id="rId2" Target="worksheets/sheet2.xml"/></Relationships>
             """),
            ("xl/sharedStrings.xml", "<sst><si><r><t>共享</t></r><r><t>文本</t></r></si></sst>"),
            ("xl/worksheets/sheet1.xml", "<worksheet><sheetData><row r=\"4\"><c r=\"A4\" t=\"s\"><v>0</v></c><c r=\"C4\"><f>1+2</f><v>3</v></c></row></sheetData></worksheet>"),
            ("xl/worksheets/sheet2.xml", "<worksheet><sheetData><row r=\"1\"><c r=\"A1\" t=\"inlineStr\"><is><t>另一页</t></is></c></row></sheetData></worksheet>"));
        var result = DocumentPreview.ReadExcel(bytes);
        Assert.Equal(2, result.Sheets.Count);
        Assert.Equal("第一页", result.Sheets[0].Name);
        Assert.Equal(4, result.Sheets[0].Rows[0].Number);
        Assert.Equal(new[] { "共享文本", "", "3" }, result.Sheets[0].Rows[0].Cells);
        Assert.Equal("另一页", result.Sheets[1].Rows[0].Cells[0]);
        Assert.Equal("第一页", JsonSerializer.SerializeToElement(result).GetProperty("sheets")[0].GetProperty("name").GetString());
    }

    [Fact]
    public void ExcelRenderingCopyFitsWideSheetsAndPreservesSourceAndOtherParts()
    {
        var source = Archive(("xl/workbook.xml", "<workbook/>"),
            ("xl/worksheets/sheet1.xml", "<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData/><pageMargins left=\"1\"/><drawing/></worksheet>"),
            ("xl/drawings/drawing1.xml", "<drawing>chart</drawing>"));
        var originalHash = System.Security.Cryptography.SHA256.HashData(source);
        var copy = DocumentPreview.FitExcelPreviewToWidth(source);
        Assert.Equal(originalHash, System.Security.Cryptography.SHA256.HashData(source));
        using var archive = new ZipArchive(new MemoryStream(copy), ZipArchiveMode.Read);
        using var sheet = new StreamReader(archive.GetEntry("xl/worksheets/sheet1.xml")!.Open());
        var xml = System.Xml.Linq.XDocument.Parse(sheet.ReadToEnd());
        var ns = xml.Root!.Name.Namespace;
        Assert.Equal("1", (string?)xml.Root.Element(ns + "sheetPr")!.Element(ns + "pageSetUpPr")!.Attribute("fitToPage"));
        Assert.Equal("1", (string?)xml.Root.Element(ns + "pageSetup")!.Attribute("fitToWidth"));
        Assert.Equal("0", (string?)xml.Root.Element(ns + "pageSetup")!.Attribute("fitToHeight"));
        Assert.True(xml.Root.Elements().ToList().FindIndex(e => e.Name.LocalName == "pageSetup") < xml.Root.Elements().ToList().FindIndex(e => e.Name.LocalName == "drawing"));
        using var drawing = new StreamReader(archive.GetEntry("xl/drawings/drawing1.xml")!.Open());
        Assert.Equal("<drawing>chart</drawing>", drawing.ReadToEnd());
    }

    [Fact]
    public void RejectsCorruptArchivesExternalSheetsAndOversizedXml()
    {
        Assert.Throws<InvalidDataException>(() => DocumentPreview.ReadWord(Encoding.UTF8.GetBytes("not a zip")));
        var external = Archive(
            ("xl/workbook.xml", "<workbook xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheet name=\"x\" r:id=\"r1\"/></workbook>"),
            ("xl/_rels/workbook.xml.rels", "<Relationships><Relationship Id=\"r1\" Target=\"https://example.com/sheet.xml\"/></Relationships>"));
        Assert.Throws<InvalidDataException>(() => DocumentPreview.ReadExcel(external));
        var oversized = Archive(("word/document.xml", new string('x', 8 * 1024 * 1024 + 1)));
        Assert.Throws<InvalidDataException>(() => DocumentPreview.ReadWord(oversized));
        Assert.True(DocumentPreview.IsPdf("%PDF-1.7"u8.ToArray()));
        Assert.False(DocumentPreview.IsPdf("not pdf"u8.ToArray()));
        using var tooLarge = new MemoryStream(new byte[DocumentPreview.MaxFileBytes + 1]);
        Assert.Throws<InvalidDataException>(() => DocumentPreview.ReadBounded(tooLarge));
    }

    private static byte[] Archive(params (string Name, string Contents)[] entries)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, contents) in entries)
            {
                using var stream = archive.CreateEntry(name).Open();
                using var writer = new StreamWriter(stream, new UTF8Encoding(false));
                writer.Write(contents);
            }
        }
        return output.ToArray();
    }
}
