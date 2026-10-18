using ImageMagick;
using ImageMagick.Drawing;

namespace DragShift.Core;

public enum FrameRatio { Auto, Square, Wide, Portrait }

/// <summary>A soft color blob of a mesh gradient, positioned in 0..1 canvas coordinates.</summary>
public sealed record ColorBlob(string Color, double X, double Y);

/// <summary>The background behind the picture: a mesh gradient, a solid color, or an image file (cover-fit).</summary>
public abstract record Backdrop
{
    public sealed record Mesh(IReadOnlyList<ColorBlob> Blobs) : Backdrop;
    public sealed record Solid(string Color) : Backdrop;
    public sealed record Picture(string Path) : Backdrop;
}

public sealed record BackgroundOptions
{
    public Backdrop Backdrop { get; init; } = BackgroundPresets.Gradients[0];
    /// <summary>Space around the picture, in source pixels.</summary>
    public double Padding { get; init; }
    /// <summary>Corner radius of the picture, in source pixels.</summary>
    public double Corners { get; init; }
    /// <summary>Drop shadow blur, in source pixels. 0 = none.</summary>
    public double Shadow { get; init; }
    public FrameRatio Ratio { get; init; } = FrameRatio.Auto;

    /// <summary>Sensible starting values for an image of the given size.</summary>
    public static BackgroundOptions DefaultsFor(uint width, uint height)
    {
        var s = Math.Max(width, height);
        return new BackgroundOptions { Padding = Math.Round(s * 0.12), Corners = Math.Round(s * 0.03), Shadow = Math.Round(s * 0.02) };
    }

    public BackgroundOptions Scaled(double k) => this with { Padding = Padding * k, Corners = Corners * k, Shadow = Shadow * k };
}

public static class BackgroundPresets
{
    public static readonly Backdrop.Mesh[] Gradients =
    [
        Mesh(("#ff5f3a", 0.0, 1.0), ("#ffb347", 0.6, 0.0), ("#ff8a5c", 1.0, 0.5), ("#9b6dd6", 1.0, 1.0)),
        Mesh(("#e9eef7", 0.0, 0.0), ("#c3d3f0", 1.0, 0.2), ("#ffffff", 0.3, 1.0), ("#dfe6f5", 1.0, 1.0)),
        Mesh(("#ff6fb5", 0.0, 0.0), ("#ffd36e", 1.0, 0.0), ("#6fe3c8", 0.0, 1.0), ("#7a8cff", 1.0, 1.0)),
        Mesh(("#f6e3ff", 0.0, 0.0), ("#ffd6e8", 1.0, 0.3), ("#e5e9ff", 0.2, 1.0), ("#fbefff", 1.0, 1.0)),
        Mesh(("#f3b8d8", 0.0, 0.0), ("#c9b8f3", 1.0, 0.0), ("#f8d8ec", 0.5, 1.0)),
        Mesh(("#0f5132", 0.0, 0.0), ("#e4572e", 1.0, 0.0), ("#f3a712", 0.0, 1.0), ("#29335c", 1.0, 1.0)),
        Mesh(("#5b8def", 0.0, 0.0), ("#8ad7f5", 1.0, 0.0), ("#b388eb", 0.0, 1.0), ("#5ee0d0", 1.0, 1.0)),
        Mesh(("#33c3d6", 0.0, 0.0), ("#5be0b3", 1.0, 0.5), ("#4aa8ff", 0.2, 1.0)),
        Mesh(("#f7f2ff", 0.0, 0.0), ("#e9ddff", 1.0, 1.0), ("#ffffff", 0.5, 0.5)),
    ];

    public static readonly string[] Solids =
    [
        "#ffffff", "#f2c94c", "#8bc34a", "#2e9e5b", "#1f9c8c", "#3cc6d8", "#3462d6", "#1c2b3a", "#6c3fc9",
    ];

    static Backdrop.Mesh Mesh(params (string c, double x, double y)[] blobs) =>
        new(blobs.Select(b => new ColorBlob(b.c, b.x, b.y)).ToArray());
}

/// <summary>"Add BG": places a picture on a backdrop with padding, rounded corners and a soft shadow.</summary>
public static class BackgroundRenderer
{
    public static (uint w, uint h) CanvasSize(uint imgW, uint imgH, double padding, FrameRatio ratio)
    {
        double w = imgW + 2 * Math.Max(0, padding), h = imgH + 2 * Math.Max(0, padding);
        var r = ratio switch
        {
            FrameRatio.Square => 1.0,
            FrameRatio.Wide => 16.0 / 9.0,
            FrameRatio.Portrait => 4.0 / 5.0,
            _ => 0,
        };
        if (r > 0)
        {
            if (w / h < r) w = h * r;
            else h = w / r;
        }
        return ((uint)Math.Max(1, Math.Round(w)), (uint)Math.Max(1, Math.Round(h)));
    }

