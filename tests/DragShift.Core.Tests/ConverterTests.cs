using System.IO.Compression;
using System.Xml.Linq;
using DragShift.Core;
using ImageMagick;

namespace DragShift.Core.Tests;

[Collection("Platform")]
public sealed class ConverterTests : IDisposable
{
    readonly TestFiles files = new();

    public void Dispose()
    {
        Platform.Pdf = null;
        Platform.Heic = null;
        files.Dispose();
    }

    [Theory]
    [InlineData(TargetFormat.Jpg, MagickFormat.Jpeg)]
    [InlineData(TargetFormat.Webp, MagickFormat.WebP)]
    [InlineData(TargetFormat.Avif, MagickFormat.Avif)]
    [InlineData(TargetFormat.Bmp, MagickFormat.Bmp)]
    [InlineData(TargetFormat.Tiff, MagickFormat.Tiff)]
    public void Png_converts_to_raster_formats_next_to_the_source(TargetFormat target, MagickFormat expected)
    {
        var src = files.Photo();
        var outputs = Converter.Convert(src, target);

        var output = Assert.Single(outputs);
        Assert.Equal(files.Dir, Path.GetDirectoryName(output));
        Assert.Equal("photo" + Formats.Extension(target), Path.GetFileName(output));
        using var img = new MagickImage(output);
        Assert.Equal(expected, img.Format);
        Assert.Equal(640u, img.Width);
        Assert.True(File.Exists(src), "source must be left in place");
    }

    [Fact]
    public void Jpg_output_flattens_transparency_onto_white()
    {
        var output = Assert.Single(Converter.Convert(files.Transparent(), TargetFormat.Jpg));
        using var img = new MagickImage(output);
        var corner = img.GetPixels().GetPixel(0, 0).ToColor()!;
        Assert.True(corner.R > 245 && corner.G > 245 && corner.B > 245, $"corner was {corner}");
    }

    [Fact]
    public void Existing_output_is_never_overwritten()
    {
        var src = files.Photo();
        File.WriteAllText(files.Path("photo.jpg"), "keep me");
        var output = Assert.Single(Converter.Convert(src, TargetFormat.Jpg));
        Assert.Equal("photo (2).jpg", Path.GetFileName(output));
        Assert.Equal("keep me", File.ReadAllText(files.Path("photo.jpg")));
    }

    [Fact]
    public void Same_format_conversion_is_refused()
    {
        var src = files.Photo();
        Assert.False(Formats.CanConvert(src, TargetFormat.Png));
        Assert.Throws<UserFacingException>(() => Converter.Convert(src, TargetFormat.Png));
    }

    [Fact]
    public void Animated_gif_keeps_frames_in_webp_and_first_frame_in_png()
    {
        var gif = files.AnimatedGif();
        using (var webp = new MagickImageCollection(Assert.Single(Converter.Convert(gif, TargetFormat.Webp))))
            Assert.Equal(3, webp.Count);

        using var png = new MagickImageCollection(Assert.Single(Converter.Convert(gif, TargetFormat.Png)));
        Assert.Single(png);
        var c = png[0].GetPixels().GetPixel(5, 5).ToColor()!;
        Assert.True(c.R > 200 && c.G < 50, "first frame (red) expected");
    }

    [Fact]
    public void Image_converts_to_pdf()
    {
        var output = Assert.Single(Converter.Convert(files.Photo(), TargetFormat.Pdf));
        var head = File.ReadAllBytes(output).Take(5).ToArray();
        Assert.Equal("%PDF-"u8.ToArray(), head);
    }

    [Fact]
    public void Image_converts_to_a_valid_docx()
    {
        var output = Assert.Single(Converter.Convert(files.Photo(), TargetFormat.Docx));
        using var zip = ZipFile.OpenRead(output);
        var names = zip.Entries.Select(e => e.FullName).ToHashSet();
        Assert.Contains("[Content_Types].xml", names);
        Assert.Contains("word/document.xml", names);
        Assert.Contains("word/media/image1.png", names);
        foreach (var xml in zip.Entries.Where(e => e.FullName.EndsWith(".xml") || e.FullName.EndsWith(".rels")))
        {
            using var s = xml.Open();
            XDocument.Load(s); // throws on malformed XML
        }
    }

    [Fact]
    public void Docx_pictures_fit_the_page()
    {
        var (cx, cy) = DocxWriter.FitEmu(new DocxWriter.Picture([], 6000, 3000, 96, 96));
        Assert.Equal((long)(6.5 * 914400), cx);
        Assert.Equal(cx / 2, cy);
        var (sx, sy) = DocxWriter.FitEmu(new DocxWriter.Picture([], 96, 48, 96, 96));
        Assert.Equal((914400L, 457200L), (sx, sy));
    }

    [Fact]
    public void Multi_page_pdf_becomes_one_image_per_page_or_one_tiff()
    {
        Platform.Pdf = new FakePdf(3);
        var pdf = files.Path("doc.pdf");
        File.WriteAllText(pdf, "%PDF-1.4 fake");

        var pngs = Converter.Convert(pdf, TargetFormat.Png);
        Assert.Equal(["doc Page 1.png", "doc Page 2.png", "doc Page 3.png"], pngs.Select(Path.GetFileName));

        using var tiff = new MagickImageCollection(Assert.Single(Converter.Convert(pdf, TargetFormat.Tiff)));
        Assert.Equal(3, tiff.Count);

        using var docx = ZipFile.OpenRead(Assert.Single(Converter.Convert(pdf, TargetFormat.Docx)));
        Assert.Equal(3, docx.Entries.Count(e => e.FullName.StartsWith("word/media/")));
    }

    [Fact]
    public void Pdf_without_a_rasterizer_fails_with_a_user_message()
    {
        var pdf = files.Path("doc.pdf");
        File.WriteAllText(pdf, "%PDF-1.4 fake");
        var e = Assert.Throws<UserFacingException>(() => Converter.Convert(pdf, TargetFormat.Png));
        Assert.Contains("PDF", e.Message);
    }

    [Fact]
    public void Heic_output_goes_through_the_platform_encoder()
    {
        var src = files.Photo();
        Assert.Throws<UserFacingException>(() => Converter.Convert(src, TargetFormat.Heic));

        byte[]? received = null;
        Platform.Heic = new FakeHeic(png => received = png);
        var output = Assert.Single(Converter.Convert(src, TargetFormat.Heic));
        Assert.Equal("photo.heic", Path.GetFileName(output));
        using var sent = new MagickImage(received!);
        Assert.Equal(MagickFormat.Png, sent.Format);
    }

    [Fact]
    public void Unsupported_files_are_rejected()
    {
        var txt = files.Path("notes.txt");
        File.WriteAllText(txt, "hi");
        Assert.Equal(FileKind.Unsupported, Formats.KindOf(txt));
        Assert.Throws<UserFacingException>(() => Converter.Convert(txt, TargetFormat.Png));
    }

    sealed class FakeHeic(Action<byte[]> onEncode) : IHeicEncoder
    {
        public byte[] EncodeHeic(byte[] png, int quality)
        {
            onEncode(png);
            return [0, 0, 0, 24, (byte)'f', (byte)'t', (byte)'y', (byte)'p'];
        }
    }
}
