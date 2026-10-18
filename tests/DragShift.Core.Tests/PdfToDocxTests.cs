using System.IO.Compression;
using System.Xml.Linq;
using DragShift.Core;
using ImageMagick;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace DragShift.Core.Tests;

[Collection("Platform")]
public sealed class PdfToDocxTests : IDisposable
{
    static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    readonly TestFiles files = new();

    public void Dispose()
    {
        Platform.Pdf = null;
        files.Dispose();
    }

    /// <summary>A text PDF: a big bold title, two body paragraphs (one with a word hyphenated across lines), an italic line and a picture.</summary>
    string TextPdf()
    {
        var builder = new PdfDocumentBuilder();
        var regular = builder.AddStandard14Font(Standard14Font.Helvetica);
        var bold = builder.AddStandard14Font(Standard14Font.HelveticaBold);
        var italic = builder.AddStandard14Font(Standard14Font.HelveticaOblique);
        var page = builder.AddPage(PageSize.Letter);

        page.AddText("Quarterly Report", 24, new PdfPoint(180, 720), bold);
        page.AddText("The first paragraph talks about the transfor-", 11, new PdfPoint(72, 660), regular);
        page.AddText("mation of the business over the last year.", 11, new PdfPoint(72, 646), regular);
        page.AddText("A second paragraph starts well below the first one.", 11, new PdfPoint(72, 560), regular);
        page.AddText("This line is set in italics.", 11, new PdfPoint(72, 480), italic);

        using (var img = new MagickImage(MagickColors.OrangeRed, 120, 80) { Format = MagickFormat.Png })
            page.AddPng(img.ToByteArray(), new PdfRectangle(72, 250, 252, 370));

        var path = files.Path("report.pdf");
        File.WriteAllBytes(path, builder.Build());
        return path;
    }

    static XDocument DocumentXml(string docx)
    {
        using var zip = ZipFile.OpenRead(docx);
        using var s = zip.GetEntry("word/document.xml")!.Open();
        return XDocument.Load(s);
    }

    static List<string> ParagraphTexts(XDocument doc) =>
        doc.Descendants(W + "p").Select(p => string.Concat(p.Descendants(W + "t").Select(t => t.Value))).Where(t => t.Length > 0).ToList();

    [Fact]
    public void Text_pdf_becomes_editable_paragraphs()
    {
        var output = Assert.Single(Converter.Convert(TextPdf(), TargetFormat.Docx));
        Assert.Equal("report.docx", Path.GetFileName(output));
        var doc = DocumentXml(output);
        var paragraphs = ParagraphTexts(doc);

        Assert.Equal("Quarterly Report", paragraphs[0]);
        Assert.Contains("The first paragraph talks about the transformation of the business over the last year.", paragraphs);
        Assert.Contains("A second paragraph starts well below the first one.", paragraphs);
        Assert.Contains("This line is set in italics.", paragraphs);
    }

    [Fact]
    public void Title_is_bold_and_large_and_italics_are_kept()
    {
        var doc = DocumentXml(Converter.Convert(TextPdf(), TargetFormat.Docx).Single());
        var runs = doc.Descendants(W + "r").Where(r => r.Element(W + "t") is not null).ToList();

        var title = runs.First(r => r.Element(W + "t")!.Value.Contains("Quarterly"));
        var props = title.Element(W + "rPr")!;
        Assert.NotNull(props.Element(W + "b"));
        Assert.Equal("48", props.Element(W + "sz")!.Attribute(W + "val")!.Value);

        var body = runs.First(r => r.Element(W + "t")!.Value.Contains("second paragraph"));
        Assert.Null(body.Element(W + "rPr")!.Element(W + "b"));
        Assert.Equal("22", body.Element(W + "rPr")!.Element(W + "sz")!.Attribute(W + "val")!.Value);

        var it = runs.First(r => r.Element(W + "t")!.Value.Contains("italics"));
        Assert.NotNull(it.Element(W + "rPr")!.Element(W + "i"));
    }

