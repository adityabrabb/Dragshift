using System.IO.Compression;
using ImageMagick;

namespace DragShift.Core;

/// <summary>
/// Zips are treated as the files inside them: the pictures in a zip can be converted, compressed or cleaned (giving a
/// new zip with everything else as it was), its pictures, PDFs and slides combined into one PDF, or its text copied.
/// Entries are only ever unpacked to a private temp folder under a made-up name, never by their own path.
/// </summary>
public static class Archives
{
    /// <summary>Entries bigger than this (uncompressed) are copied as they are, never unpacked.</summary>
    const long MaxEntryBytes = 1L << 30;

    static readonly IReadOnlySet<FileKind> None = new HashSet<FileKind>();
    static readonly Dictionary<string, (DateTime stamp, IReadOnlySet<FileKind> kinds)> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The kinds of file inside a zip. Cached per path and modification time, since the wheel asks for every slice.</summary>
    public static IReadOnlySet<FileKind> KindsIn(string zip)
    {
        DateTime stamp;
        try
        {
            stamp = File.GetLastWriteTimeUtc(zip);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return None;
        }
        lock (Cache)
        {
            if (Cache.TryGetValue(zip, out var hit) && hit.stamp == stamp) return hit.kinds;
        }
        var kinds = new HashSet<FileKind>();
        try
        {
            using var archive = ZipFile.OpenRead(zip);
            foreach (var entry in archive.Entries.Where(IsUsable)) kinds.Add(KindOf(entry));
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            // Unreadable: nothing on the wheel applies.
        }
        lock (Cache) Cache[zip] = (stamp, kinds);
        return kinds;
    }

    public static string? WhyNot(string zip, TargetFormat target)
    {
        var kinds = KindsIn(zip);
        if (target == TargetFormat.Pdf)
        {
            return kinds.Contains(FileKind.Image) || kinds.Contains(FileKind.Pdf) || (kinds.Contains(FileKind.Presentation) && Platform.Office is not null)
                ? null
                : "Nothing in this zip for a PDF";
        }
        if (target == TargetFormat.Docx || Formats.IsMedia(target)) return "Not for these files";
        return kinds.Contains(FileKind.Image) ? null : "No pictures in this zip";
    }

    public static string? WhyNot(string zip, ToolKind tool)
    {
        var kinds = KindsIn(zip);
        switch (tool)
        {
            case ToolKind.Compress or ToolKind.Metadata:
                return kinds.Contains(FileKind.Image) ? null : "No pictures in this zip";
            case ToolKind.CopyText:
                if (kinds.Contains(FileKind.Pdf) || kinds.Contains(FileKind.Presentation)) return null;
                if (kinds.Contains(FileKind.Image)) return Platform.Ocr is null ? "Text recognition isn't set up in Windows" : null;
                return "No text in this zip";
            default:
                return "Not for these files";
        }
    }

    // ---- New zips ----

    /// <summary>A copy of the zip with every picture converted, e.g. "Photos WEBP.zip".</summary>
    public static string ConvertImages(string zip, TargetFormat target) =>
        Rewrite(zip, " " + Formats.Label(target), CompressionLevel.Optimal,
            file => Formats.CanConvert(file, target) ? Converter.Convert(file, target)[0] : null,
            $"No pictures in “{Path.GetFileName(zip)}” could be converted to {Formats.Label(target)}.");

    /// <summary>Pictures compressed like the Compress tool does, and everything packed as tightly as zip allows.</summary>
    public static string Compress(string zip)
    {
        var output = Rewrite(zip, " Compressed", CompressionLevel.SmallestSize,
            file => IsGif(file) ? null : Tools.Compress(file),
            $"“{Path.GetFileName(zip)}” is already as small as it gets.");
        if (new FileInfo(output).Length >= new FileInfo(zip).Length)
        {
            File.Delete(output);
            throw new UserFacingException($"“{Path.GetFileName(zip)}” is already as small as it gets.");
        }
        return output;
    }

    /// <summary>EXIF, GPS and other metadata removed from every picture.</summary>
    public static string StripMetadata(string zip) =>
        Rewrite(zip, " Clean", CompressionLevel.Optimal,
            // A GIF would lose its animation (Metadata works on one frame), and rarely carries any.
            file => IsGif(file) ? null : Tools.StripMetadata(file),
            $"No pictures in “{Path.GetFileName(zip)}” could be cleaned.");

