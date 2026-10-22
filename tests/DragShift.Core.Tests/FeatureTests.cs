using System.Diagnostics;
using DragShift.Core;
using ImageMagick;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Writer;

namespace DragShift.Core.Tests;

/// <summary>Combine to PDF, Resize, Copy text and video/audio (the last only where FFmpeg is installed).</summary>
[Collection("Platform")]
public sealed class FeatureTests : IDisposable
{
    readonly TestFiles files = new();

    public void Dispose()
    {
        Platform.Pdf = null;
        Platform.Ocr = null;
        Platform.Ffmpeg = null;
        files.Dispose();
    }

    string TextPdf(string name, params string[] pages)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(UglyToad.PdfPig.Fonts.Standard14Fonts.Standard14Font.Helvetica);
        foreach (var text in pages)
            builder.AddPage(UglyToad.PdfPig.Content.PageSize.A4).AddText(text, 14, new UglyToad.PdfPig.Core.PdfPoint(72, 700), font);
        var path = files.Path(name);
        File.WriteAllBytes(path, builder.Build());
        return path;
    }

    // ---- Combine ----

    [Fact]
    public void Images_and_pdfs_combine_into_one_pdf_in_name_order()
    {
        var img10 = files.Photo("scan 10.png");
        var img2 = files.Photo("scan 2.png");
        var pdf = TextPdf("scan 1.pdf", "First page text here", "Second page text here");

        var output = Converter.Combine([img10, pdf, img2]);

        Assert.Equal("scan 1 Combined.pdf", Path.GetFileName(output));
        using var doc = PdfDocument.Open(output);
        Assert.Equal(4, doc.NumberOfPages);
        // The PDF's text layer survives, and it comes first ("1" < "2" < "10").
        Assert.Contains("First page", doc.GetPage(1).Text);
        Assert.Contains("Second page", doc.GetPage(2).Text);
        Assert.Empty(doc.GetPage(3).GetWords());
    }

    [Fact]
    public void Natural_order_compares_numbers_by_value()
    {
        var names = new[] { "img10.jpg", "IMG2.jpg", "img1.jpg", "img02b.jpg", "a.jpg" };
        Assert.Equal(["a.jpg", "img1.jpg", "IMG2.jpg", "img02b.jpg", "img10.jpg"], names.OrderBy(n => n, NaturalComparer.Instance));
    }

    // ---- Resize ----

    [Theory]
    [InlineData(50, null, 320u)]
    [InlineData(null, 400, 400u)]
    public void Resize_shrinks_the_long_side(int? percent, int? longSide, uint expectedWidth)
    {
        var output = Tools.Resize(files.Photo(), new ResizeSpec(percent, longSide));
        using var img = new MagickImage(output);
        Assert.Equal(expectedWidth, img.Width);
        Assert.Equal(expectedWidth * 3 / 4, img.Height);
        Assert.Contains(new ResizeSpec(percent, longSide).Label, Path.GetFileName(output));
    }

    [Fact]
    public void Resize_never_enlarges()
    {
        var src = files.Photo();
        var e = Assert.Throws<UserFacingException>(() => Tools.Resize(src, new ResizeSpec(LongSide: 1920)));
        Assert.Contains("640 px", e.Message);
    }

    // ---- Copy text ----

    sealed class FakeOcr(string text) : ITextRecognizer
    {
        public int Calls;
        public string Recognize(byte[] png)
        {
            Calls++;
            using var _ = new MagickImage(png);
            return text;
        }
    }

    [Fact]
    public void Copy_text_reads_the_pdf_text_layer_without_ocr()
    {
        var pdf = TextPdf("doc.pdf", "Hello from page one", "And from page two");
        var text = TextExtractor.Extract(pdf);
        Assert.Contains("Hello from page one", text);
        Assert.Contains("And from page two", text);
    }

    [Fact]
    public void Copy_text_ocrs_images_and_scanned_pdf_pages()
    {
        var ocr = new FakeOcr("Scanned words");
        Platform.Ocr = ocr;
        Platform.Pdf = new FakePdf(1);
        Assert.Equal("Scanned words", TextExtractor.Extract(files.Photo()));

        var builder = new PdfDocumentBuilder();
        builder.AddPage(UglyToad.PdfPig.Content.PageSize.A4);
        var scanned = files.Path("scan.pdf");
        File.WriteAllBytes(scanned, builder.Build());
        Assert.Equal("Scanned words", TextExtractor.Extract(scanned));
        Assert.Equal(2, ocr.Calls);
    }

    [Fact]
    public void Copy_text_is_offered_for_images_only_with_ocr()
    {
        var photo = files.Photo();
        Assert.False(ToolInfo.CanApply(photo, ToolKind.CopyText));
        Assert.True(ToolInfo.CanApply("doc.pdf", ToolKind.CopyText));
        Platform.Ocr = new FakeOcr("");
        Assert.True(ToolInfo.CanApply(photo, ToolKind.CopyText));
    }

    // ---- What the wheel offers ----

    [Fact]
    public void Video_and_audio_need_ffmpeg()
    {
        Assert.NotNull(Formats.WhyNot("clip.mp4", TargetFormat.Gif));
        Assert.False(ToolInfo.CanApply("clip.mp4", ToolKind.Compress));
        Platform.Ffmpeg = "ffmpeg";
        Assert.True(Formats.CanConvert("clip.mov", TargetFormat.Mp4));
        Assert.True(Formats.CanConvert("clip.mp4", TargetFormat.Gif));
        Assert.True(Formats.CanConvert("clip.mp4", TargetFormat.Mp3));
        Assert.True(Formats.CanConvert("anim.gif", TargetFormat.Mp4));
        Assert.True(Formats.CanConvert("song.wav", TargetFormat.Mp3));
        Assert.True(ToolInfo.CanApply("clip.mp4", ToolKind.Compress));
        Assert.True(ToolInfo.CanApply("song.flac", ToolKind.Metadata));

        Assert.False(Formats.CanConvert("clip.mp4", TargetFormat.Mp4));
        Assert.False(Formats.CanConvert("clip.mp4", TargetFormat.Png));
        Assert.False(Formats.CanConvert("song.wav", TargetFormat.Mp4));
        Assert.False(Formats.CanConvert("photo.jpg", TargetFormat.Mp4));
        Assert.False(Formats.CanConvert("doc.pdf", TargetFormat.Gif));
        Assert.False(ToolInfo.CanApply("clip.mp4", ToolKind.Crop));
        Assert.False(ToolInfo.CanApply("song.flac", ToolKind.Compress));
    }

    [Fact]
    public void Images_convert_to_gif_keeping_animation()
    {
        var gif = Assert.Single(Converter.Convert(files.Photo(), TargetFormat.Gif));
        using (var still = new MagickImageCollection(gif)) Assert.Single(still);

        var webp = Assert.Single(Converter.Convert(files.AnimatedGif(), TargetFormat.Webp));
        var back = Assert.Single(Converter.Convert(webp, TargetFormat.Gif));
        using var frames = new MagickImageCollection(back);
        Assert.Equal(3, frames.Count);
    }

    // ---- FFmpeg (skipped when it isn't installed) ----

    string? Clip()
    {
        if (Media.FindFfmpeg() is not { } ffmpeg) return null;
        Platform.Ffmpeg = ffmpeg;
        var clip = files.Path("clip.mov");
        using var p = Process.Start(new ProcessStartInfo(ffmpeg,
            $"-hide_banner -loglevel error -f lavfi -i testsrc=size=320x240:rate=25:duration=1 -f lavfi -i sine=duration=1 -c:v libx264 -pix_fmt yuv420p -c:a aac -shortest -metadata title=Secret \"{clip}\"")
            { UseShellExecute = false, CreateNoWindow = true })!;
        p.WaitForExit();
        return clip;
    }

    [Fact]
    public void Video_converts_with_ffmpeg()
    {
        if (Clip() is not { } clip) return;
        foreach (var (target, name) in new[] { (TargetFormat.Mp4, "clip.mp4"), (TargetFormat.Gif, "clip.gif"), (TargetFormat.Mp3, "clip.mp3"), (TargetFormat.Webm, "clip.webm") })
        {
            var output = Assert.Single(Converter.Convert(clip, target));
            Assert.Equal(name, Path.GetFileName(output));
            Assert.True(new FileInfo(output).Length > 1000, $"{name} is too small");
        }
        using var gif = new MagickImageCollection(files.Path("clip.gif"));
        Assert.True(gif.Count > 5, "GIF should be animated");
    }

    [Fact]
    public void Video_metadata_is_stripped_without_reencoding()
    {
        if (Clip() is not { } clip) return;
        var output = Tools.RunInstant(clip, ToolKind.Metadata);
        Assert.Equal("clip Clean.mov", Path.GetFileName(output));
        Assert.DoesNotContain("Secret", File.ReadAllText(output));
        Assert.Contains("Secret", File.ReadAllText(clip));
    }

    [Fact]
    public void Silent_video_to_mp3_explains_itself()
    {
        if (Clip() is null) return;
        var silent = files.Path("silent.mp4");
        using (var p = Process.Start(new ProcessStartInfo(Platform.Ffmpeg!,
                   $"-hide_banner -loglevel error -f lavfi -i testsrc=size=160x120:rate=10:duration=1 -c:v libx264 -pix_fmt yuv420p \"{silent}\"")
                   { UseShellExecute = false, CreateNoWindow = true })!)
            p.WaitForExit();
        var e = Assert.Throws<UserFacingException>(() => Converter.Convert(silent, TargetFormat.Mp3));
        Assert.Contains("no sound", e.Message);
        Assert.False(File.Exists(files.Path("silent.mp3")));
    }
}
