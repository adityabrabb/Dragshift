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
            {
                if (frames.Count == 1)
                {
                    var output = OutputPaths.NextTo(source, ext);
                    ImageIO.SaveRaster(frames[0], target, output);
                    return [output];
                }
                if (ImageIO.SupportsMultipleFrames(target))
                {
                    var output = OutputPaths.NextTo(source, ext);
                    ImageIO.SaveFrames(frames, target, output);
                    return [output];
                }
                if (!isPdf)
                {
                    // Animated GIF → PNG/JPG etc: keep the first frame, like a thumbnail would.
                    var output = OutputPaths.NextTo(source, ext);
                    ImageIO.SaveRaster(frames[0], target, output);
                    return [output];
                }
                // Multi-page PDF → one image per page.
                var outputs = new List<string>();
                for (var i = 0; i < frames.Count; i++)
                {
                    var output = OutputPaths.NextTo(source, ext, $" Page {i + 1}");
                    ImageIO.SaveRaster(frames[i], target, output);
                    outputs.Add(output);
                }
                return outputs;
            }
        }
    }

    /// <summary>
    /// Several files → one PDF, in natural name order ("2" before "10"). PDFs keep their pages as they are (text stays
    /// text); images become one page per frame. Written next to the first file as "{name} Combined.pdf".
    /// </summary>
    public static string Combine(IReadOnlyList<string> sources)
    {
        var ordered = sources.Where(s => Formats.KindOf(s) is FileKind.Image or FileKind.Pdf)
            .OrderBy(Path.GetFileName, NaturalComparer.Instance).ToList();
        if (ordered.Count == 0) throw new UserFacingException("There's nothing here to put in a PDF.");

        var parts = new List<byte[]>();
        foreach (var file in ordered)
        {
            if (Formats.KindOf(file) == FileKind.Pdf)
            {
                parts.Add(File.ReadAllBytes(file));
                continue;
            }
            using var frames = ImageIO.Load(file);
            parts.Add(PdfPages(frames));
        }

        byte[] merged;
        try
        {
            merged = parts.Count == 1 ? parts[0] : PdfMerger.Merge(parts);
        }
        catch (Exception e)
        {
            throw new UserFacingException("Couldn't combine these PDFs (one may be password-protected or damaged).", e);
        }
        var output = OutputPaths.NextTo(ordered[0], ".pdf", " Combined");
        File.WriteAllBytes(output, merged);
        return output;
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
