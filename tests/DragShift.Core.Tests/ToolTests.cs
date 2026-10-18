using DragShift.Core;
using ImageMagick;

namespace DragShift.Core.Tests;

public sealed class ToolTests : IDisposable
{
    readonly TestFiles files = new();

    public void Dispose() => files.Dispose();

    static IMagickColor<byte> At(string path, int x, int y)
    {
        using var img = new MagickImage(path);
        return img.GetPixels().GetPixel(x, y).ToColor()!;
    }

    [Fact]
    public void Compress_shrinks_a_large_photo_and_caps_its_size()
    {
        var src = files.Photo("big.jpg", 4000, 3000);
        // Re-save at max quality so there's something to gain.
        using (var img = new MagickImage(src)) { img.Quality = 100; img.Write(src); }

        var output = Tools.Compress(src);
        Assert.Equal("big Compressed.jpg", Path.GetFileName(output));
        Assert.True(new FileInfo(output).Length < new FileInfo(src).Length);
        using var result = new MagickImage(output);
        Assert.Equal((uint)Tools.CompressMaxSide, Math.Max(result.Width, result.Height));
    }

    [Fact]
    public void Compress_refuses_when_nothing_would_be_saved()
    {
        var src = files.Path("tiny.webp");
        using (var img = new MagickImage(MagickColors.White, 8, 8) { Quality = 10 }) img.Write(src);
        Assert.Throws<UserFacingException>(() => Tools.Compress(src));
        Assert.Single(Directory.GetFiles(files.Dir));
    }

    [Fact]
    public void Metadata_is_stripped()
    {
        var src = files.Path("exif.jpg");
        using (var img = new MagickImage(files.Photo()))
        {
            var exif = new ExifProfile();
            exif.SetValue(ExifTag.Make, "SecretCam");
            exif.SetValue(ExifTag.GPSLatitudeRef, "N");
            img.SetProfile(exif);
            img.Write(src);
        }
        using (var check = new MagickImage(src)) Assert.NotNull(check.GetExifProfile());

        var output = Tools.StripMetadata(src);
        Assert.Equal("exif Clean.jpg", Path.GetFileName(output));
        using var clean = new MagickImage(output);
        Assert.Null(clean.GetExifProfile());
    }

    [Fact]
    public void Crop_cuts_the_rectangle_and_clamps_it()
    {
        var output = Tools.Crop(files.Photo(), new PixelRect(600, 400, 500, 500));
        Assert.Equal("photo Cropped.png", Path.GetFileName(output));
        using var img = new MagickImage(output);
        Assert.Equal((40u, 80u), (img.Width, img.Height));
        Assert.Throws<UserFacingException>(() => Tools.Crop(files.Photo("p2.png"), new PixelRect(700, 0, 10, 10)));
    }

    [Theory]
    [InlineData(RedactStyle.Box)]
    [InlineData(RedactStyle.Pixelate)]
    [InlineData(RedactStyle.Blur)]
    public void Redact_changes_only_the_marked_area(RedactStyle style)
    {
        var src = files.Photo();
        var output = Tools.Redact(src, [new PixelRect(0, 0, 200, 200)], style);
        Assert.Equal("photo Redacted.png", Path.GetFileName(output));
        Assert.Equal(At(src, 500, 400), At(output, 500, 400));
        if (style == RedactStyle.Box)
        {
            var c = At(output, 100, 100);
            Assert.Equal((0, 0, 0), (c.R, c.G, c.B));
        }
        else
        {
            using var a = new MagickImage(src);
            using var b = new MagickImage(output);
            a.Crop(new MagickGeometry(0, 0, 200, 200));
            b.Crop(new MagickGeometry(0, 0, 200, 200));
            Assert.True(a.Compare(b, ErrorMetric.RootMeanSquared) > 0.005);
        }
    }

    [Fact]
    public void Edit_rotates_flips_and_scales()
    {
        var output = Tools.Edit(files.Photo(), new EditOptions { QuarterTurns = 1, FlipHorizontal = true, ScalePercent = 50 });
        Assert.Equal("photo Edited.png", Path.GetFileName(output));
        using var img = new MagickImage(output);
        Assert.Equal((240u, 320u), (img.Width, img.Height));
    }

