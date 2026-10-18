using DragShift.Core;
using ImageMagick;
using ImageMagick.Drawing;

namespace DragShift.Core.Tests;

/// <summary>A fresh temp folder per test, plus generators for sample inputs.</summary>
public sealed class TestFiles : IDisposable
{
    public string Dir { get; } = Directory.CreateTempSubdirectory("dragshift-tests-").FullName;

    public string Path(string name) => System.IO.Path.Combine(Dir, name);

    /// <summary>A photo-like image: gradient sky, a red disc "subject", some noise so lossy codecs have work to do.</summary>
    public string Photo(string name = "photo.png", uint w = 640, uint h = 480)
    {
        using var img = new MagickImage("gradient:#87ceeb-#f5deb3", w, h);
        new Drawables().FillColor(new MagickColor("#d02020")).StrokeColor(MagickColors.Transparent)
            .Circle(w / 2.0, h / 2.0, w / 2.0 + Math.Min(w, h) / 4.0, h / 2.0).Draw(img);
        img.AddNoise(NoiseType.Gaussian, 0.4);
        var path = Path(name);
        img.Write(path);
        return path;
    }

    public string Transparent(string name = "logo.png")
    {
        using var img = new MagickImage(MagickColors.Transparent, 200, 120);
        new Drawables().FillColor(MagickColors.Blue).Rectangle(40, 20, 160, 100).Draw(img);
        var path = Path(name);
        img.Write(path);
        return path;
    }

    public string AnimatedGif(string name = "anim.gif")
    {
        using var frames = new MagickImageCollection();
        foreach (var c in new[] { MagickColors.Red, MagickColors.Green, MagickColors.Blue })
        {
            var f = new MagickImage(c, 60, 40) { AnimationDelay = 10 };
            frames.Add(f);
        }
        var path = Path(name);
        frames.Write(path);
        return path;
    }

    public void Dispose()
    {
        try { Directory.Delete(Dir, true); } catch { /* best effort */ }
    }
}

/// <summary>Stand-in for Windows.Data.Pdf: "renders" a fixed number of solid-color pages.</summary>
public sealed class FakePdf(int pages) : IPdfRasterizer
{
    public IReadOnlyList<byte[]> RenderPages(string pdfPath, int dpi)
    {
        var colors = new[] { MagickColors.White, MagickColors.LightGray, MagickColors.Yellow };
        return Enumerable.Range(0, pages).Select(i =>
        {
            using var img = new MagickImage(colors[i % colors.Length], 170, 220) { Format = MagickFormat.Png };
            return img.ToByteArray();
        }).ToList();
    }
}

/// <summary>Tests that swap <see cref="Platform"/> services must not run in parallel with each other.</summary>
[CollectionDefinition("Platform", DisableParallelization = true)]
public sealed class PlatformCollection;
