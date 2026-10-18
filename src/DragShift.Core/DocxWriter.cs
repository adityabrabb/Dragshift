using System.IO.Compression;
using System.Security;
using System.Text;

namespace DragShift.Core;

/// <summary>A run of text with one look.</summary>
public sealed record DocRun(string Text, double? SizePt = null, bool Bold = false, bool Italic = false);

/// <summary>Content of a generated Word document, top to bottom.</summary>
public abstract record DocBlock
{
    public sealed record Paragraph(IReadOnlyList<DocRun> Runs, bool Centered = false) : DocBlock;

    /// <summary>A PNG. Shown at its physical size (from <paramref name="WidthIn"/>/<paramref name="HeightIn"/>, or pixels at 96 DPI), shrunk to fit the page.</summary>
    public sealed record Picture(byte[] Png, int PixelWidth, int PixelHeight, double? WidthIn = null, double? HeightIn = null) : DocBlock;

    public sealed record PageBreak : DocBlock;
}

/// <summary>Minimal Word (.docx) writer: paragraphs of text runs, pictures and page breaks on US Letter pages.</summary>
public static class DocxWriter
{
    const long EmuPerInch = 914400;
    // US Letter with 1" margins.
    const double MaxWidthIn = 6.5, MaxHeightIn = 9.0;

    /// <summary>One picture per page (images and scanned pages).</summary>
    public static void WritePictures(string path, IReadOnlyList<DocBlock.Picture> pictures, string title)
    {
        if (pictures.Count == 0) throw new ArgumentException("No pictures", nameof(pictures));
        var blocks = new List<DocBlock>();
        foreach (var p in pictures)
        {
            if (blocks.Count > 0) blocks.Add(new DocBlock.PageBreak());
            blocks.Add(p);
        }
        Write(path, blocks, title);
    }

    public static void Write(string path, IReadOnlyList<DocBlock> blocks, string title)
    {
        using var fs = File.Create(path);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);

        Add(zip, "[Content_Types].xml", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
              <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
              <Default Extension="xml" ContentType="application/xml"/>
              <Default Extension="png" ContentType="image/png"/>
              <Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/>
              <Override PartName="/docProps/core.xml" ContentType="application/vnd.openxmlformats-package.core-properties+xml"/>
            </Types>
            """);
        Add(zip, "_rels/.rels", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/>
              <Relationship Id="rId2" Type="http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties" Target="docProps/core.xml"/>
            </Relationships>
            """);
        Add(zip, "docProps/core.xml", $"""
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <cp:coreProperties xmlns:cp="http://schemas.openxmlformats.org/package/2006/metadata/core-properties" xmlns:dc="http://purl.org/dc/elements/1.1/">
              <dc:title>{Escape(title)}</dc:title>
              <dc:creator>DragShift</dc:creator>
            </cp:coreProperties>
            """);

        var rels = new StringBuilder();
        var body = new StringBuilder();
        var pictureCount = 0;
        foreach (var block in blocks)
        {
            switch (block)
            {
                case DocBlock.Paragraph p:
                    body.Append(ParagraphXml(p));
                    break;
                case DocBlock.PageBreak:
                    body.Append("""<w:p><w:r><w:br w:type="page"/></w:r></w:p>""");
                    break;
                case DocBlock.Picture pic:
                {
                    var n = ++pictureCount;
                    using (var s = zip.CreateEntry($"word/media/image{n}.png", CompressionLevel.NoCompression).Open())
                        s.Write(pic.Png);
                    rels.Append($"""<Relationship Id="rImg{n}" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/image" Target="media/image{n}.png"/>""");
                    body.Append(PictureXml(pic, n));
                    break;
                }
            }
        }

