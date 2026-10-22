using System.Diagnostics;

namespace DragShift.Core;

/// <summary>Video and audio through FFmpeg: conversions, compression and metadata removal.</summary>
public static class Media
{
    /// <summary>Video → GIF keeps at most this many seconds, so a long clip can't turn into a gigabyte GIF.</summary>
    public const int GifMaxSeconds = 60;
    public const int GifMaxWidth = 800;
    public const int GifFps = 15;
    /// <summary>Longest side kept by Compress for videos.</summary>
    public const int CompressMaxSide = 1920;

    // H.264/VP9 with 4:2:0 chroma need even dimensions.
    const string EvenSize = "scale=trunc(iw/2)*2:trunc(ih/2)*2";

    /// <summary>
    /// ffmpeg next to DragShift, on PATH, or where winget puts it; null if there's none that runs.
    /// </summary>
    public static string? FindFfmpeg()
    {
        var exe = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var dirs = new List<string> { AppContext.BaseDirectory };
        dirs.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));
        if (local.Length > 0)
        {
            dirs.Add(Path.Combine(local, "Microsoft", "WinGet", "Links"));
            // winget's portable install, before a new login puts its Links folder on PATH.
            var packages = Path.Combine(local, "Microsoft", "WinGet", "Packages");
            if (Directory.Exists(packages))
            {
                foreach (var pkg in Directory.EnumerateDirectories(packages, "*FFmpeg*"))
                    dirs.AddRange(Directory.EnumerateDirectories(pkg, "bin", SearchOption.AllDirectories));
            }
        }
        foreach (var dir in dirs)
        {
            string candidate;
            try { candidate = Path.Combine(dir.Trim('"'), exe); }
            catch (ArgumentException) { continue; }
            if (File.Exists(candidate) && Runs(candidate)) return candidate;
        }
        return null;
    }

    static bool Runs(string ffmpeg)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(ffmpeg, "-hide_banner -version")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            });
            if (p is null) return false;
            p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(10_000)) { p.Kill(); return false; }
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Converts a video, audio file or GIF and returns the file written.</summary>
    public static string Convert(string source, TargetFormat target)
    {
        var output = OutputPaths.NextTo(source, Formats.Extension(target));
        string[] args = target switch
        {
            TargetFormat.Mp4 =>
            [
                "-vf", EvenSize, "-c:v", "libx264", "-preset", "medium", "-crf", "23", "-pix_fmt", "yuv420p",
                "-c:a", "aac", "-b:a", "160k", "-movflags", "+faststart",
            ],
            TargetFormat.Webm =>
            [
                "-vf", EvenSize, "-c:v", "libvpx-vp9", "-crf", "32", "-b:v", "0", "-deadline", "good", "-cpu-used", "4",
                "-row-mt", "1", "-pix_fmt", "yuv420p", "-c:a", "libopus", "-b:a", "128k",
            ],
            TargetFormat.Gif =>
            [
                "-t", GifMaxSeconds.ToString(),
                "-vf", $"fps={GifFps},scale='min({GifMaxWidth},iw)':-2:flags=lanczos,split[a][b];[a]palettegen=stats_mode=diff[p];[b][p]paletteuse=dither=bayer:bayer_scale=4",
                "-loop", "0",
            ],
            TargetFormat.Mp3 => ["-vn", "-map", "0:a:0", "-c:a", "libmp3lame", "-q:a", "2"],
            _ => throw new ArgumentOutOfRangeException(nameof(target), target, "Not a video or audio format"),
        };
        Run(source, args, output);
        return output;
    }

    /// <summary>Re-encodes a video as a smaller H.264 MP4, at most 1920 px on the long side.</summary>
    public static string Compress(string source)
    {
        var output = OutputPaths.NextTo(source, ".mp4", " Compressed");
        Run(source,
        [
            "-vf", $"scale='min({CompressMaxSide},iw)':'min({CompressMaxSide},ih)':force_original_aspect_ratio=decrease,{EvenSize}",
            "-c:v", "libx264", "-preset", "medium", "-crf", "28", "-pix_fmt", "yuv420p",
            "-c:a", "aac", "-b:a", "128k", "-movflags", "+faststart", "-map_metadata", "-1",
        ], output);
        if (new FileInfo(output).Length >= new FileInfo(source).Length)
        {
            File.Delete(output);
            throw new UserFacingException($"“{Path.GetFileName(source)}” is already as small as it gets.");
        }
        return output;
    }

    /// <summary>Copies the streams into a new file without tags (location, device, dates). Nothing is re-encoded.</summary>
    public static string StripMetadata(string source)
    {
        var output = OutputPaths.NextTo(source, Path.GetExtension(source), " Clean");
        string[] streams = Formats.KindOf(source) == FileKind.Audio ? ["-map", "0:a"] : ["-map", "0:v", "-map", "0:a?"];
        Run(source, [.. streams, "-c", "copy", "-map_metadata", "-1", "-map_chapters", "-1"], output);
        return output;
    }

    static void Run(string source, IEnumerable<string> args, string output)
    {
        var ffmpeg = Platform.Ffmpeg ?? throw new UserFacingException("Video and audio need FFmpeg: choose “Add video support” in the tray menu.");
        var psi = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true,
        };
        foreach (var a in (string[])["-hide_banner", "-nostdin", "-loglevel", "error", "-i", Path.GetFullPath(source)]) psi.ArgumentList.Add(a);
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.ArgumentList.Add(output);

        using var p = Process.Start(psi) ?? throw new UserFacingException("Couldn't start FFmpeg.");
        var stderr = p.StandardError.ReadToEndAsync();
        p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode == 0 && File.Exists(output)) return;

        try { File.Delete(output); } catch { /* best effort */ }
        var log = stderr.Result;
        var name = Path.GetFileName(source);
        if (log.Contains("matches no streams") || log.Contains("does not contain any stream"))
            throw new UserFacingException($"“{name}” has no sound to save.");
        var last = log.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
        throw new UserFacingException($"{name}: {last ?? $"FFmpeg failed (exit code {p.ExitCode})"}");
    }
}
