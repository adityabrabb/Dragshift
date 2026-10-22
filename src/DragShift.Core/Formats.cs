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

    public static bool IsAvailable(TargetFormat target) => target != TargetFormat.Heic || Platform.Heic is not null;

    static bool IsGif(string path) => Path.GetExtension(path).Equals(".gif", StringComparison.OrdinalIgnoreCase);
}
