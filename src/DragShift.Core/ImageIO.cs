using ImageMagick;

namespace DragShift.Core;

/// <summary>Loading any supported input as image frames, and encoding frames to a target format.</summary>
public static class ImageIO
{
    /// <summary>Resolution PDF pages are rendered at.</summary>
    public const int PdfDpi = 200;

    /// <summary>
    /// All frames/pages of an input, upright (EXIF orientation applied) and fully composed (animated GIF frames coalesced).
    /// The caller owns the returned collection.
    /// </summary>
    public static MagickImageCollection Load(string path)
    {
        switch (Formats.KindOf(path))
        {
            case FileKind.Pdf:
            {
                var pdf = Platform.Pdf ?? throw new UserFacingException("Reading PDFs isn't available on this system.");
                var pages = pdf.RenderPages(path, PdfDpi);
                if (pages.Count == 0) throw new UserFacingException($"“{Path.GetFileName(path)}” has no pages.");
                var collection = new MagickImageCollection();
                foreach (var page in pages)
                {
                    var img = new MagickImage(page);
                    img.Density = new Density(PdfDpi, PdfDpi, DensityUnit.PixelsPerInch);
                    collection.Add(img);
                }
                return collection;
            }
            case FileKind.Image:
            {
                MagickImageCollection collection;
                try
                {
                    collection = new MagickImageCollection(path);
                }
                catch (MagickException e)
                {
                    throw new UserFacingException($"Couldn't read “{Path.GetFileName(path)}”: {FirstLine(e.Message)}", e);
                }
                if (collection.Count > 1) collection.Coalesce();
                foreach (var frame in collection) frame.AutoOrient();
                return collection;
            }
            default:
                throw new UserFacingException($"“{Path.GetFileName(path)}” isn't an image or PDF.");
        }
    }

    /// <summary>The first frame/page only. The caller owns the returned image.</summary>
    public static MagickImage LoadFirst(string path)
    {
        using var all = Load(path);
        return (MagickImage)all[0].Clone();
    }

    public static byte[] ToPng(IMagickImage<byte> image)
    {
        using var copy = image.Clone();
        copy.Format = MagickFormat.Png;
        return copy.ToByteArray();
    }

    /// <summary>Writes one image in a raster target format. JPEG/BMP get flattened onto white since they have no alpha.</summary>
    public static void SaveRaster(IMagickImage<byte> image, TargetFormat target, string path, int? quality = null)
    {
        if (target == TargetFormat.Heic)
        {
            var heic = Platform.Heic ?? throw new UserFacingException("Saving HEIC isn't available on this system.");
            File.WriteAllBytes(path, heic.EncodeHeic(ToPng(image), quality ?? 90));
            return;
        }
        using var copy = image.Clone();
        PrepareFor(copy, target, quality);
        copy.Write(path);
    }

    /// <summary>Writes several frames into one multi-frame file (animated WebP, multi-page TIFF).</summary>
    public static void SaveFrames(MagickImageCollection frames, TargetFormat target, string path, int? quality = null)
    {
        using var copy = new MagickImageCollection();
        foreach (var frame in frames)
        {
            var f = frame.Clone();
            PrepareFor(f, target, quality);
            copy.Add(f);
        }
        if (target == TargetFormat.Webp) copy.Optimize();
        copy.Write(path, MagickFormatFor(target));
    }

    public static bool SupportsMultipleFrames(TargetFormat target) => target is TargetFormat.Webp or TargetFormat.Tiff;

    static void PrepareFor(IMagickImage<byte> img, TargetFormat target, int? quality)
    {
        img.Format = MagickFormatFor(target);
        switch (target)
        {
            case TargetFormat.Jpg:
                Flatten(img);
                img.Quality = (uint)(quality ?? 92);
                break;
            case TargetFormat.Bmp:
                Flatten(img);
                break;
            case TargetFormat.Webp:
                img.Quality = (uint)(quality ?? 90);
                break;
            case TargetFormat.Avif:
                img.Quality = (uint)(quality ?? 80);
                break;
            case TargetFormat.Tiff:
                img.Settings.Compression = CompressionMethod.Zip;
                break;
        }
    }

    static void Flatten(IMagickImage<byte> img)
    {
        if (!img.HasAlpha) return;
        img.BackgroundColor = MagickColors.White;
        img.Alpha(AlphaOption.Remove);
    }

    public static MagickFormat MagickFormatFor(TargetFormat target) => target switch
    {
        TargetFormat.Png => MagickFormat.Png,
        TargetFormat.Jpg => MagickFormat.Jpeg,
        TargetFormat.Webp => MagickFormat.WebP,
        TargetFormat.Heic => MagickFormat.Heic,
        TargetFormat.Avif => MagickFormat.Avif,
        TargetFormat.Bmp => MagickFormat.Bmp,
        TargetFormat.Tiff => MagickFormat.Tiff,
        TargetFormat.Pdf => MagickFormat.Pdf,
        _ => throw new ArgumentOutOfRangeException(nameof(target), target, "Not a raster format"),
    };

    /// <summary>The target format to keep when re-saving a file in place-ish (tools write the same format back when they can).</summary>
    public static TargetFormat SameFormatFor(string path)
    {
        var current = Formats.CurrentFormat(path);
        return current switch
        {
            null or TargetFormat.Pdf or TargetFormat.Docx => TargetFormat.Png,
            // HEIC writing depends on an optional Windows codec; keep tool output dependable.
            TargetFormat.Heic => TargetFormat.Jpg,
            _ => current.Value,
        };
    }

    internal static string FirstLine(string s)
    {
        var i = s.IndexOf('\n');
        return (i < 0 ? s : s[..i]).Trim();
    }
}
