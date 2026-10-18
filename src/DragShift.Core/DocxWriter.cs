using System.IO.Compression;
using System.Security;
using System.Text;

namespace DragShift.Core;

/// <summary>Minimal Word document writer: one picture per page, scaled to fit the page margins.</summary>
public static class DocxWriter
{
    const long EmuPerInch = 914400;
    // US Letter with 1" margins.
    const double MaxWidthIn = 6.5, MaxHeightIn = 9.0;

    public sealed record Picture(byte[] Png, int PixelWidth, int PixelHeight, double DpiX = 96, double DpiY = 96);

    public static void Write(string path, IReadOnlyList<Picture> pictures, string title)
    {
        if (pictures.Count == 0) throw new ArgumentException("No pictures", nameof(pictures));
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
              <dc:title>{SecurityElement.Escape(title)}</dc:title>
              <dc:creator>DragShift</dc:creator>
            </cp:coreProperties>
            """);

        var rels = new StringBuilder();
        var body = new StringBuilder();
        for (var i = 0; i < pictures.Count; i++)
        {
            var p = pictures[i];
            var n = i + 1;
            using (var s = zip.CreateEntry($"word/media/image{n}.png", CompressionLevel.NoCompression).Open())
                s.Write(p.Png);
            rels.Append($"""<Relationship Id="rImg{n}" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/image" Target="media/image{n}.png"/>""");

            var (cx, cy) = FitEmu(p);
            var pageBreak = i < pictures.Count - 1 ? """<w:r><w:br w:type="page"/></w:r>""" : "";
            body.Append($"""
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
                </w:drawing></w:r>{pageBreak}</w:p>
                """);
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

    /// <summary>Picture size in EMUs: its physical size at its DPI, shrunk (never enlarged) to fit the page.</summary>
    internal static (long cx, long cy) FitEmu(Picture p)
    {
        var wIn = p.PixelWidth / (p.DpiX > 0 ? p.DpiX : 96);
        var hIn = p.PixelHeight / (p.DpiY > 0 ? p.DpiY : 96);
        var k = Math.Min(1, Math.Min(MaxWidthIn / wIn, MaxHeightIn / hIn));
        return ((long)Math.Round(wIn * k * EmuPerInch), (long)Math.Round(hIn * k * EmuPerInch));
    }

    static void Add(ZipArchive zip, string name, string content)
    {
        using var w = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
        w.Write(content.TrimStart());
    }
}
