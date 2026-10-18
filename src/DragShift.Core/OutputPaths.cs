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
