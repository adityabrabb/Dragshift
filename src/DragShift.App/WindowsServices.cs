using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using DragShift.Core;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace DragShift.App;

/// <summary>
/// PDF pages rendered by PDFium on the CPU. (Windows.Data.Pdf renders on the GPU, and on some graphics drivers a process
/// that has released its PDF objects can't finish exiting: it hangs in the kernel, holding the exe and the tray mutex.)
/// </summary>
sealed class PdfiumRasterizer : IPdfRasterizer
{
    /// <summary>Caps the rendered page width so huge pages don't exhaust memory.</summary>
    const int MaxWidth = 5000;

    /// <summary>PDFium isn't thread-safe, and Actions converts two files at a time.</summary>
    static readonly object Gate = new();
    static bool initialized;

    public IReadOnlyList<byte[]> RenderPages(string pdfPath, int dpi, IReadOnlyList<int>? which = null)
    {
        lock (Gate)
        {
            if (!initialized)
            {
                Pdfium.FPDF_InitLibrary();
                initialized = true;
            }
            // Loaded from memory: PDFium's own file loading doesn't take every Windows file name.
            var bytes = File.ReadAllBytes(pdfPath);
            var pin = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            var doc = Pdfium.FPDF_LoadMemDocument64(pin.AddrOfPinnedObject(), (nuint)bytes.Length, null);
            if (doc == IntPtr.Zero)
            {
                pin.Free();
                throw new UserFacingException($"Couldn't open “{Path.GetFileName(pdfPath)}” (it may be password-protected or damaged).");
            }
            try
            {
                var count = Pdfium.FPDF_GetPageCount(doc);
                var pages = new List<byte[]>();
                foreach (var i in which ?? Enumerable.Range(0, count).ToList())
                {
                    if (i < 0 || i >= count) throw new ArgumentOutOfRangeException(nameof(which), i, "No such page");
                    pages.Add(RenderPage(doc, i, dpi));
                }
                return pages;
            }
            finally
            {
                Pdfium.FPDF_CloseDocument(doc);
                pin.Free();
            }
        }
    }

    static byte[] RenderPage(IntPtr doc, int index, int dpi)
    {
        var page = Pdfium.FPDF_LoadPage(doc, index);
        if (page == IntPtr.Zero) throw new UserFacingException($"Page {index + 1} of the PDF is damaged.");
        try
        {
            // Sizes are in points (1/72 in), with the page's rotation applied.
            double wPt = Pdfium.FPDF_GetPageWidthF(page), hPt = Pdfium.FPDF_GetPageHeightF(page);
            var w = (int)Math.Clamp(Math.Round(wPt * dpi / 72.0), 1, MaxWidth);
            var h = (int)Math.Max(1, Math.Round(hPt * w / wPt));
            var bitmap = Pdfium.FPDFBitmap_Create(w, h, 1);
            if (bitmap == IntPtr.Zero) throw new UserFacingException("The PDF page is too large to render.");
            try
            {
                Pdfium.FPDFBitmap_FillRect(bitmap, 0, 0, w, h, 0xFFFFFFFF);
                Pdfium.FPDF_RenderPageBitmap(bitmap, page, 0, 0, w, h, 0, Pdfium.FPDF_ANNOT | Pdfium.FPDF_PRINTING);
                var stride = Pdfium.FPDFBitmap_GetStride(bitmap);
                var buffer = Pdfium.FPDFBitmap_GetBuffer(bitmap);
                var bgra = new byte[w * h * 4];
                for (var y = 0; y < h; y++)
                    Marshal.Copy(buffer + y * stride, bgra, y * w * 4, w * 4);
                using var img = new ImageMagick.MagickImage(bgra, new ImageMagick.PixelReadSettings((uint)w, (uint)h, ImageMagick.StorageType.Char, ImageMagick.PixelMapping.BGRA));
                img.Alpha(ImageMagick.AlphaOption.Off);
                img.Format = ImageMagick.MagickFormat.Png;
                return img.ToByteArray();
            }
            finally
            {
                Pdfium.FPDFBitmap_Destroy(bitmap);
            }
        }
        finally
        {
            Pdfium.FPDF_ClosePage(page);
        }
    }