    /// <summary>The finished, fully opaque picture. The caller owns the result.</summary>
    public static MagickImage Render(MagickImage source, BackgroundOptions o)
    {
        var (cw, ch) = CanvasSize(source.Width, source.Height, o.Padding, o.Ratio);
        var canvas = Backdrop(o.Backdrop, cw, ch);
        var x = (int)Math.Round((cw - source.Width) / 2.0);
        var y = (int)Math.Round((ch - source.Height) / 2.0);
        var radius = Math.Clamp(o.Corners, 0, Math.Min(source.Width, source.Height) / 2.0);

        if (o.Shadow > 0.5)
        {
            var blur = o.Shadow;
            var margin = (int)Math.Ceiling(blur * 3);
            using var shadow = new MagickImage(MagickColors.Transparent, source.Width + (uint)(2 * margin), source.Height + (uint)(2 * margin));
            new Drawables().FillColor(new MagickColor(0, 0, 0, 115)).StrokeColor(MagickColors.Transparent)
                .RoundRectangle(margin, margin, margin + source.Width - 1, margin + source.Height - 1, radius, radius)
                .Draw(shadow);
            shadow.Blur(0, blur / 2);
            canvas.Composite(shadow, x - margin, y - margin + (int)Math.Round(blur / 3), CompositeOperator.Over);
        }

        using var picture = (MagickImage)source.Clone();
        if (radius >= 0.5)
        {
            picture.Alpha(AlphaOption.Set);
            using var mask = new MagickImage(MagickColors.Transparent, source.Width, source.Height);
            new Drawables().FillColor(MagickColors.White).StrokeColor(MagickColors.Transparent)
                .RoundRectangle(0, 0, source.Width - 1, source.Height - 1, radius, radius)
                .Draw(mask);
            picture.Composite(mask, CompositeOperator.DstIn);
        }
        canvas.Composite(picture, x, y, CompositeOperator.Over);
        canvas.Alpha(AlphaOption.Off);
        return canvas;
    }

    public static MagickImage Backdrop(Backdrop backdrop, uint w, uint h)
    {
        switch (backdrop)
        {
            case Core.Backdrop.Solid s:
                return new MagickImage(new MagickColor(s.Color), w, h);
            case Core.Backdrop.Picture p:
            {
                var img = ImageIO.LoadFirst(p.Path);
                img.Resize(new MagickGeometry(w, h) { FillArea = true });
                img.Crop(new MagickGeometry(w, h), Gravity.Center);
                img.ResetPage();
                img.Alpha(AlphaOption.Off);
                return img;
            }
            case Core.Backdrop.Mesh m:
                return MeshGradient(m.Blobs, w, h);
            default:
                throw new ArgumentOutOfRangeException(nameof(backdrop));
        }
    }

    /// <summary>
    /// Soft mesh gradient: every pixel is a Gaussian-weighted blend of the blob colors. Computed on a small grid and
    /// upscaled smoothly, which is exact enough for a field this smooth and keeps big canvases fast.
    /// </summary>
    public static MagickImage MeshGradient(IReadOnlyList<ColorBlob> blobs, uint w, uint h)
    {
        if (blobs.Count == 0) return new MagickImage(MagickColors.White, w, h);
        const int grid = 160;
        var gw = (int)Math.Min(grid, w);
        var gh = (int)Math.Min(grid, h);
        var colors = blobs.Select(b => new MagickColor(b.Color)).ToArray();
        var aspect = (double)w / h;
        var sigma2 = 2 * 0.42 * 0.42;
        var data = new byte[gw * gh * 3];
        for (var j = 0; j < gh; j++)
        for (var i = 0; i < gw; i++)
        {
            double u = (i + 0.5) / gw, v = (j + 0.5) / gh, r = 0, g = 0, b = 0, sum = 0;
            for (var k = 0; k < blobs.Count; k++)
            {
                double dx = (u - blobs[k].X) * Math.Sqrt(aspect), dy = (v - blobs[k].Y) / Math.Sqrt(aspect);
                var wt = Math.Exp(-(dx * dx + dy * dy) / sigma2) + 1e-6;
                r += colors[k].R * wt; g += colors[k].G * wt; b += colors[k].B * wt; sum += wt;
            }
            var o = (j * gw + i) * 3;
            data[o] = (byte)Math.Round(r / sum);
            data[o + 1] = (byte)Math.Round(g / sum);
            data[o + 2] = (byte)Math.Round(b / sum);
        }
        var img = new MagickImage();
        img.ReadPixels(data, new PixelReadSettings((uint)gw, (uint)gh, StorageType.Char, PixelMapping.RGB));
        img.FilterType = FilterType.Gaussian;
        img.Resize(new MagickGeometry(w, h) { IgnoreAspectRatio = true });
        return img;
    }
}