    static bool IsGif(string file) => Path.GetExtension(file).Equals(".gif", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Copies the zip next to itself with <paramref name="suffix"/>, replacing each picture with what
    /// <paramref name="transform"/> makes of it (null, or an expected failure, keeps the original).
    /// </summary>
    static string Rewrite(string zip, string suffix, CompressionLevel level, Func<string, string?> transform, string nothingChanged)
    {
        using var temp = new TempFolder();
        var output = OutputPaths.NextTo(zip, ".zip", suffix);
        var changed = 0;
        try
        {
            using var source = Open(zip);
            using var fs = File.Create(output);
            using var outZip = new ZipArchive(fs, ZipArchiveMode.Create);
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in source.Entries)
            {
                string? produced = null;
                if (IsUsable(entry) && KindOf(entry) == FileKind.Image)
                {
                    try
                    {
                        produced = transform(Extract(entry, temp));
                    }
                    catch (Exception e) when (e is UserFacingException or MagickException)
                    {
                        // This one can't be done (unreadable, already small…): it goes in as it was.
                    }
                }
                var name = produced is null ? entry.FullName : Path.ChangeExtension(entry.FullName, Path.GetExtension(produced));
                var copy = outZip.CreateEntry(Unique(name, used), level);
                copy.LastWriteTime = entry.LastWriteTime;
                if (entry.Name.Length == 0) continue; // a folder
                using var to = copy.Open();
                using var from = produced is null ? entry.Open() : File.OpenRead(produced);
                from.CopyTo(to);
                if (produced is not null) changed++;
            }
        }
        catch (Exception e)
        {
            try { File.Delete(output); } catch { /* best effort */ }
            if (e is InvalidDataException) throw new UserFacingException($"Couldn't read “{Path.GetFileName(zip)}” (it may be password-protected or damaged).", e);
            throw;
        }
        if (changed == 0)
        {
            File.Delete(output);
            throw new UserFacingException(nothingChanged);
        }
        return output;
    }

    /// <summary>Two pictures can end up with one name ("a.png" and "a.jpg" both become "a.webp"): the second gets "(2)".</summary>
    static string Unique(string name, HashSet<string> used)
    {
        var candidate = name;
        for (var n = 2; !used.Add(candidate); n++)
            candidate = name.EndsWith('/') ? $"{name.TrimEnd('/')} ({n})/" : $"{Path.ChangeExtension(name, null)} ({n}){Path.GetExtension(name)}";
        return candidate;
    }

    // ---- Reading the contents ----

    /// <summary>The text of the PDFs, slides and (with OCR) pictures inside, in name order.</summary>
    public static string Text(string zip)
    {
        using var temp = new TempFolder();
        var texts = new List<string>();
        foreach (var file in ExtractAll(zip, temp, k => k is FileKind.Pdf or FileKind.Presentation || (k == FileKind.Image && Platform.Ocr is not null)))
        {
            try
            {
                texts.Add(TextExtractor.Extract(file));
            }
            catch (Exception e) when (e is UserFacingException or MagickException)
            {
                // Skip what can't be read; the rest is still worth copying.
            }
        }
        return string.Join("\n\n", texts.Where(t => t.Length > 0));
    }

    /// <summary>The pictures, PDFs and slides inside, unpacked into <paramref name="temp"/> in name order, for a combined PDF.</summary>
    internal static IReadOnlyList<string> ExtractForPdf(string zip, TempFolder temp) =>
        ExtractAll(zip, temp, k => k is FileKind.Image or FileKind.Pdf || (k == FileKind.Presentation && Platform.Office is not null));

    static List<string> ExtractAll(string zip, TempFolder temp, Func<FileKind, bool> which)
    {
        using var archive = Open(zip);
        return archive.Entries.Where(e => IsUsable(e) && which(KindOf(e)))
            .OrderBy(e => e.FullName, NaturalComparer.Instance)
            .Select(e => Extract(e, temp)).ToList();
    }

    static string Extract(ZipArchiveEntry entry, TempFolder temp)
    {
        // Named by the temp folder, so "../../x" or "C:\x" entry names can't put anything anywhere else.
        var path = temp.NewFile(Path.GetExtension(entry.Name).ToLowerInvariant());
        entry.ExtractToFile(path);
        return path;
    }

    /// <summary>Real files: not folders, macOS resource forks, password-protected or absurdly large entries.</summary>
    static bool IsUsable(ZipArchiveEntry e) =>
        e.Name.Length > 0 && !e.IsEncrypted && !e.Name.StartsWith("._", StringComparison.Ordinal) &&
        !e.FullName.StartsWith("__MACOSX/", StringComparison.OrdinalIgnoreCase) && e.Length <= MaxEntryBytes;

    static FileKind KindOf(ZipArchiveEntry e) => Formats.KindOf(e.Name) switch
    {
        // One level deep only, and old binary .ppt has no text to read here.
        FileKind.Archive => FileKind.Unsupported,
        FileKind.Presentation when !Presentations.IsOpenXml(e.Name) => FileKind.Unsupported,
        var k => k,
    };

    static ZipArchive Open(string zip)
    {
        try
        {
            return ZipFile.OpenRead(zip);
        }
        catch (InvalidDataException e)
        {
            throw new UserFacingException($"Couldn't read “{Path.GetFileName(zip)}” (it may be damaged).", e);
        }
    }
}