    [Fact]
    public void Embedded_images_are_carried_over_in_place()
    {
        var output = Converter.Convert(TextPdf(), TargetFormat.Docx).Single();
        using var zip = ZipFile.OpenRead(output);
        var media = Assert.Single(zip.Entries, e => e.FullName.StartsWith("word/media/"));
        using (var s = media.Open())
        using (var img = new MagickImage(s))
            Assert.Equal((120u, 80u), (img.Width, img.Height));

        // The picture sits after the text above it: the italic line (y=480) comes first, the image (top y=370) after.
        var doc = DocumentXml(output);
        var order = doc.Descendants(W + "p").Select(p => p.Descendants(W + "drawing").Any() ? "IMG" : string.Concat(p.Descendants(W + "t").Select(t => t.Value))).ToList();
        Assert.True(order.IndexOf("IMG") > order.IndexOf("This line is set in italics."));
    }

    [Fact]
    public void Two_columns_are_read_column_by_column()
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var page = builder.AddPage(PageSize.Letter);
        for (var i = 0; i < 6; i++)
        {
            page.AddText($"Left column line {i + 1} of text here", 10, new PdfPoint(60, 700 - i * 13), font);
            page.AddText($"Right column line {i + 1} of text here", 10, new PdfPoint(330, 700 - i * 13), font);
        }
        var pdf = files.Path("columns.pdf");
        File.WriteAllBytes(pdf, builder.Build());

        var text = string.Join("\n", ParagraphTexts(DocumentXml(Converter.Convert(pdf, TargetFormat.Docx).Single())));
        Assert.True(text.IndexOf("Left column line 6") < text.IndexOf("Right column line 1"), text);
    }

    [Fact]
    public void Scanned_pages_become_pictures_and_only_those_pages_are_rendered()
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        builder.AddPage(PageSize.Letter).AddText("A page with real text on it.", 12, new PdfPoint(72, 700), font);
        using (var img = new MagickImage(MagickColors.Gray, 200, 260) { Format = MagickFormat.Png })
            builder.AddPage(PageSize.Letter).AddPng(img.ToByteArray(), new PdfRectangle(0, 0, 612, 792));
        var pdf = files.Path("mixed.pdf");
        File.WriteAllBytes(pdf, builder.Build());

        var fake = new FakePdf(2);
        Platform.Pdf = fake;
        var output = Converter.Convert(pdf, TargetFormat.Docx).Single();

        Assert.Equal([1], fake.Rendered);
        var doc = DocumentXml(output);
        Assert.Contains("A page with real text on it.", ParagraphTexts(doc));
        Assert.Single(doc.Descendants(W + "drawing"));
        Assert.Single(doc.Descendants(W + "br"), b => (string?)b.Attribute(W + "type") == "page");
    }

    [Fact]
    public void Unreadable_pdf_fails_with_a_user_message()
    {
        var pdf = files.Path("broken.pdf");
        File.WriteAllText(pdf, "not a pdf at all");
        Assert.Throws<UserFacingException>(() => Converter.Convert(pdf, TargetFormat.Docx));
    }

    [Fact]
    public void Control_characters_from_pdfs_never_break_the_xml()
    {
        var path = files.Path("odd.docx");
        DocxWriter.Write(path, [new DocBlock.Paragraph([new DocRun("bell\u0007 & <tag> \u0001ok")])], "t");
        Assert.Contains("bell &amp; &lt;tag&gt; ok", ReadDocument(path));
        Assert.Equal("bell & <tag> ok", ParagraphTexts(DocumentXml(path)).Single());
    }

    static string ReadDocument(string docx)
    {
        using var zip = ZipFile.OpenRead(docx);
        using var r = new StreamReader(zip.GetEntry("word/document.xml")!.Open());
        return r.ReadToEnd();
    }
}
