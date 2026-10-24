namespace DragShift.Core;

public static class OutputPaths
{
    /// <summary>
    /// A path next to <paramref name="source"/> named "{name}{suffix}{ext}" that doesn't exist yet,
    /// e.g. "Page 001 Cropped.jpg", then "Page 001 Cropped (2).jpg". Never returns the source itself.
    /// </summary>
    public static string NextTo(string source, string extension, string suffix = "")
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(source)) ?? ".";
        var stem = Path.GetFileNameWithoutExtension(source) + suffix;
        var candidate = Path.Combine(dir, stem + extension);
        for (var n = 2; File.Exists(candidate) || Directory.Exists(candidate) || SamePath(candidate, source); n++)
            candidate = Path.Combine(dir, $"{stem} ({n}){extension}");
        return candidate;
    }

    static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
}

/// <summary>A private scratch folder under %TEMP%, deleted with everything in it when disposed.</summary>
internal sealed class TempFolder : IDisposable
{
    int count;

    public string Dir { get; } = Directory.CreateTempSubdirectory("dragshift-").FullName;

    /// <summary>A new, unused path in the folder with the given extension.</summary>
    public string NewFile(string extension) => Path.Combine(Dir, $"{Interlocked.Increment(ref count)}{extension}");

    public void Dispose()
    {
        try { Directory.Delete(Dir, true); } catch { /* best effort */ }
    }
}
