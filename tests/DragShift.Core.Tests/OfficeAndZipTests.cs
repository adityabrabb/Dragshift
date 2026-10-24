using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using DragShift.Core;
using ImageMagick;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Writer;

namespace DragShift.Core.Tests;

/// <summary>PowerPoint files (read directly, and drawn through a stand-in for PowerPoint) and zips.</summary>
[Collection("Platform")]
public sealed class OfficeAndZipTests : IDisposable
{
    readonly TestFiles files = new();

    public void Dispose()
    {
        Platform.Pdf = null;
        Platform.Ocr = null;
        Platform.Office = null;
        files.Dispose();
    }

    // ---- Sample files ----

    const string PresentationNs = "http://schemas.openxmlformats.org/presentationml/2006/main";
    const string DrawingNs = "http://schemas.openxmlformats.org/drawingml/2006/main";
    const string RelNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    /// <summary>
    /// A two-slide deck. The slide list puts slide2.xml first, so reading must follow it rather than part names.
    /// slide1 has a title, a bold run, a big photo (PNG) and a table; docProps name an author and a company.
    /// </summary>
    string Deck(string name = "deck.pptx")
    {
        var path = files.Path(name);
        using var zip = new ZipArchive(File.Create(path), ZipArchiveMode.Create);
        void Add(string part, string xml)
        {
            using var w = new StreamWriter(zip.CreateEntry(part).Open(), new UTF8Encoding(false));
            w.Write(xml);
        }
        Add("[Content_Types].xml", """
            <?xml version="1.0" encoding="UTF-8"?>
            <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
              <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
              <Default Extension="xml" ContentType="application/xml"/>
              <Default Extension="png" ContentType="image/png"/>
              <Override PartName="/ppt/presentation.xml" ContentType="application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml"/>
              <Override PartName="/ppt/slides/slide1.xml" ContentType="application/vnd.openxmlformats-officedocument.presentationml.slide+xml"/>
              <Override PartName="/ppt/slides/slide2.xml" ContentType="application/vnd.openxmlformats-officedocument.presentationml.slide+xml"/>
              <Override PartName="/docProps/core.xml" ContentType="application/vnd.openxmlformats-package.core-properties+xml"/>
              <Override PartName="/docProps/app.xml" ContentType="application/vnd.openxmlformats-officedocument.extended-properties+xml"/>
            </Types>
            """);
        Add("_rels/.rels", """
            <?xml version="1.0" encoding="UTF-8"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="ppt/presentation.xml"/>
              <Relationship Id="rId2" Type="http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties" Target="docProps/core.xml"/>
              <Relationship Id="rId3" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/extended-properties" Target="docProps/app.xml"/>
            </Relationships>
            """);
        Add("docProps/core.xml", """
            <?xml version="1.0" encoding="UTF-8"?>
            <cp:coreProperties xmlns:cp="http://schemas.openxmlformats.org/package/2006/metadata/core-properties" xmlns:dc="http://purl.org/dc/elements/1.1/" xmlns:dcterms="http://purl.org/dc/terms/" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
              <dc:title>Quarterly review</dc:title><dc:creator>Jane Doe</dc:creator><cp:lastModifiedBy>John Roe</cp:lastModifiedBy>
              <dcterms:created xsi:type="dcterms:W3CDTF">2024-01-02T03:04:05Z</dcterms:created>
            </cp:coreProperties>
            """);
        Add("docProps/app.xml", """
            <?xml version="1.0" encoding="UTF-8"?>
            <Properties xmlns="http://schemas.openxmlformats.org/officeDocument/2006/extended-properties"><Slides>2</Slides><Company>Acme Corp</Company></Properties>
            """);
        Add("ppt/presentation.xml", $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <p:presentation xmlns:p="{PresentationNs}" xmlns:r="{RelNs}">
              <p:sldIdLst><p:sldId id="256" r:id="rId2"/><p:sldId id="257" r:id="rId1"/></p:sldIdLst>
            </p:presentation>
            """);
        Add("ppt/_rels/presentation.xml.rels", """
            <?xml version="1.0" encoding="UTF-8"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/slide" Target="slides/slide1.xml"/>
              <Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/slide" Target="slides/slide2.xml"/>
            </Relationships>
            """);
        Add("ppt/slides/slide1.xml", $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <p:sld xmlns:p="{PresentationNs}" xmlns:a="{DrawingNs}" xmlns:r="{RelNs}"><p:cSld><p:spTree>
              <p:sp><p:nvSpPr><p:cNvPr id="2" name="Title"/><p:cNvSpPr/><p:nvPr><p:ph type="title"/></p:nvPr></p:nvSpPr>
                <p:txBody><a:p><a:r><a:t>Results</a:t></a:r></a:p></p:txBody></p:sp>
              <p:sp><p:nvSpPr><p:cNvPr id="3" name="Body"/><p:cNvSpPr/><p:nvPr><p:ph idx="1"/></p:nvPr></p:nvSpPr>
                <p:txBody><a:p><a:r><a:rPr b="1"/><a:t>Revenue</a:t></a:r><a:r><a:t> grew 12%</a:t></a:r></a:p><a:p/></p:txBody></p:sp>
              <p:pic><p:nvPicPr><p:cNvPr id="4" name="Photo"/><p:cNvPicPr/><p:nvPr/></p:nvPicPr>
                <p:blipFill><a:blip r:embed="rId5"/></p:blipFill>
                <p:spPr><a:xfrm><a:off x="0" y="0"/><a:ext cx="4572000" cy="3048000"/></a:xfrm></p:spPr></p:pic>
              <p:graphicFrame><a:graphic><a:graphicData><a:tbl>
                <a:tr><a:tc><a:txBody><a:p><a:r><a:t>Q1</a:t></a:r></a:p></a:txBody></a:tc><a:tc><a:txBody><a:p><a:r><a:t>10</a:t></a:r></a:p></a:txBody></a:tc></a:tr>
              </a:tbl></a:graphicData></a:graphic></p:graphicFrame>
            </p:spTree></p:cSld></p:sld>
            """);
        Add("ppt/slides/_rels/slide1.xml.rels", """
            <?xml version="1.0" encoding="UTF-8"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId5" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/image" Target="../media/image1.png"/>
            </Relationships>
            """);
        Add("ppt/slides/slide2.xml", $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <p:sld xmlns:p="{PresentationNs}" xmlns:a="{DrawingNs}"><p:cSld><p:spTree>
              <p:sp><p:nvSpPr><p:cNvPr id="2" name="Title"/><p:cNvSpPr/><p:nvPr><p:ph type="ctrTitle"/></p:nvPr></p:nvSpPr>
                <p:txBody><a:p><a:r><a:t>Opening</a:t></a:r></a:p></p:txBody></p:sp>
            </p:spTree></p:cSld></p:sld>
            """);
        using (var photo = new MagickImage("gradient:#87ceeb-#f5deb3", 3000, 2000))
        {
            photo.AddNoise(NoiseType.Gaussian, 0.4);
            photo.Format = MagickFormat.Png;
            using var s = zip.CreateEntry("ppt/media/image1.png").Open();
            s.Write(photo.ToByteArray());
        }
        return path;
    }

    /// <summary>A zip of the given files under the given entry names, plus a folder entry.</summary>
    string Zip(string name, params (string entry, string file)[] content)
    {
        var path = files.Path(name);
        using var zip = new ZipArchive(File.Create(path), ZipArchiveMode.Create);
        zip.CreateEntry("sub/");
        foreach (var (entry, file) in content) zip.CreateEntryFromFile(file, entry);
        return path;
    }

    string TextPdf(string name, params string[] pages)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(UglyToad.PdfPig.Fonts.Standard14Fonts.Standard14Font.Helvetica);
        foreach (var text in pages)
            builder.AddPage(UglyToad.PdfPig.Content.PageSize.A4).AddText(text, 14, new UglyToad.PdfPig.Core.PdfPoint(72, 700), font);
        var path = files.Path(name);
        File.WriteAllBytes(path, builder.Build());
        return path;
    }

    /// <summary>Stand-in for PowerPoint: "draws" every deck as a PDF with one text page per slide.</summary>
    sealed class FakeOffice(int slides) : IOfficeConverter
    {
        public void ToPdf(string source, string pdfPath)
        {
            var builder = new PdfDocumentBuilder();
            var font = builder.AddStandard14Font(UglyToad.PdfPig.Fonts.Standard14Fonts.Standard14Font.Helvetica);
            for (var i = 1; i <= slides; i++)
                builder.AddPage(UglyToad.PdfPig.Content.PageSize.A4).AddText($"Slide number {i}", 14, new UglyToad.PdfPig.Core.PdfPoint(72, 700), font);
            File.WriteAllBytes(pdfPath, builder.Build());
        }
    }

    static Dictionary<string, byte[]> Entries(string zip)
    {
        using var archive = ZipFile.OpenRead(zip);
        return archive.Entries.ToDictionary(e => e.FullName, e =>
        {
            using var s = e.Open();
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        });
    }

    static XDocument Xml(byte[] bytes) => XDocument.Load(new MemoryStream(bytes));

    // ---- Presentations ----

    [Fact]
    public void Presentations_and_zips_are_recognized()
    {
        Assert.Equal(FileKind.Presentation, Formats.KindOf("a.pptx"));
        Assert.Equal(FileKind.Presentation, Formats.KindOf("a.PPT"));
        Assert.Equal(FileKind.Archive, Formats.KindOf("a.zip"));
        Assert.True(Presentations.IsOpenXml("a.pptx"));
        Assert.False(Presentations.IsOpenXml("a.ppt"));
    }

    [Fact]
    public void Without_PowerPoint_a_pptx_still_gets_docx_text_and_tools()
    {
        var deck = Deck();
        Assert.Null(Formats.WhyNot(deck, TargetFormat.Docx));
        Assert.Equal("Needs PowerPoint or LibreOffice", Formats.WhyNot(deck, TargetFormat.Pdf));
        Assert.Equal("Needs PowerPoint or LibreOffice", Formats.WhyNot(deck, TargetFormat.Png));
        Assert.NotNull(Formats.WhyNot(deck, TargetFormat.Mp4));
        Assert.True(ToolInfo.CanApply(deck, ToolKind.CopyText));
        Assert.True(ToolInfo.CanApply(deck, ToolKind.Compress));
        Assert.True(ToolInfo.CanApply(deck, ToolKind.Metadata));
        Assert.False(ToolInfo.CanApply(deck, ToolKind.Crop));
        Assert.False(Formats.Combinable(deck));
    }

    [Fact]
    public void Pptx_text_follows_the_slide_list()
    {
        var text = TextExtractor.Extract(Deck());
        Assert.Equal("Opening\n\nResults\nRevenue grew 12%\nQ1\t10", text);
    }

    [Fact]
    public void Pptx_to_docx_has_titles_text_and_pictures()
    {
        var output = Assert.Single(Converter.Convert(Deck(), TargetFormat.Docx));
        Assert.Equal("deck.docx", Path.GetFileName(output));
        var parts = Entries(output);
        var body = Encoding.UTF8.GetString(parts["word/document.xml"]);
        Assert.True(body.IndexOf("Opening", StringComparison.Ordinal) < body.IndexOf("Results", StringComparison.Ordinal));
        Assert.Contains("Revenue", body);
        Assert.Contains("Q1  |  10", body);
        Assert.Contains("word/media/image1.png", parts.Keys);
        // The picture keeps its size on the slide: 5" wide, which fits the page as is.
        Assert.Contains("cx=\"4572000\"", body);
    }

    [Fact]
    public void Compressing_a_pptx_shrinks_pictures_and_keeps_them_linked()
    {
        var deck = Deck();
        var output = Tools.RunInstant(deck, ToolKind.Compress);

        Assert.Equal("deck Compressed.pptx", Path.GetFileName(output));
        Assert.True(new FileInfo(output).Length < new FileInfo(deck).Length / 2);
        var parts = Entries(output);
        // The opaque PNG became a JPEG, capped at 2560 px, and the slide and content types point at the new name.
        Assert.DoesNotContain("ppt/media/image1.png", parts.Keys);
        using (var img = new MagickImage(parts["ppt/media/image1.jpeg"]))
        {
            Assert.Equal(MagickFormat.Jpeg, img.Format);
            Assert.Equal(2560u, img.Width);
        }
        Assert.Contains("../media/image1.jpeg", Encoding.UTF8.GetString(parts["ppt/slides/_rels/slide1.xml.rels"]));
        Assert.Contains(Xml(parts["[Content_Types].xml"]).Root!.Elements(), e => (string?)e.Attribute("Extension") == "jpeg");
        // Still a working deck: its picture is found again.
        Assert.Contains("word/media/image1.png", Entries(Assert.Single(Converter.Convert(output, TargetFormat.Docx))).Keys);
    }

    [Fact]
    public void Stripping_pptx_metadata_removes_people_and_keeps_the_title()
    {
        var output = Tools.RunInstant(Deck(), ToolKind.Metadata);

        Assert.Equal("deck Clean.pptx", Path.GetFileName(output));
        var parts = Entries(output);
        var core = Encoding.UTF8.GetString(parts["docProps/core.xml"]);
        Assert.Contains("Quarterly review", core);
        Assert.DoesNotContain("Jane Doe", core);
        Assert.DoesNotContain("John Roe", core);
        Assert.DoesNotContain("2024-01-02", core);
        Assert.DoesNotContain("Acme", Encoding.UTF8.GetString(parts["docProps/app.xml"]));
        Assert.Equal("Opening\n\nResults\nRevenue grew 12%\nQ1\t10", Presentations.Text(output));
    }

    [Fact]
    public void With_PowerPoint_slides_become_a_pdf_or_a_picture_each()
    {
        Platform.Office = new FakeOffice(slides: 2);
        Platform.Pdf = new FakePdf(pages: 2);
        var deck = Deck();

        var pdf = Assert.Single(Converter.Convert(deck, TargetFormat.Pdf));
        Assert.Equal("deck.pdf", Path.GetFileName(pdf));
        using (var doc = PdfDocument.Open(pdf)) Assert.Equal(2, doc.NumberOfPages);

        Assert.Equal(["deck Slide 1.png", "deck Slide 2.png"], Converter.Convert(deck, TargetFormat.Png).Select(Path.GetFileName));
        // Not an animated WEBP: one per slide. TIFF holds them all.
        Assert.Equal(2, Converter.Convert(deck, TargetFormat.Webp).Count);
        Assert.Equal("deck.tiff", Path.GetFileName(Assert.Single(Converter.Convert(deck, TargetFormat.Tiff))));
    }

    [Fact]
    public void Decks_combine_with_images_into_one_pdf()
    {
        Platform.Office = new FakeOffice(slides: 2);
        var deck = Deck("a deck.pptx");
        var photo = files.Photo("b photo.png");

        var output = Converter.Combine([photo, deck]);

        Assert.Equal("a deck Combined.pdf", Path.GetFileName(output));
        using var doc = PdfDocument.Open(output);
        Assert.Equal(3, doc.NumberOfPages);
        Assert.Contains("Slide number 1", doc.GetPage(1).Text);
    }

    // ---- Zips ----

    [Fact]
    public void A_zip_offers_what_its_contents_allow()
    {
        var pictures = Zip("pictures.zip", ("a.png", files.Photo()));
        var notes = Zip("notes.zip", ("notes.txt", WriteText("notes.txt", "hello")));

        Assert.Null(Formats.WhyNot(pictures, TargetFormat.Webp));
        Assert.Null(Formats.WhyNot(pictures, TargetFormat.Png)); // a zip is never "already PNG"
        Assert.Null(Formats.WhyNot(pictures, TargetFormat.Pdf));
        Assert.NotNull(Formats.WhyNot(pictures, TargetFormat.Mp4));
        Assert.True(ToolInfo.CanApply(pictures, ToolKind.Compress));
        Assert.False(ToolInfo.CanApply(pictures, ToolKind.Crop));
        Assert.False(ToolInfo.CanApply(pictures, ToolKind.CopyText)); // no OCR here

        Assert.Equal("No pictures in this zip", Formats.WhyNot(notes, TargetFormat.Webp));
        Assert.NotNull(Formats.WhyNot(notes, TargetFormat.Pdf));
        Assert.False(ToolInfo.CanApply(notes, ToolKind.Compress));
    }

    string WriteText(string name, string text)
    {
        var path = files.Path(name);
        File.WriteAllText(path, text);
        return path;
    }

    [Fact]
    public void Converting_a_zip_converts_the_pictures_and_keeps_the_rest()
    {
        var zip = Zip("holiday.zip",
            ("beach.png", files.Photo()),
            ("sub/sunset.jpg", files.Photo("sunset.jpg")),
            ("sub/beach.jpg", files.Photo("beach.jpg")),
            ("readme.txt", WriteText("readme.txt", "hi")));

        var output = Assert.Single(Converter.Convert(zip, TargetFormat.Webp));

        Assert.Equal("holiday WEBP.zip", Path.GetFileName(output));
        var parts = Entries(output);
        Assert.Equal(["beach.webp", "readme.txt", "sub/", "sub/beach.webp", "sub/sunset.webp"], parts.Keys.Order(StringComparer.Ordinal));
        using (var img = new MagickImage(parts["sub/sunset.webp"])) Assert.Equal(MagickFormat.WebP, img.Format);
        Assert.Equal("hi", Encoding.UTF8.GetString(parts["readme.txt"]));
    }

    [Fact]
    public void Two_pictures_converting_to_one_name_both_survive()
    {
        var zip = Zip("same.zip", ("a.png", files.Photo()), ("a.jpg", files.Photo("a.jpg")));
        var parts = Entries(Assert.Single(Converter.Convert(zip, TargetFormat.Webp)));
        Assert.Contains("a.webp", parts.Keys);
        Assert.Contains("a (2).webp", parts.Keys);
    }

    [Fact]
    public void Entry_names_cant_write_outside_the_zip()
    {
        var zip = Zip("sneaky.zip", ("../../escape.png", files.Photo()));
        var output = Assert.Single(Converter.Convert(zip, TargetFormat.Jpg));
        Assert.Contains("../../escape.jpg", Entries(output).Keys);
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(files.Dir)!, "escape.jpg")));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(files.Dir)!)!, "escape.jpg")));
    }

    [Fact]
    public void A_zip_on_pdf_becomes_one_pdf_of_its_contents_in_name_order()
    {
        var zip = Zip("scans.zip",
            ("scan 10.png", files.Photo("x.png")),
            ("scan 2.pdf", TextPdf("y.pdf", "Second scan text", "More text here")),
            ("scan 1.jpg", files.Photo("z.jpg")));

        var output = Assert.Single(Converter.Convert(zip, TargetFormat.Pdf));

        Assert.Equal("scans.pdf", Path.GetFileName(output));
        using var doc = PdfDocument.Open(output);
        Assert.Equal(4, doc.NumberOfPages);
        Assert.Empty(doc.GetPage(1).GetWords());
        Assert.Contains("Second scan", doc.GetPage(2).Text);
    }

    [Fact]
    public void Copy_text_reads_the_pdfs_and_decks_in_a_zip()
    {
        var zip = Zip("docs.zip", ("a.pdf", TextPdf("a.pdf", "Alpha page words here")), ("b.pptx", Deck()));
        Assert.True(ToolInfo.CanApply(zip, ToolKind.CopyText));
        var text = TextExtractor.Extract(zip);
        Assert.StartsWith("Alpha page words here", text);
        Assert.Contains("Revenue grew 12%", text);
    }

    [Fact]
    public void Compress_and_metadata_work_inside_a_zip()
    {
        using (var tagged = new MagickImage(files.Photo("big.jpg", 3200, 2400)))
        {
            var exif = new ExifProfile();
            exif.SetValue(ExifTag.Make, "TestCam");
            tagged.SetProfile(exif);
            tagged.Quality = 98;
            tagged.Write(files.Path("big.jpg"));
        }
        var zip = Zip("camera.zip", ("big.jpg", files.Path("big.jpg")));

        var compressed = Tools.RunInstant(zip, ToolKind.Compress);
        Assert.Equal("camera Compressed.zip", Path.GetFileName(compressed));
        Assert.True(new FileInfo(compressed).Length < new FileInfo(zip).Length);

        var clean = Tools.RunInstant(zip, ToolKind.Metadata);
        Assert.Equal("camera Clean.zip", Path.GetFileName(clean));
        using var img = new MagickImage(Entries(clean)["big.jpg"]);
        Assert.Null(img.GetExifProfile());
    }
}
