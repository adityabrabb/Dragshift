using System.IO.Compression;
using System.Xml.Linq;
using ImageMagick;

namespace DragShift.Core;

/// <summary>
/// PowerPoint files. A .pptx is a zip of XML parts, so its text, pictures and metadata are read and rewritten here
/// directly; drawing the slides (PDF, pictures) is left to <see cref="Platform.Office"/>.
/// </summary>
public static class Presentations
{
    static readonly XNamespace P = "http://schemas.openxmlformats.org/presentationml/2006/main";
    static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    static readonly XNamespace Rels = "http://schemas.openxmlformats.org/package/2006/relationships";
    static readonly XNamespace Mc = "http://schemas.openxmlformats.org/markup-compatibility/2006";
    static readonly XNamespace Types = "http://schemas.openxmlformats.org/package/2006/content-types";

    const long EmuPerInch = 914400;

    /// <summary>The zip-based formats (.pptx and friends), as opposed to the old binary .ppt.</summary>
    public static bool IsOpenXml(string path) =>
        Formats.KindOf(path) == FileKind.Presentation && Path.GetExtension(path).ToLowerInvariant() is not (".ppt" or ".pps");

    // ---- Rendering ----

    /// <summary>Saves the slides as a PDF at <paramref name="pdfPath"/> with PowerPoint or LibreOffice.</summary>
    public static void RenderPdf(string source, string pdfPath)
    {
        var office = Platform.Office ?? throw new UserFacingException("Showing slides needs PowerPoint or LibreOffice.");
        try
        {
            office.ToPdf(source, pdfPath);
        }
        catch (Exception e) when (e is not UserFacingException)
        {
            throw new UserFacingException($"Couldn't open “{Path.GetFileName(source)}” (it may be password-protected or damaged).", e);
        }
        if (!File.Exists(pdfPath)) throw new UserFacingException($"“{Path.GetFileName(source)}” didn't produce any slides.");
    }

    // ---- Reading ----

    /// <summary>Something on a slide, top to bottom in the order PowerPoint draws it.</summary>
    abstract record Item
    {
        public sealed record Text(IReadOnlyList<DocRun> Runs, bool Title) : Item;
        public sealed record Row(IReadOnlyList<string> Cells) : Item;
        public sealed record Picture(string Part, long Cx, long Cy) : Item;
    }

    /// <summary>All the text, slide by slide, paragraphs on their own lines. Table cells are separated by tabs.</summary>
    public static string Text(string path)
    {
        var slides = Read(path, out _);
        return string.Join("\n\n", slides.Select(items => string.Join("\n", items.Select(i => i switch
        {
            Item.Text t => string.Concat(t.Runs.Select(r => r.Text)),
            Item.Row r => string.Join("\t", r.Cells),
            _ => "",
        }).Where(s => s.Length > 0))).Where(s => s.Length > 0)).Trim();
    }

    /// <summary>Word content: each slide's title as a heading, its text as paragraphs and its pictures, a page per slide.</summary>
    public static IReadOnlyList<DocBlock> ToDocBlocks(string path)
    {
        var slides = Read(path, out var media);
        var blocks = new List<DocBlock>();
        foreach (var items in slides)
        {
            if (blocks.Count > 0) blocks.Add(new DocBlock.PageBreak());
            foreach (var item in items)
            {
                switch (item)
                {
                    case Item.Text { Title: true } t:
                        blocks.Add(new DocBlock.Paragraph([new DocRun(string.Concat(t.Runs.Select(r => r.Text)), 20, Bold: true)]));
                        break;
                    case Item.Text t:
                        blocks.Add(new DocBlock.Paragraph(t.Runs.Select(r => r with { Text = r.Text.Replace('\n', ' ') }).ToList()));
                        break;
                    case Item.Row r:
                        blocks.Add(new DocBlock.Paragraph([new DocRun(string.Join("  |  ", r.Cells))]));
                        break;
                    case Item.Picture pic when media.TryGetValue(pic.Part, out var bytes) && ToPng(bytes) is { } png:
                        blocks.Add(new DocBlock.Picture(png.bytes, png.w, png.h,
                            pic.Cx > 0 ? (double)pic.Cx / EmuPerInch : null, pic.Cy > 0 ? (double)pic.Cy / EmuPerInch : null));
                        break;
                }
            }
        }
        if (blocks.All(b => b is DocBlock.PageBreak))
            throw new UserFacingException($"“{Path.GetFileName(path)}” has no text or pictures to put in a document.");
        return blocks;
    }

