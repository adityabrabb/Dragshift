using ImageMagick;
using ImageMagick.Drawing;

namespace DragShift.Core;

/// <summary>Tools on the inner ring of the wheel.</summary>
public enum ToolKind
{
    Compress,
    Metadata,
    Edit,
    Annotate,
    Crop,
    Redact,
    RemoveBg,
    AddBg,
}

public static class ToolInfo
{
    public static readonly ToolKind[] All = Enum.GetValues<ToolKind>();

    public static string Label(ToolKind t) => t switch
    {
        ToolKind.Metadata => "METADATA",
        ToolKind.RemoveBg => "REMOVE BG",
        ToolKind.AddBg => "ADD BG",
        _ => t.ToString().ToUpperInvariant(),
    };

    /// <summary>Tools that need a window to collect input; the rest run immediately on drop.</summary>
    public static bool IsInteractive(ToolKind t) => t is ToolKind.Edit or ToolKind.Annotate or ToolKind.Crop or ToolKind.Redact or ToolKind.AddBg;

    /// <summary>Tools work on single images (not PDFs).</summary>
    public static bool CanApply(string path, ToolKind t) => Formats.KindOf(path) == FileKind.Image;
}

public enum RedactStyle { Box, Pixelate, Blur }

public sealed record PixelRect(int X, int Y, int Width, int Height)
{
    /// <summary>Clamped to an image of the given size; null if nothing is left.</summary>
    public PixelRect? ClampTo(uint w, uint h)
    {
        var x0 = Math.Clamp(X, 0, (int)w);
        var y0 = Math.Clamp(Y, 0, (int)h);
        var x1 = Math.Clamp(X + Width, 0, (int)w);
        var y1 = Math.Clamp(Y + Height, 0, (int)h);
        return x1 - x0 < 1 || y1 - y0 < 1 ? null : new PixelRect(x0, y0, x1 - x0, y1 - y0);
    }

    internal MagickGeometry Geometry => new(X, Y, (uint)Width, (uint)Height);
}

public sealed record EditOptions
{
    /// <summary>Clockwise quarter turns, 0..3.</summary>
    public int QuarterTurns { get; init; }
    public bool FlipHorizontal { get; init; }
    public bool FlipVertical { get; init; }
    /// <summary>-100..100, 0 = unchanged.</summary>
    public double Brightness { get; init; }
    public double Contrast { get; init; }
    public double Saturation { get; init; }
    /// <summary>Output size as a percentage, 1..100.</summary>
    public double ScalePercent { get; init; } = 100;

    public bool IsIdentity => QuarterTurns % 4 == 0 && !FlipHorizontal && !FlipVertical && Brightness == 0 && Contrast == 0 && Saturation == 0 && ScalePercent >= 100;
}

public static class Tools
{
    /// <summary>Runs a non-interactive tool and returns the file written.</summary>
    public static string RunInstant(string source, ToolKind tool) => tool switch
    {
        ToolKind.Compress => Compress(source),
        ToolKind.Metadata => StripMetadata(source),
        ToolKind.RemoveBg => RemoveBackground(source),
        _ => throw new ArgumentException($"{tool} needs an editor window", nameof(tool)),
    };

    // ---- Compress ----

    /// <summary>Longest side kept by Compress; bigger images are scaled down.</summary>
    public const int CompressMaxSide = 2560;

    public static string Compress(string source)
    {
        using var img = ImageIO.LoadFirst(source);
        var inputBytes = new FileInfo(source).Length;
        img.Strip();
        if (Math.Max(img.Width, img.Height) > CompressMaxSide)
            img.Resize(new MagickGeometry(CompressMaxSide, CompressMaxSide) { Greater = true });

        var current = ImageIO.SameFormatFor(source);
        TargetFormat target;
        int? quality = null;
        switch (current)
        {
            case TargetFormat.Jpg:
                (target, quality) = (TargetFormat.Jpg, 78);
                break;
            case TargetFormat.Webp:
                (target, quality) = (TargetFormat.Webp, 75);
                break;
            case TargetFormat.Avif:
                (target, quality) = (TargetFormat.Avif, 60);
                break;
            default:
                // PNG/BMP/TIFF/GIF: lossy palette PNG when there's transparency, otherwise JPEG.
                if (img.HasAlpha && !img.IsOpaque)
                {
                    target = TargetFormat.Png;
                    img.Quantize(new QuantizeSettings { Colors = 256, DitherMethod = DitherMethod.FloydSteinberg });
                    img.Settings.SetDefine(MagickFormat.Png, "compression-level", "9");
                }
                else
                {
                    (target, quality) = (TargetFormat.Jpg, 82);
                }
                break;
        }

        var output = OutputPaths.NextTo(source, Formats.Extension(target), " Compressed");
        ImageIO.SaveRaster(img, target, output, quality);
        if (new FileInfo(output).Length >= inputBytes)
        {
            File.Delete(output);
            throw new UserFacingException($"“{Path.GetFileName(source)}” is already as small as it gets.");
        }
        return output;
    }

    // ---- Metadata ----