        Add(zip, "word/_rels/document.xml.rels", $"""
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">{rels}</Relationships>
            """);
        Add(zip, "word/document.xml", $"""
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"
                        xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"
                        xmlns:wp="http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing">
              <w:body>{body}
                <w:sectPr><w:pgSz w:w="12240" w:h="15840"/><w:pgMar w:top="1440" w:right="1440" w:bottom="1440" w:left="1440" w:header="720" w:footer="720" w:gutter="0"/></w:sectPr>
              </w:body>
            </w:document>
            """);
    }

    static string ParagraphXml(DocBlock.Paragraph p)
    {
        var sb = new StringBuilder("<w:p>");
        sb.Append(p.Centered ? """<w:pPr><w:spacing w:after="120"/><w:jc w:val="center"/></w:pPr>""" : """<w:pPr><w:spacing w:after="120"/></w:pPr>""");
        foreach (var run in p.Runs)
        {
            if (run.Text.Length == 0) continue;
            sb.Append("<w:r><w:rPr>");
            if (run.Bold) sb.Append("<w:b/>");
            if (run.Italic) sb.Append("<w:i/>");
            if (run.SizePt is { } pt)
            {
                var half = (int)Math.Clamp(Math.Round(pt * 2), 2, 3276);
                sb.Append($"""<w:sz w:val="{half}"/><w:szCs w:val="{half}"/>""");
            }
            sb.Append($"""</w:rPr><w:t xml:space="preserve">{Escape(run.Text)}</w:t></w:r>""");
        }
        return sb.Append("</w:p>").ToString();
    }

    static string PictureXml(DocBlock.Picture pic, int n)
    {
        var (cx, cy) = FitEmu(pic);
        return $"""
            <w:p><w:pPr><w:jc w:val="center"/></w:pPr><w:r><w:drawing>
              <wp:inline distT="0" distB="0" distL="0" distR="0">
                <wp:extent cx="{cx}" cy="{cy}"/>
                <wp:docPr id="{n}" name="Picture {n}"/>
                <a:graphic xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main">
                  <a:graphicData uri="http://schemas.openxmlformats.org/drawingml/2006/picture">
                    <pic:pic xmlns:pic="http://schemas.openxmlformats.org/drawingml/2006/picture">
                      <pic:nvPicPr><pic:cNvPr id="{n}" name="image{n}.png"/><pic:cNvPicPr/></pic:nvPicPr>
                      <pic:blipFill><a:blip r:embed="rImg{n}"/><a:stretch><a:fillRect/></a:stretch></pic:blipFill>
                      <pic:spPr><a:xfrm><a:off x="0" y="0"/><a:ext cx="{cx}" cy="{cy}"/></a:xfrm><a:prstGeom prst="rect"><a:avLst/></a:prstGeom></pic:spPr>
                    </pic:pic>
                  </a:graphicData>
                </a:graphic>
              </wp:inline>
            </w:drawing></w:r></w:p>
            """;
    }

    /// <summary>Picture size in EMUs: its physical size, shrunk (never enlarged) to fit the page.</summary>
    internal static (long cx, long cy) FitEmu(DocBlock.Picture p)
    {
        var wIn = p.WidthIn is > 0 ? p.WidthIn.Value : p.PixelWidth / 96.0;
        var hIn = p.HeightIn is > 0 ? p.HeightIn.Value : p.PixelHeight / 96.0;
        var k = Math.Min(1, Math.Min(MaxWidthIn / wIn, MaxHeightIn / hIn));
        return ((long)Math.Round(wIn * k * EmuPerInch), (long)Math.Round(hIn * k * EmuPerInch));
    }

    /// <summary>XML-escapes text and drops characters XML can't contain (PDF text often carries control codes).</summary>
    internal static string Escape(string s)
    {
        var clean = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (c is '\t' or '\n' or '\r' || (c >= 0x20 && c != 0xFFFE && c != 0xFFFF)) clean.Append(c);
        }
        return SecurityElement.Escape(clean.ToString()) ?? "";
    }

    static void Add(ZipArchive zip, string name, string content)
    {
        using var w = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
        w.Write(content.TrimStart());
    }
}
