namespace DragShift.Core;

/// <summary>Conversion targets on the outer ring of the wheel.</summary>
public enum TargetFormat
{
    Png,
    Jpg,
    Webp,
    Heic,
    Avif,
    Bmp,
    Tiff,
    Pdf,
    Docx,
}

/// <summary>What kind of input a dropped file is.</summary>
public enum FileKind
{
    Unsupported,
    Image,
    Pdf,
}

public static class Formats
{
    public static readonly TargetFormat[] All = Enum.GetValues<TargetFormat>();

    static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".jpe", ".jfif", ".webp", ".heic", ".heif", ".avif", ".bmp", ".dib",
        ".tif", ".tiff", ".gif", ".ico", ".tga", ".psd", ".jxl", ".svg",
    };

    public static string Label(TargetFormat f) => f.ToString().ToUpperInvariant();

    public static string Extension(TargetFormat f) => f switch
    {
        TargetFormat.Jpg => ".jpg",
        TargetFormat.Tiff => ".tiff",
        _ => "." + f.ToString().ToLowerInvariant(),
    };

    public static FileKind KindOf(string path)
    {
        var ext = Path.GetExtension(path);
        if (ext.Equals(".pdf", StringComparison.OrdinalIgnoreCase)) return FileKind.Pdf;
        return ImageExtensions.Contains(ext) ? FileKind.Image : FileKind.Unsupported;
    }

    /// <summary>The target format a file already is, if any (so the wheel can disable a no-op conversion).</summary>
    public static TargetFormat? CurrentFormat(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => TargetFormat.Png,
        ".jpg" or ".jpeg" or ".jpe" or ".jfif" => TargetFormat.Jpg,
        ".webp" => TargetFormat.Webp,
        ".heic" or ".heif" => TargetFormat.Heic,
        ".avif" => TargetFormat.Avif,
        ".bmp" or ".dib" => TargetFormat.Bmp,
        ".tif" or ".tiff" => TargetFormat.Tiff,
        ".pdf" => TargetFormat.Pdf,
        ".docx" => TargetFormat.Docx,
        _ => null,
    };

    /// <summary>
    /// A conversion makes sense when the file is readable, isn't already in that format, and this system can write the
    /// format (HEIC needs Windows' HEIF/HEVC codecs).
    /// </summary>
    public static bool CanConvert(string path, TargetFormat target) =>
        KindOf(path) != FileKind.Unsupported && CurrentFormat(path) != target && IsAvailable(target);

    public static bool IsAvailable(TargetFormat target) => target != TargetFormat.Heic || Platform.Heic is not null;
}