    [Fact]
    public void Annotate_composites_the_overlay()
    {
        var src = files.Photo();
        using var overlay = new MagickImage(MagickColors.Transparent, 640, 480);
        new ImageMagick.Drawing.Drawables().FillColor(MagickColors.Lime).Rectangle(10, 10, 30, 30).Draw(overlay);
        overlay.Format = MagickFormat.Png;

        var output = Tools.Annotate(src, overlay.ToByteArray());
        var c = At(output, 20, 20);
        Assert.Equal((0, 255, 0), (c.R, c.G, c.B));
        Assert.Equal(At(src, 300, 300), At(output, 300, 300));
    }

    [Theory]
    [InlineData(FrameRatio.Auto, 100, 50, 20, 140, 90)]
    [InlineData(FrameRatio.Square, 100, 50, 20, 140, 140)]
    [InlineData(FrameRatio.Wide, 100, 100, 0, 178, 100)]
    [InlineData(FrameRatio.Portrait, 100, 50, 0, 100, 125)]
    public void Background_canvas_follows_padding_and_ratio(FrameRatio ratio, int w, int h, int pad, int ew, int eh)
    {
        Assert.Equal(((uint)ew, (uint)eh), BackgroundRenderer.CanvasSize((uint)w, (uint)h, pad, ratio));
    }

    [Fact]
    public void Add_background_pads_rounds_and_is_opaque()
    {
        var src = files.Photo("shot.png", 400, 300);
        var options = new BackgroundOptions { Backdrop = new Backdrop.Solid("#3462d6"), Padding = 50, Corners = 40, Shadow = 0 };
        var output = Tools.AddBackground(src, options);
        Assert.Equal("shot BG.png", Path.GetFileName(output));

        using var img = new MagickImage(output);
        Assert.Equal((500u, 400u), (img.Width, img.Height));
        Assert.False(img.HasAlpha);
        var bg = At(output, 5, 5);
        Assert.Equal((0x34, 0x62, 0xd6), (bg.R, bg.G, bg.B));
        // The picture's own corner is cut away by the rounding, so the backdrop shows there.
        var corner = At(output, 52, 52);
        Assert.Equal((0x34, 0x62, 0xd6), (corner.R, corner.G, corner.B));
        // Its center is the photo's red subject.
        var mid = At(output, 250, 200);
        Assert.True(mid.R > 150 && mid.B < 100);
    }

    [Fact]
    public void Add_background_with_mesh_gradient_and_shadow_renders()
    {
        var src = files.Photo("shot.jpg", 300, 200);
        var options = BackgroundOptions.DefaultsFor(300, 200) with { Ratio = FrameRatio.Square };
        var output = Tools.AddBackground(src, options);
        using var img = new MagickImage(output);
        Assert.Equal(img.Width, img.Height);
        Assert.Equal(MagickFormat.Jpeg, img.Format);
    }

    [Fact]
    public void Remove_background_keeps_the_subject_and_clears_the_surroundings()
    {
        var src = files.Path("subject.png");
        using (var img = new MagickImage(MagickColors.White, 400, 400))
        {
            new ImageMagick.Drawing.Drawables().FillColor(new MagickColor("#d02020")).Circle(200, 200, 300, 200).Draw(img);
            img.Write(src);
        }

        var output = Tools.RemoveBackground(src);
        Assert.Equal("subject No BG.png", Path.GetFileName(output));
        var center = At(output, 200, 200);
        var corner = At(output, 10, 10);
        Assert.True(center.A > 200, $"subject alpha {center.A}");
        Assert.True(corner.A < 60, $"background alpha {corner.A}");
    }

    [Fact]
    public void Output_names_count_up()
    {
        var src = files.Photo();
        Assert.Equal("photo Cropped.png", Path.GetFileName(OutputPaths.NextTo(src, ".png", " Cropped")));
        File.WriteAllText(files.Path("photo Cropped.png"), "");
        File.WriteAllText(files.Path("photo Cropped (2).png"), "");
        Assert.Equal("photo Cropped (3).png", Path.GetFileName(OutputPaths.NextTo(src, ".png", " Cropped")));
        Assert.Equal("photo (2).png", Path.GetFileName(OutputPaths.NextTo(src, ".png")));
    }
}
