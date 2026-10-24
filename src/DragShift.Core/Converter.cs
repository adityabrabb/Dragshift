using ImageMagick;
using UglyToad.PdfPig.Writer;

namespace DragShift.Core;

/// <summary>The outer ring: converts a file to another format, writing the result next to the original.</summary>
public static class Converter
{
    /// <summary>Converts <paramref name="source"/> and returns the file(s) written. The source is never modified.</summary>
    public static IReadOnlyList<string> Convert(string source, TargetFormat target)
    {
        if (!Formats.IsAvailable(target))
            throw new UserFacingException($"Saving {Formats.Label(target)} isn't available on this PC.");
        if (!Formats.CanConvert(source, target))
            throw new UserFacingException($"“{Path.GetFileName(source)}” can't be converted to {Formats.Label(target)}.");

        var kind = Formats.KindOf(source);
        if (kind == FileKind.Archive)
            return [target == TargetFormat.Pdf ? WritePdf(OutputPaths.NextTo(source, ".pdf"), [source]) : Archives.ConvertImages(source, target)];
        if (kind == FileKind.Presentation)
            return ConvertPresentation(source, target);
        if (kind is FileKind.Video or FileKind.Audio || Formats.IsMedia(target))
            return [Media.Convert(source, target)];

        var isPdf = kind == FileKind.Pdf;
        var ext = Formats.Extension(target);

        if (isPdf && target == TargetFormat.Docx)
        {
            // Real text, not pictures of pages: extracted from the PDF's text layer.
            var output = OutputPaths.NextTo(source, ext);
            DocxWriter.Write(output, PdfToDocx.Extract(source), Path.GetFileNameWithoutExtension(source));
            return [output];
        }

        using var frames = ImageIO.Load(source);

        switch (target)
        {
            case TargetFormat.Pdf:
            {
                var output = OutputPaths.NextTo(source, ext);
                File.WriteAllBytes(output, PdfPages(frames));
                return [output];
            }
            case TargetFormat.Docx:
            {
                var output = OutputPaths.NextTo(source, ext);
                var pictures = frames.Select(f =>
                {
                    var (dx, dy) = f.Density.X > 1 ? DpiOf(f.Density) : (96, 96);
                    return new DocBlock.Picture(ImageIO.ToPng(f), (int)f.Width, (int)f.Height, f.Width / dx, f.Height / dy);
                }).ToList();
                DocxWriter.WritePictures(output, pictures, Path.GetFileNameWithoutExtension(source));
                return [output];
            }
            default:
                return SaveImages(source, frames, target, isPdf ? " Page" : null);
        }
    }

    /// <summary>
    /// Slides → PDF, or one picture per slide (or a multi-page TIFF), drawn by PowerPoint/LibreOffice. A .pptx → DOCX
    /// is read directly instead: slide titles, text and pictures, a page per slide.
    /// </summary>
    static IReadOnlyList<string> ConvertPresentation(string source, TargetFormat target)
    {
        var title = Path.GetFileNameWithoutExtension(source);
        if (target == TargetFormat.Docx && Presentations.IsOpenXml(source))
        {
            var output = OutputPaths.NextTo(source, ".docx");
            DocxWriter.Write(output, Presentations.ToDocBlocks(source), title);
            return [output];
        }
        using var temp = new TempFolder();
        var pdf = temp.NewFile(".pdf");
        Presentations.RenderPdf(source, pdf);
        switch (target)
        {
            case TargetFormat.Pdf:
            {
                var output = OutputPaths.NextTo(source, ".pdf");
                File.Copy(pdf, output);
                return [output];
            }
            case TargetFormat.Docx:
            {
                // Old binary .ppt: by way of the PDF.
                var output = OutputPaths.NextTo(source, ".docx");
                DocxWriter.Write(output, PdfToDocx.Extract(pdf), title);
                return [output];
            }
            default:
            {
                using var frames = ImageIO.Load(pdf);
                return SaveImages(source, frames, target, " Slide");
            }
        }
    }

    /// <summary>
    /// Writes frames next to <paramref name="source"/>: one file, a multi-frame file where the format has frames, or
    /// for pages and slides (<paramref name="pageWord"/> set) one file per page, e.g. "Deck Slide 3.png".
    /// </summary>
    static IReadOnlyList<string> SaveImages(string source, MagickImageCollection frames, TargetFormat target, string? pageWord)
    {
        var ext = Formats.Extension(target);
        // A deck as an animated WEBP or GIF would be odd: slides get a file each, unless it's a multi-page TIFF.
        var multiFrame = ImageIO.SupportsMultipleFrames(target) && (pageWord != " Slide" || target == TargetFormat.Tiff);
        if (frames.Count == 1 || multiFrame || pageWord is null)
        {
            var output = OutputPaths.NextTo(source, ext);
            if (frames.Count > 1 && multiFrame) ImageIO.SaveFrames(frames, target, output);
            // Animated GIF → PNG/JPG etc: keep the first frame, like a thumbnail would.
            else ImageIO.SaveRaster(frames[0], target, output);
            return [output];
        }
        // Multi-page PDF → one image per page.
        var outputs = new List<string>();
        for (var i = 0; i < frames.Count; i++)
        {
            var output = OutputPaths.NextTo(source, ext, $"{pageWord} {i + 1}");
            ImageIO.SaveRaster(frames[i], target, output);
            outputs.Add(output);
        }
        return outputs;
    }

