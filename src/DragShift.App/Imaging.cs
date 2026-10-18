using System;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImageMagick;

namespace DragShift.App;

static class Imaging
{
    /// <summary>WPF bitmap of a Magick image (straight copy of the pixels, frozen so any thread may use it).</summary>
    public static BitmapSource ToBitmapSource(IMagickImage<byte> image)
    {
        var w = (int)image.Width;
        var h = (int)image.Height;
        using var pixels = image.GetPixelsUnsafe();
        var bgra = pixels.ToByteArray(0, 0, (uint)w, (uint)h, "BGRA") ?? throw new InvalidOperationException("Couldn't read pixels.");
        var source = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, bgra, w * 4);
        source.Freeze();
        return source;
    }

    /// <summary>A copy no bigger than <paramref name="maxSide"/>, for fast on-screen previews. Caller owns it.</summary>
    public static MagickImage Downscaled(MagickImage image, uint maxSide)
    {
        var copy = (MagickImage)image.Clone();
        if (Math.Max(copy.Width, copy.Height) > maxSide)
            copy.Resize(new MagickGeometry(maxSide, maxSide) { Greater = true });
        return copy;
    }

    public static byte[] EncodePng(BitmapSource source)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }
}
