namespace DragShift.Core;

/// <summary>Renders PDF pages to PNG bytes. The Windows app supplies one backed by Windows.Data.Pdf.</summary>
public interface IPdfRasterizer
{
    /// <summary>Renders the given zero-based pages (all pages when null), in order.</summary>
    IReadOnlyList<byte[]> RenderPages(string pdfPath, int dpi, IReadOnlyList<int>? pages = null);
}

/// <summary>Encodes an image (given as PNG bytes) to HEIC. The Windows app supplies one backed by WIC's HEIF encoder.</summary>
public interface IHeicEncoder
{
    byte[] EncodeHeic(byte[] png, int quality);
}

/// <summary>Recognizes text in an image (given as PNG bytes). The Windows app supplies one backed by Windows.Media.Ocr.</summary>
public interface ITextRecognizer
{
    /// <summary>The text found, one line per line of text; empty when there is none.</summary>
    string Recognize(byte[] png);
}

/// <summary>
/// Platform services the core can't provide portably. An unset service makes the matching actions unavailable:
/// e.g. HEIC output is only offered when <see cref="Heic"/> is set, which the app does only after checking that this
/// PC's codecs can really encode HEIC.
/// </summary>
public static class Platform
{
    static volatile IPdfRasterizer? pdf;
    static volatile IHeicEncoder? heic;
    static volatile ITextRecognizer? ocr;
    static volatile string? ffmpeg;

    public static IPdfRasterizer? Pdf { get => pdf; set => pdf = value; }
    public static IHeicEncoder? Heic { get => heic; set => heic = value; }
    public static ITextRecognizer? Ocr { get => ocr; set => ocr = value; }
    /// <summary>Path to ffmpeg(.exe), for video and audio. See <see cref="Media.FindFfmpeg"/>.</summary>
    public static string? Ffmpeg { get => ffmpeg; set => ffmpeg = value; }
}

/// <summary>An expected failure with a message fit to show the user as-is.</summary>
public sealed class UserFacingException(string message, Exception? inner = null) : Exception(message, inner);