    /// <summary>
    /// Several files → one PDF, in natural name order ("2" before "10"). PDFs keep their pages as they are (text stays
    /// text); images become one page per frame; slides are drawn by PowerPoint/LibreOffice; zips add what's inside.
    /// Written next to the first file as "{name} Combined.pdf".
    /// </summary>
    public static string Combine(IReadOnlyList<string> sources)
    {
        var ordered = sources.Where(Formats.Combinable).OrderBy(Path.GetFileName, NaturalComparer.Instance).ToList();
        if (ordered.Count == 0) throw new UserFacingException("There's nothing here to put in a PDF.");
        return WritePdf(OutputPaths.NextTo(ordered[0], ".pdf", " Combined"), ordered);
    }

    static string WritePdf(string output, IReadOnlyList<string> files)
    {
        using var temp = new TempFolder();
        var parts = new List<byte[]>();
        foreach (var file in files) AddPdfParts(file, parts, temp);
        if (parts.Count == 0) throw new UserFacingException("There's nothing here to put in a PDF.");

        byte[] merged;
        try
        {
            merged = parts.Count == 1 ? parts[0] : PdfMerger.Merge(parts);
        }
        catch (Exception e)
        {
            throw new UserFacingException("Couldn't combine these PDFs (one may be password-protected or damaged).", e);
        }
        File.WriteAllBytes(output, merged);
        return output;
    }

    static void AddPdfParts(string file, List<byte[]> parts, TempFolder temp)
    {
        switch (Formats.KindOf(file))
        {
            case FileKind.Pdf:
                parts.Add(File.ReadAllBytes(file));
                break;
            case FileKind.Image:
            {
                using var frames = ImageIO.Load(file);
                parts.Add(PdfPages(frames));
                break;
            }
            case FileKind.Presentation:
            {
                var pdf = temp.NewFile(".pdf");
                Presentations.RenderPdf(file, pdf);
                parts.Add(File.ReadAllBytes(pdf));
                break;
            }
            case FileKind.Archive:
                foreach (var inner in Archives.ExtractForPdf(file, temp)) AddPdfParts(inner, parts, temp);
                break;
        }
    }

    /// <summary>Each frame as a page, on white, at the image's own resolution (144 dpi when it has none).</summary>
    static byte[] PdfPages(MagickImageCollection frames)
    {
        using var pages = new MagickImageCollection();
        foreach (var f in frames)
        {
            var page = f.Clone();
            page.Format = MagickFormat.Pdf;
            if (page.HasAlpha)
            {
                page.BackgroundColor = MagickColors.White;
                page.Alpha(AlphaOption.Remove);
            }
            if (page.Density.X <= 1) page.Density = new Density(144, 144, DensityUnit.PixelsPerInch);
            pages.Add(page);
        }
        return pages.ToByteArray(MagickFormat.Pdf);
    }

    static (double x, double y) DpiOf(Density d) =>
        d.Units == DensityUnit.PixelsPerCentimeter ? (d.X * 2.54, d.Y * 2.54) : (d.X, d.Y);
}

/// <summary>Orders names the way Explorer does: digit runs compare as numbers, case is ignored.</summary>
public sealed class NaturalComparer : IComparer<string?>
{
    public static readonly NaturalComparer Instance = new();

    public int Compare(string? a, string? b)
    {
        if (a is null || b is null) return Comparer<string?>.Default.Compare(a, b);
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            if (char.IsAsciiDigit(a[i]) && char.IsAsciiDigit(b[j]))
            {
                int si = i, sj = j;
                while (i < a.Length && char.IsAsciiDigit(a[i])) i++;
                while (j < b.Length && char.IsAsciiDigit(b[j])) j++;
                var na = a[si..i].TrimStart('0');
                var nb = b[sj..j].TrimStart('0');
                var c = na.Length != nb.Length ? na.Length.CompareTo(nb.Length) : string.CompareOrdinal(na, nb);
                if (c != 0) return c;
                continue;
            }
            var d = char.ToUpperInvariant(a[i]).CompareTo(char.ToUpperInvariant(b[j]));
            if (d != 0) return d;
            i++;
            j++;
        }
        return (a.Length - i).CompareTo(b.Length - j);
    }
}