    static class Pdfium
    {
        const string Dll = "pdfium";
        public const int FPDF_ANNOT = 0x01;
        public const int FPDF_PRINTING = 0x800;

        [DllImport(Dll)] public static extern void FPDF_InitLibrary();
        [DllImport(Dll)] public static extern IntPtr FPDF_LoadMemDocument64(IntPtr data, nuint size, [MarshalAs(UnmanagedType.LPUTF8Str)] string? password);
        [DllImport(Dll)] public static extern void FPDF_CloseDocument(IntPtr doc);
        [DllImport(Dll)] public static extern int FPDF_GetPageCount(IntPtr doc);
        [DllImport(Dll)] public static extern IntPtr FPDF_LoadPage(IntPtr doc, int index);
        [DllImport(Dll)] public static extern void FPDF_ClosePage(IntPtr page);
        [DllImport(Dll)] public static extern float FPDF_GetPageWidthF(IntPtr page);
        [DllImport(Dll)] public static extern float FPDF_GetPageHeightF(IntPtr page);
        [DllImport(Dll)] public static extern IntPtr FPDFBitmap_Create(int width, int height, int alpha);
        [DllImport(Dll)] public static extern void FPDFBitmap_FillRect(IntPtr bitmap, int left, int top, int width, int height, uint color);
        [DllImport(Dll)] public static extern void FPDF_RenderPageBitmap(IntPtr bitmap, IntPtr page, int x, int y, int w, int h, int rotate, int flags);
        [DllImport(Dll)] public static extern IntPtr FPDFBitmap_GetBuffer(IntPtr bitmap);
        [DllImport(Dll)] public static extern int FPDFBitmap_GetStride(IntPtr bitmap);
        [DllImport(Dll)] public static extern void FPDFBitmap_Destroy(IntPtr bitmap);
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
        output.Seek(0);
        var buffer = new Windows.Storage.Streams.Buffer((uint)output.Size);
        return (await output.ReadAsync(buffer, (uint)output.Size, InputStreamOptions.None)).ToArray();
    }
}

/// <summary>Text recognition by Windows' built-in OCR, in the languages the user has installed.</summary>
sealed class WindowsTextRecognizer : ITextRecognizer
{
    /// <summary>Small text reads better when the picture is scaled up first.</summary>
    const uint UpscaleBelow = 1400;

    readonly OcrEngine engine;

    WindowsTextRecognizer(OcrEngine engine) => this.engine = engine;

    /// <summary>A recognizer if one of the user's languages has OCR support, otherwise null.</summary>
    public static WindowsTextRecognizer? TryCreate()
    {
        try
        {
            return OcrEngine.TryCreateFromUserProfileLanguages() is { } engine ? new WindowsTextRecognizer(engine) : null;
        }
        catch
        {
            return null;
        }
    }

    public string Recognize(byte[] png) => Task.Run(() => RecognizeAsync(png)).GetAwaiter().GetResult();

    async Task<string> RecognizeAsync(byte[] png)
    {
        using (var img = new ImageMagick.MagickImage(png))
        {
            var longSide = Math.Max(img.Width, img.Height);
            var max = OcrEngine.MaxImageDimension;
            if (longSide > max) img.Resize(new ImageMagick.MagickGeometry(max, max));
            else if (longSide < UpscaleBelow) img.Resize(new ImageMagick.Percentage(Math.Min(200.0, 100.0 * max / longSide)));
            if (img.HasAlpha)
            {
                img.BackgroundColor = ImageMagick.MagickColors.White;
                img.Alpha(ImageMagick.AlphaOption.Remove);
            }
            img.Format = ImageMagick.MagickFormat.Png;
            png = img.ToByteArray();
        }

        using var input = new InMemoryRandomAccessStream();
        await input.WriteAsync(png.AsBuffer());
        input.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(input);
        using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
        var result = await engine.RecognizeAsync(bitmap);
        return string.Join(Environment.NewLine, result.Lines.Select(l => l.Text));
    }
}
