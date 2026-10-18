namespace DragShift.Core;

/// <summary>Renders PDF pages to PNG bytes. The Windows app supplies one backed by Windows.Data.Pdf.</summary>
public interface IPdfRasterizer
{
    IReadOnlyList<byte[]> RenderPages(string pdfPath, int dpi);
}

/// <summary>Encodes an image (given as PNG bytes) to HEIC. The Windows app supplies one backed by WIC's HEIF encoder.</summary>
public interface IHeicEncoder
{
    byte[] EncodeHeic(byte[] png, int quality);
}

/// <summary>Platform services the core can't provide portably. Unset services make the matching actions fail with a clear message.</summary>
public static class Platform
{
    public static IPdfRasterizer? Pdf { get; set; }
    public static IHeicEncoder? Heic { get; set; }
}

/// <summary>An expected failure with a message fit to show the user as-is.</summary>
public sealed class UserFacingException(string message, Exception? inner = null) : Exception(message, inner);
