using ImageMagick;

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

        var isPdf = Formats.KindOf(source) == FileKind.Pdf;
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
                pages.Write(output, MagickFormat.Pdf);
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

    static (double x, double y) DpiOf(Density d) =>
        d.Units == DensityUnit.PixelsPerCentimeter ? (d.X * 2.54, d.Y * 2.54) : (d.X, d.Y);
}