    static (byte[] bytes, int w, int h)? ToPng(byte[] bytes)
    {
        try
        {
            using var img = new MagickImage(bytes);
            return (ImageIO.ToPng(img), (int)img.Width, (int)img.Height);
        }
        catch (MagickException)
        {
            return null; // e.g. EMF/WMF clip art: skipped rather than failing the whole document
        }
    }

    /// <summary>Slides in presentation order, and the bytes of every picture they show (by part name).</summary>
    static List<List<Item>> Read(string path, out Dictionary<string, byte[]> media)
    {
        if (!IsOpenXml(path)) throw new UserFacingException($"Save “{Path.GetFileName(path)}” as .pptx first.");
        using var zip = Open(path);
        var slides = new List<List<Item>>();
        media = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var slidePart in SlideParts(zip))
        {
            var slide = LoadXml(zip, slidePart);
            if (slide is null) continue;
            var rels = Relationships(zip, slidePart);
            var items = new List<Item>();
            if (slide.Root?.Element(P + "cSld")?.Element(P + "spTree") is { } tree) Walk(tree, rels, items);
            foreach (var pic in items.OfType<Item.Picture>())
            {
                if (!media.ContainsKey(pic.Part) && zip.GetEntry(pic.Part) is { } entry) media[pic.Part] = ReadAll(entry);
            }
            slides.Add(items);
        }
        return slides;
    }

    static void Walk(XElement container, IReadOnlyDictionary<string, string> rels, List<Item> items)
    {
        foreach (var el in container.Elements())
        {
            switch (el.Name.LocalName)
            {
                case "sp":
                {
                    var ph = el.Element(P + "nvSpPr")?.Element(P + "nvPr")?.Element(P + "ph")?.Attribute("type")?.Value;
                    var title = ph is "title" or "ctrTitle";
                    foreach (var para in el.Element(P + "txBody")?.Elements(A + "p") ?? [])
                    {
                        var runs = Runs(para);
                        if (runs.Count > 0) items.Add(new Item.Text(runs, title));
                    }
                    break;
                }
                case "pic":
                {
                    var embed = el.Element(P + "blipFill")?.Element(A + "blip")?.Attribute(R + "embed")?.Value;
                    var ext = el.Element(P + "spPr")?.Element(A + "xfrm")?.Element(A + "ext");
                    if (embed is not null && rels.TryGetValue(embed, out var part))
                        items.Add(new Item.Picture(part, Long(ext?.Attribute("cx")), Long(ext?.Attribute("cy"))));
                    break;
                }
                case "graphicFrame":
                    foreach (var row in el.Descendants(A + "tr"))
                    {
                        var cells = row.Elements(A + "tc").Select(tc => string.Join(" ",
                            tc.Descendants(A + "p").Select(p => string.Concat(Runs(p).Select(r => r.Text))).Where(s => s.Length > 0))).ToList();
                        if (cells.Any(c => c.Length > 0)) items.Add(new Item.Row(cells));
                    }
                    break;
                case "grpSp":
                    Walk(el, rels, items);
                    break;
                case "AlternateContent" when el.Name.Namespace == Mc:
                    // The fallback is what older readers draw; it holds the same shapes as the newer choice.
                    if ((el.Element(Mc + "Fallback") ?? el.Element(Mc + "Choice")) is { } branch) Walk(branch, rels, items);
                    break;
            }
        }
    }

    static List<DocRun> Runs(XElement paragraph)
    {
        var runs = new List<DocRun>();
        foreach (var el in paragraph.Elements())
        {
            switch (el.Name.LocalName)
            {
                case "r" or "fld":
                    var text = el.Element(A + "t")?.Value ?? "";
                    if (text.Length == 0) continue;
                    var rPr = el.Element(A + "rPr");
                    runs.Add(new DocRun(text, Bold: rPr?.Attribute("b")?.Value is "1" or "true", Italic: rPr?.Attribute("i")?.Value is "1" or "true"));
                    break;
                case "br":
                    runs.Add(new DocRun("\n"));
                    break;
            }
        }
        return runs.All(r => string.IsNullOrWhiteSpace(r.Text)) ? [] : runs;
    }

    static long Long(XAttribute? a) => long.TryParse(a?.Value, out var v) ? v : 0;

    /// <summary>Slide part names in the order of the presentation's slide list.</summary>
    static IEnumerable<string> SlideParts(ZipArchive zip)
    {
        const string main = "ppt/presentation.xml";
        var rels = Relationships(zip, main);
        var ids = LoadXml(zip, main)?.Root?.Element(P + "sldIdLst")?.Elements(P + "sldId")
            .Select(s => s.Attribute(R + "id")?.Value).OfType<string>() ?? [];
        foreach (var id in ids)
        {
            if (rels.TryGetValue(id, out var part)) yield return part;
        }
    }

    /// <summary>Relationship id → resolved part name, for a part (from its _rels/*.rels file).</summary>
    static Dictionary<string, string> Relationships(ZipArchive zip, string part)
    {
        var dir = Path.GetDirectoryName(part)?.Replace('\\', '/') ?? "";
        var relsPart = (dir.Length > 0 ? dir + "/" : "") + "_rels/" + Path.GetFileName(part) + ".rels";
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var rel in LoadXml(zip, relsPart)?.Root?.Elements(Rels + "Relationship") ?? [])
        {
            if (rel.Attribute("TargetMode")?.Value == "External") continue;
            if (rel.Attribute("Id")?.Value is { } id && rel.Attribute("Target")?.Value is { } target) map[id] = Resolve(dir, target);
        }
        return map;
    }

    internal static string Resolve(string dir, string target)
    {
        var parts = target.StartsWith('/') ? new List<string>() : dir.Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
        foreach (var seg in Uri.UnescapeDataString(target).Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (seg == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); }
            else if (seg != ".") parts.Add(seg);
        }
        return string.Join('/', parts);
    }

    // ---- Compress ----

    /// <summary>
    /// Smaller .pptx: pictures over 2560 px are scaled down and re-encoded (opaque PNG/BMP/TIFF become JPEG), and every
    /// part is recompressed. Slides, layouts and animations are untouched.
    /// </summary>
    public static string Compress(string source)
    {
        var output = Rewrite(source, " Compressed", CompressionLevel.SmallestSize, (name, bytes) =>
        {
            if (!IsMediaPicture(name)) return null;
            using var img = TryLoad(bytes);
            if (img is null) return null;
            img.Strip();
            if (Math.Max(img.Width, img.Height) > Tools.CompressMaxSide)
                img.Resize(new MagickGeometry(Tools.CompressMaxSide, Tools.CompressMaxSide) { Greater = true });
            var isJpeg = img.Format == MagickFormat.Jpeg;
            var transparent = img.HasAlpha && !img.IsOpaque;
            string ext;
            if (isJpeg || !transparent)
            {
                ext = isJpeg ? Path.GetExtension(name) : ".jpeg";
                img.Format = MagickFormat.Jpeg;
                img.Quality = isJpeg ? 78u : 82u;
            }
            else
            {
                if (img.Format != MagickFormat.Png) return null; // transparent BMP/TIFF: rare, left as they are
                ext = Path.GetExtension(name);
                img.Quantize(new QuantizeSettings { Colors = 256, DitherMethod = DitherMethod.FloydSteinberg });
                img.Settings.SetDefine(MagickFormat.Png, "compression-level", "9");
            }
            var smaller = img.ToByteArray();
            return smaller.Length < bytes.Length ? (Path.ChangeExtension(name, ext), smaller) : null;
        });
        if (new FileInfo(output).Length >= new FileInfo(source).Length)
        {
            File.Delete(output);
            throw new UserFacingException($"“{Path.GetFileName(source)}” is already as small as it gets.");
        }
        return output;
    }

    static bool IsMediaPicture(string name) => name.StartsWith("ppt/media/", StringComparison.OrdinalIgnoreCase) &&
        Path.GetExtension(name).ToLowerInvariant() is ".jpg" or ".jpeg" or ".png" or ".bmp" or ".tif" or ".tiff";

    static MagickImage? TryLoad(byte[] bytes)
    {
        try
        {
            return new MagickImage(bytes);
        }
        catch (MagickException)
        {
            return null;
        }
    }

    // ---- Metadata ----

    /// <summary>
    /// Removes the author, last editor, company, dates, keywords and comment authors' names, plus EXIF/GPS from the
    /// pictures. The title stays.
    /// </summary>
    public static string StripMetadata(string source) => Rewrite(source, " Clean", CompressionLevel.Optimal, (name, bytes) =>
    {
        switch (name)
        {
            case "docProps/core.xml":
                return (name, EditXml(bytes, doc =>
                {
                    foreach (var el in doc.Root?.Elements().Where(e => e.Name.LocalName != "title").ToList() ?? []) el.Remove();
                }));
            case "docProps/app.xml":
                return (name, EditXml(bytes, doc =>
                {
                    foreach (var el in doc.Root?.Elements().Where(e => e.Name.LocalName is "Company" or "Manager" or "HyperlinkBase").ToList() ?? []) el.Remove();
                }));
            case "ppt/commentAuthors.xml" or "ppt/authors.xml":
                return (name, EditXml(bytes, doc =>
                {
                    foreach (var author in doc.Descendants().Where(e => e.Name.LocalName is "cmAuthor" or "author"))
                    {
                        author.SetAttributeValue("name", "Author");
                        author.SetAttributeValue("initials", "A");
                        if (author.Attribute("userId") is { } user) user.Value = "";
                        if (author.Attribute("providerId") is { } provider) provider.Value = "";
                    }
                }));
        }
        if (!IsMediaPicture(name)) return null;
        using var img = TryLoad(bytes);
        if (img is null || (img.GetExifProfile() is null && img.GetXmpProfile() is null && img.GetIptcProfile() is null)) return null;
        // Pixels stay as stored: PowerPoint lays pictures out without EXIF orientation.
        var quality = img.Format == MagickFormat.Jpeg && img.Quality is > 0 and <= 100 ? img.Quality : 92;
        img.Strip();
        if (img.Format == MagickFormat.Jpeg) img.Quality = quality;
        return (name, img.ToByteArray());
    });

    static byte[] EditXml(byte[] bytes, Action<XDocument> edit)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Load(new MemoryStream(bytes));
        }
        catch (System.Xml.XmlException)
        {
            return bytes;
        }
        edit(doc);
        using var ms = new MemoryStream();
        doc.Save(ms, SaveOptions.DisableFormatting);
        return ms.ToArray();
    }

    // ---- Rewriting the package ----

    /// <summary>
    /// Copies the package next to the source with <paramref name="suffix"/>, letting <paramref name="change"/> replace
    /// any part (return null to keep it). A part may be renamed (picture.png → picture.jpeg); the references to it
    /// and the content types are updated to match.
    /// </summary>
    static string Rewrite(string source, string suffix, CompressionLevel level, Func<string, byte[], (string name, byte[] bytes)?> change)
    {
        if (!IsOpenXml(source)) throw new UserFacingException($"Save “{Path.GetFileName(source)}” as .pptx first.");
        var output = OutputPaths.NextTo(source, Path.GetExtension(source), suffix);
        try
        {
            using var zip = Open(source);
            var parts = zip.Entries.Where(e => e.Name.Length > 0).Select(e => (entry: e, name: e.FullName, bytes: ReadAll(e))).ToList();
            var names = new HashSet<string>(parts.Select(p => p.name), StringComparer.OrdinalIgnoreCase);
            var renamed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < parts.Count; i++)
            {
                if (change(parts[i].name, parts[i].bytes) is not { } replaced) continue;
                var newName = replaced.name;
                if (!newName.Equals(parts[i].name, StringComparison.OrdinalIgnoreCase))
                {
                    for (var n = 2; names.Contains(newName); n++)
                        newName = Path.ChangeExtension(parts[i].name, null) + $"_{n}" + Path.GetExtension(replaced.name);
                    names.Add(newName);
                    renamed[parts[i].name] = newName;
                }
                parts[i] = (parts[i].entry, newName, replaced.bytes);
            }
            if (renamed.Count > 0)
            {
                for (var i = 0; i < parts.Count; i++)
                {
                    var name = parts[i].name;
                    if (name.EndsWith(".rels", StringComparison.OrdinalIgnoreCase))
                        parts[i] = parts[i] with { bytes = RetargetRels(name, parts[i].bytes, renamed) };
                    else if (name == "[Content_Types].xml")
                        parts[i] = parts[i] with { bytes = UpdateContentTypes(parts[i].bytes, renamed) };
                }
            }

            using var fs = File.Create(output);
            using var outZip = new ZipArchive(fs, ZipArchiveMode.Create);
            foreach (var (entry, name, bytes) in parts)
            {
                var e = outZip.CreateEntry(name, level);
                e.LastWriteTime = entry.LastWriteTime;
                using var s = e.Open();
                s.Write(bytes);
            }
        }
        catch
        {
            try { File.Delete(output); } catch { /* best effort */ }
            throw;
        }
        return output;
    }

    static byte[] RetargetRels(string relsName, byte[] bytes, IReadOnlyDictionary<string, string> renamed) => EditXml(bytes, doc =>
    {
        // "ppt/slides/_rels/slide1.xml.rels" describes "ppt/slides/slide1.xml".
        var dir = Path.GetDirectoryName(Path.GetDirectoryName(relsName))?.Replace('\\', '/') ?? "";
        foreach (var rel in doc.Root?.Elements(Rels + "Relationship") ?? [])
        {
            if (rel.Attribute("TargetMode")?.Value == "External" || rel.Attribute("Target") is not { } target) continue;
            if (!renamed.TryGetValue(Resolve(dir, target.Value), out var newPart)) continue;
            var slash = target.Value.LastIndexOf('/');
            target.Value = target.Value[..(slash + 1)] + Path.GetFileName(newPart);
        }
    });

    static byte[] UpdateContentTypes(byte[] bytes, IReadOnlyDictionary<string, string> renamed) => EditXml(bytes, doc =>
    {
        if (doc.Root is not { } root) return;
        foreach (var o in root.Elements(Types + "Override"))
        {
            if (o.Attribute("PartName") is { } pn && renamed.TryGetValue(pn.Value.TrimStart('/'), out var newPart)) pn.Value = "/" + newPart;
        }
        foreach (var ext in renamed.Values.Select(n => Path.GetExtension(n).TrimStart('.').ToLowerInvariant()).Distinct())
        {
            if (root.Elements(Types + "Default").Any(d => string.Equals(d.Attribute("Extension")?.Value, ext, StringComparison.OrdinalIgnoreCase))) continue;
            var type = ext is "jpg" or "jpeg" ? "image/jpeg" : "image/" + ext;
            root.AddFirst(new XElement(Types + "Default", new XAttribute("Extension", ext), new XAttribute("ContentType", type)));
        }
    });

    // ---- Zip helpers ----

    static ZipArchive Open(string path)
    {
        try
        {
            return ZipFile.OpenRead(path);
        }
        catch (InvalidDataException e)
        {
            throw new UserFacingException($"Couldn't read “{Path.GetFileName(path)}” (it may be password-protected or damaged).", e);
        }
    }

    static XDocument? LoadXml(ZipArchive zip, string part)
    {
        if (zip.GetEntry(part) is not { } entry) return null;
        try
        {
            using var s = entry.Open();
            return XDocument.Load(s);
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }

    static byte[] ReadAll(ZipArchiveEntry entry)
    {
        using var s = entry.Open();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }
}
