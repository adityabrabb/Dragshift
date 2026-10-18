using ImageMagick;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.DocumentLayoutAnalysis;
using UglyToad.PdfPig.DocumentLayoutAnalysis.PageSegmenter;
using UglyToad.PdfPig.DocumentLayoutAnalysis.ReadingOrderDetector;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;

namespace DragShift.Core;

/// <summary>
/// PDF → editable Word content: text as paragraphs in reading order (multi-column aware) keeping font size, bold and
/// italic, with headings detected from size, and embedded images placed where they appear. Pages with no text layer
/// (scans) become a picture of the page.
/// </summary>
public static class PdfToDocx
{
    /// <summary>Pages with fewer words than this are treated as scanned.</summary>
    const int MinWordsForText = 3;
    /// <summary>Images smaller than this (in either dimension, in points) are decoration and skipped.</summary>
    const double MinImagePoints = 24;

    public static IReadOnlyList<DocBlock> Extract(string pdfPath)
    {
        PdfDocument doc;
        try
        {
            doc = PdfDocument.Open(pdfPath);
        }
        catch (Exception e)
        {
            throw new UserFacingException($"Couldn't read “{Path.GetFileName(pdfPath)}” (it may be password-protected or damaged).", e);
        }

        using (doc)
        {
            var blocks = new List<DocBlock>();
            foreach (var page in doc.GetPages())
            {
                if (blocks.Count > 0) blocks.Add(new DocBlock.PageBreak());
                var words = NearestNeighbourWordExtractor.Instance.GetWords(page.Letters).Where(w => !string.IsNullOrWhiteSpace(w.Text)).ToList();
                if (words.Count < MinWordsForText) blocks.Add(ScannedPage(pdfPath, page));
                else blocks.AddRange(TextPage(page, words));
            }
            return blocks;
        }
    }

    static IEnumerable<DocBlock> TextPage(Page page, List<Word> words)
    {
        var textBlocks = DocstrumBoundingBoxes.Instance.GetBlocks(words);
        var ordered = UnsupervisedReadingOrderDetector.Instance.Get(textBlocks).ToList();
        var bodySize = Median(words.Select(WordSize));

        // Images go before the first text block that starts below them.
        var images = Images(page).OrderByDescending(i => i.top).ToList();
        var output = new List<DocBlock>();
        foreach (var block in ordered)
        {
            while (images.Count > 0 && images[0].top > block.BoundingBox.Top)
            {
                output.Add(images[0].picture);
                images.RemoveAt(0);
            }
            if (Paragraph(block, bodySize, page.Width) is { } p) output.Add(p);
        }
        output.AddRange(images.Select(i => i.picture));
        return output;
    }

    static DocBlock.Paragraph? Paragraph(TextBlock block, double bodySize, double pageWidth)
    {
        var blockWords = block.TextLines.SelectMany(l => l.Words).ToList();
        if (blockWords.Count == 0) return null;
        var size = Median(blockWords.Select(WordSize));
        var heading = size >= bodySize * 1.25 && blockWords.Count <= 25;
        var sizePt = Math.Round(Math.Clamp(size, 6, 72) * 2) / 2;

        var runs = new List<DocRun>();
        var text = new System.Text.StringBuilder();
        bool? bold = null, italic = null;
        void Flush()
        {
            if (text.Length > 0) runs.Add(new DocRun(text.ToString(), sizePt, bold == true || heading, italic == true));
            text.Clear();
        }

        for (var li = 0; li < block.TextLines.Count; li++)
        {
            var line = block.TextLines[li];
            for (var wi = 0; wi < line.Words.Count; wi++)
            {
                var word = line.Words[wi];
                var (b, i) = Style(word);
                var separator = "";
                if (wi > 0) separator = " ";
                else if (li > 0)
                {
                    // Re-join words hyphenated across a line break; otherwise lines of a paragraph flow with a space.
                    var dehyphen = text.Length > 1 && text[^1] == '-' && char.IsLetter(text[^2]) && word.Text.Length > 0 && char.IsLower(word.Text[0]);
                    if (dehyphen) text.Length--;
                    separator = dehyphen ? "" : " ";
                }
                if (bold is not null && (b != bold || i != italic))
                {
                    text.Append(separator);
                    separator = "";
                    Flush();
                }
                bold = b;
                italic = i;
                text.Append(separator).Append(word.Text);
            }
        }
        Flush();

        var box = block.BoundingBox;
        var centered = heading && box.Width < pageWidth * 0.6 && Math.Abs(box.Centroid.X - pageWidth / 2) < pageWidth * 0.08;
        return new DocBlock.Paragraph(runs, centered);
    }

    static (bool bold, bool italic) Style(Word word)
    {
        var letter = word.Letters.Count > 0 ? word.Letters[0] : null;
        var name = letter?.FontName ?? "";
        var bold = letter?.FontDetails?.IsBold == true || ContainsAny(name, "Bold", "Black", "Heavy", "Semibold", "Demi");
        var italic = letter?.FontDetails?.IsItalic == true || ContainsAny(name, "Italic", "Oblique");
        return (bold, italic);
    }

    static bool ContainsAny(string s, params string[] parts) => parts.Any(p => s.Contains(p, StringComparison.OrdinalIgnoreCase));

    static double WordSize(Word w) => w.Letters.Count == 0 ? 0 : w.Letters.Average(l => l.PointSize);

    static double Median(IEnumerable<double> values)
    {
        var v = values.Where(x => x > 0).OrderBy(x => x).ToList();
        return v.Count == 0 ? 11 : v[v.Count / 2];
    }

    static IEnumerable<(double top, DocBlock.Picture picture)> Images(Page page)
    {
        foreach (var image in page.GetImages())
        {
            var b = image.BoundingBox;
            if (b.Width < MinImagePoints || b.Height < MinImagePoints) continue;
            var png = ImagePng(image);
            if (png is null) continue;
            using var info = new MagickImage(png);
            yield return (b.Top, new DocBlock.Picture(png, (int)info.Width, (int)info.Height, b.Width / 72, b.Height / 72));
        }
    }

    /// <summary>The image as PNG, decoding JPEG/JPEG 2000 streams with Magick when PdfPig can't. Null if undecodable.</summary>
    static byte[]? ImagePng(IPdfImage image)
    {
        try
        {
            if (image.TryGetPng(out var png)) return png;
            var raw = image.RawBytes.ToArray();
            if (raw.Length == 0) return null;
            using var img = new MagickImage(raw);
            return ImageIO.ToPng(img);
        }
        catch
        {
            return null;
        }
    }

    static DocBlock ScannedPage(string pdfPath, Page page)
    {
        var renderer = Platform.Pdf;
        if (renderer is null)
            return new DocBlock.Paragraph([new DocRun($"[Page {page.Number} has no text and couldn't be rendered on this system]", Italic: true)]);
        var png = renderer.RenderPages(pdfPath, ImageIO.PdfDpi, [page.Number - 1])[0];
        using var info = new MagickImage(png);
        return new DocBlock.Picture(png, (int)info.Width, (int)info.Height, page.Width / 72, page.Height / 72);
    }
}
