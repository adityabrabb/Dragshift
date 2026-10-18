using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using DragShift.Core;
using Windows.Data.Pdf;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace DragShift.App;

/// <summary>PDF pages rendered by Windows' built-in PDF engine (Windows.Data.Pdf).</summary>
sealed class WindowsPdfRasterizer : IPdfRasterizer
{
    /// <summary>Caps the rendered page width so huge pages don't exhaust memory.</summary>
    const uint MaxWidth = 5000;

    public IReadOnlyList<byte[]> RenderPages(string pdfPath, int dpi, IReadOnlyList<int>? pages = null) =>
        Task.Run(() => RenderAsync(pdfPath, dpi, pages)).GetAwaiter().GetResult();

    static async Task<IReadOnlyList<byte[]>> RenderAsync(string pdfPath, int dpi, IReadOnlyList<int>? which)
    {
        PdfDocument doc;
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(pdfPath));
            doc = await PdfDocument.LoadFromFileAsync(file);
        }
        catch (Exception e)
        {
            throw new UserFacingException($"Couldn't open “{Path.GetFileName(pdfPath)}” (it may be password-protected or damaged).", e);
        }

        var pages = new List<byte[]>();
        foreach (var i in which ?? Enumerable.Range(0, (int)doc.PageCount).ToList())
        {
            if (i < 0 || i >= doc.PageCount) throw new ArgumentOutOfRangeException(nameof(which), i, "No such page");
            using var page = doc.GetPage((uint)i);
            // Page.Size is in DIPs (1/96 in).
            var width = (uint)Math.Clamp(Math.Round(page.Size.Width * dpi / 96.0), 1, MaxWidth);
            var options = new PdfPageRenderOptions
            {
                DestinationWidth = width,
                BackgroundColor = Windows.UI.Color.FromArgb(255, 255, 255, 255),
                BitmapEncoderId = BitmapEncoder.PngEncoderId,
            };
            using var stream = new InMemoryRandomAccessStream();
            await page.RenderToStreamAsync(stream, options);
            pages.Add(await ReadAll(stream));
        }
        return pages;
    }

    internal static async Task<byte[]> ReadAll(IRandomAccessStream stream)
    {
        stream.Seek(0);
        var buffer = new Windows.Storage.Streams.Buffer((uint)stream.Size);
        var read = await stream.ReadAsync(buffer, (uint)stream.Size, InputStreamOptions.None);
        return read.ToArray();
    }
}

/// <summary>HEIC through the Windows Imaging Component, which needs Microsoft's HEIF/HEVC extensions installed.</summary>
sealed class WindowsHeicEncoder : IHeicEncoder
{
    /// <summary>
    /// An encoder if this PC can really write HEIC (the HEIF and HEVC extensions are installed), otherwise null.
    /// Checked by encoding a tiny image, since the HEIF encoder can be registered while the HEVC codec it needs isn't.
    /// </summary>
    public static WindowsHeicEncoder? TryCreate()
    {
        try
        {
            using var probe = new ImageMagick.MagickImage(ImageMagick.MagickColors.Gray, 16, 16) { Format = ImageMagick.MagickFormat.Png };
            var heic = EncodeCoreAsync(probe.ToByteArray(), 80).GetAwaiter().GetResult();
            return heic.Length > 0 ? new WindowsHeicEncoder() : null;
        }
        catch
        {
            return null;
        }
    }

    public byte[] EncodeHeic(byte[] png, int quality) =>
        Task.Run(() => EncodeAsync(png, quality)).GetAwaiter().GetResult();

    static async Task<byte[]> EncodeAsync(byte[] png, int quality)
    {
        try
        {
            return await EncodeCoreAsync(png, quality);
        }
        catch (Exception e) when (e is not UserFacingException)
        {
            throw new UserFacingException(
                "Saving HEIC needs Microsoft's free “HEIF Image Extensions” and “HEVC Video Extensions” from the Microsoft Store.", e);
        }
    }

    static async Task<byte[]> EncodeCoreAsync(byte[] png, int quality)
    {
        using var input = new InMemoryRandomAccessStream();
        await input.WriteAsync(png.AsBuffer());
        input.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(input);
        using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);

        using var output = new InMemoryRandomAccessStream();
        var props = new BitmapPropertySet
        {
            ["ImageQuality"] = new BitmapTypedValue(Math.Clamp(quality, 1, 100) / 100f, Windows.Foundation.PropertyType.Single),
        };
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.HeifEncoderId, output, props);
        encoder.SetSoftwareBitmap(bitmap);
        await encoder.FlushAsync();
        return await WindowsPdfRasterizer.ReadAll(output);
    }
}