    /// <summary>Removes EXIF/GPS/XMP/IPTC and other profiles. Keeps orientation by baking it into the pixels.</summary>
    public static string StripMetadata(string source)
    {
        using var img = ImageIO.LoadFirst(source);
        var target = ImageIO.SameFormatFor(source);
        int? quality = target == TargetFormat.Jpg && img.Quality is > 0 and <= 100 ? (int)img.Quality : null;
        img.Strip();
        var output = OutputPaths.NextTo(source, Formats.Extension(target), " Clean");
        ImageIO.SaveRaster(img, target, output, quality);
        return output;
    }

    // ---- Crop ----

    public static string Crop(string source, PixelRect rect)
    {
        using var img = ImageIO.LoadFirst(source);
        var r = rect.ClampTo(img.Width, img.Height) ?? throw new UserFacingException("The crop area is empty.");
        img.Crop(r.Geometry);
        img.ResetPage();
        return SaveSame(source, img, " Cropped");
    }

    // ---- Redact ----

    public static string Redact(string source, IReadOnlyList<PixelRect> rects, RedactStyle style)
    {
        using var img = ImageIO.LoadFirst(source);
        ApplyRedactions(img, rects, style);
        return SaveSame(source, img, " Redacted");
    }

    public static void ApplyRedactions(MagickImage img, IReadOnlyList<PixelRect> rects, RedactStyle style)
    {
        foreach (var raw in rects)
        {
            var r = raw.ClampTo(img.Width, img.Height);
            if (r is null) continue;
            if (style == RedactStyle.Box)
            {
                new Drawables().FillColor(MagickColors.Black).StrokeColor(MagickColors.Transparent)
                    .Rectangle(r.X, r.Y, r.X + r.Width, r.Y + r.Height).Draw(img);
                continue;
            }
            using var region = (MagickImage)img.Clone();
            region.Crop(r.Geometry);
            region.ResetPage();
            if (style == RedactStyle.Pixelate)
            {
                // Blocks of ~1/12 of the shorter side, at least 8px, so text can't be read back.
                var block = Math.Max(8, Math.Min(r.Width, r.Height) / 12);
                region.Scale(new MagickGeometry((uint)Math.Max(1, r.Width / block), (uint)Math.Max(1, r.Height / block)) { IgnoreAspectRatio = true });
                region.FilterType = FilterType.Point;
                region.Resize(new MagickGeometry((uint)r.Width, (uint)r.Height) { IgnoreAspectRatio = true });
            }
            else
            {
                var sigma = Math.Max(6, Math.Min(r.Width, r.Height) / 6.0);
                region.Blur(0, sigma);
                region.Blur(0, sigma);
            }
            img.Composite(region, r.X, r.Y, CompositeOperator.Over);
        }
    }

    // ---- Edit ----

    public static string Edit(string source, EditOptions options)
    {
        using var img = ImageIO.LoadFirst(source);
        ApplyEdit(img, options);
        return SaveSame(source, img, " Edited");
    }

    public static void ApplyEdit(MagickImage img, EditOptions o)
    {
        var turns = ((o.QuarterTurns % 4) + 4) % 4;
        if (turns != 0) img.Rotate(90 * turns);
        if (o.FlipHorizontal) img.Flop();
        if (o.FlipVertical) img.Flip();
        if (o.Brightness != 0 || o.Contrast != 0)
            img.BrightnessContrast(new Percentage(Math.Clamp(o.Brightness, -100, 100)), new Percentage(Math.Clamp(o.Contrast, -100, 100)));
        if (o.Saturation != 0)
            img.Modulate(new Percentage(100), new Percentage(100 + Math.Clamp(o.Saturation, -100, 100)), new Percentage(100));
        if (o.ScalePercent is > 0 and < 100)
            img.Resize(new Percentage(o.ScalePercent));
        img.ResetPage();
    }

    // ---- Annotate ----

    /// <summary>Draws a transparent overlay (same pixel size as the image, as PNG bytes) on top of the image.</summary>
    public static string Annotate(string source, byte[] overlayPng)
    {
        using var img = ImageIO.LoadFirst(source);
        using var overlay = new MagickImage(overlayPng);
        if (overlay.Width != img.Width || overlay.Height != img.Height)
            overlay.Resize(new MagickGeometry(img.Width, img.Height) { IgnoreAspectRatio = true });
        img.Composite(overlay, 0, 0, CompositeOperator.Over);
        return SaveSame(source, img, " Annotated");
    }

    // ---- Add background ----

    public static string AddBackground(string source, BackgroundOptions options)
    {
        using var img = ImageIO.LoadFirst(source);
        using var result = BackgroundRenderer.Render(img, options);
        var target = ImageIO.SameFormatFor(source);
        var output = OutputPaths.NextTo(source, Formats.Extension(target), " BG");
        ImageIO.SaveRaster(result, target, output);
        return output;
    }

    // ---- Remove background ----

    public static string RemoveBackground(string source)
    {
        using var img = ImageIO.LoadFirst(source);
        BackgroundRemover.Apply(img);
        var output = OutputPaths.NextTo(source, ".png", " No BG");
        ImageIO.SaveRaster(img, TargetFormat.Png, output);
        return output;
    }

    static string SaveSame(string source, MagickImage img, string suffix)
    {
        var target = ImageIO.SameFormatFor(source);
        var output = OutputPaths.NextTo(source, Formats.Extension(target), suffix);
        ImageIO.SaveRaster(img, target, output, target == TargetFormat.Jpg ? 92 : null);
        return output;
    }
}
