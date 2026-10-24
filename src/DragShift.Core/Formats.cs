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
    Gif,
    Pdf,
    Docx,
    Mp4,
    Webm,
    Mp3,
}

/// <summary>What kind of input a dropped file is.</summary>
public enum FileKind
{
    Unsupported,
    Image,
    Pdf,
    Video,
    Audio,
    Presentation,
    Archive,
}

public static class Formats
{
    public static readonly TargetFormat[] All = Enum.GetValues<TargetFormat>();

    static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".jpe", ".jfif", ".webp", ".heic", ".heif", ".avif", ".bmp", ".dib",
        ".tif", ".tiff", ".gif", ".ico", ".tga", ".psd", ".jxl", ".svg",
    };

    static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".m4v", ".mov", ".webm", ".mkv", ".avi", ".wmv", ".flv", ".mpg", ".mpeg", ".m2ts", ".mts", ".ts", ".3gp",
    };

    static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".wav", ".m4a", ".aac", ".flac", ".ogg", ".oga", ".opus", ".wma", ".aiff", ".aif",
    };

    static readonly HashSet<string> PresentationExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pptx", ".pptm", ".ppsx", ".ppsm", ".potx", ".ppt", ".pps",
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
        if (ImageExtensions.Contains(ext)) return FileKind.Image;
        if (VideoExtensions.Contains(ext)) return FileKind.Video;
        if (PresentationExtensions.Contains(ext)) return FileKind.Presentation;
        if (ext.Equals(".zip", StringComparison.OrdinalIgnoreCase)) return FileKind.Archive;
        return AudioExtensions.Contains(ext) ? FileKind.Audio : FileKind.Unsupported;
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
        ".gif" => TargetFormat.Gif,
        ".pdf" => TargetFormat.Pdf,
        ".docx" => TargetFormat.Docx,
        ".mp4" => TargetFormat.Mp4,
        ".webm" => TargetFormat.Webm,
        ".mp3" => TargetFormat.Mp3,
        _ => null,
    };

    /// <summary>Video and audio targets, written by FFmpeg.</summary>
    public static bool IsMedia(TargetFormat f) => f is TargetFormat.Mp4 or TargetFormat.Webm or TargetFormat.Mp3;

    /// <summary>
    /// A conversion makes sense when the file is readable, isn't already in that format, and this system can write the
    /// format (HEIC needs Windows' HEIF/HEVC codecs, video and audio need FFmpeg).
    /// </summary>
    public static bool CanConvert(string path, TargetFormat target) => WhyNot(path, target) is null;

    /// <summary>Null when <see cref="CanConvert"/> holds, otherwise a short reason fit for the wheel's center.</summary>
    public static string? WhyNot(string path, TargetFormat target)
    {
        if (!IsAvailable(target)) return $"{Label(target)} isn't available on this PC";
        if (CurrentFormat(path) == target) return $"Already {Label(target)}";
        var kind = KindOf(path);
        if (kind == FileKind.Archive) return Archives.WhyNot(path, target);
        if (kind == FileKind.Presentation) return PresentationWhyNot(path, target);
        var fits = kind switch
        {
            FileKind.Image => !IsMedia(target) || (IsGif(path) && target is TargetFormat.Mp4 or TargetFormat.Webm),
            FileKind.Pdf => target is not (TargetFormat.Gif or TargetFormat.Pdf) && !IsMedia(target),
            FileKind.Video => target is TargetFormat.Gif || IsMedia(target),
            FileKind.Audio => target is TargetFormat.Mp3,
            _ => false,
        };
        if (!fits) return "Not for these files";
        var needsFfmpeg = kind is FileKind.Video or FileKind.Audio || IsMedia(target);
        return needsFfmpeg && Platform.Ffmpeg is null ? "Needs FFmpeg: tray menu → Add video support" : null;
    }

    /// <summary>
    /// Slides become a PDF, or one picture per slide, through PowerPoint/LibreOffice. DOCX (slide text and pictures)
    /// is read straight from a .pptx, so it works without them.
    /// </summary>
    static string? PresentationWhyNot(string path, TargetFormat target)
    {
        if (IsMedia(target) || target == TargetFormat.Gif) return "Not for these files";
        if (target == TargetFormat.Docx && Presentations.IsOpenXml(path)) return null;
        if (Platform.Office is null) return "Needs PowerPoint or LibreOffice";
        return target != TargetFormat.Pdf && Platform.Pdf is null ? "Reading PDFs isn't available on this system" : null;
    }

    /// <summary>Files that can go into a combined PDF: images, PDFs, presentations (when they can be rendered) and zips of those.</summary>
    public static bool Combinable(string path) => KindOf(path) switch
    {
        FileKind.Image or FileKind.Pdf => true,
        FileKind.Presentation => Platform.Office is not null,
        FileKind.Archive => Archives.WhyNot(path, TargetFormat.Pdf) is null,
        _ => false,
    };

    public static bool IsAvailable(TargetFormat target) => target != TargetFormat.Heic || Platform.Heic is not null;

    static bool IsGif(string path) => Path.GetExtension(path).Equals(".gif", StringComparison.OrdinalIgnoreCase);
}
