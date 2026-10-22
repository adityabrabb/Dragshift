using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace DragShift.Core;

/// <summary>
/// "Copy text": the words in an image (by OCR) or a PDF (from its text layer, with OCR for scanned pages).
/// </summary>
public static class TextExtractor
{
    /// <summary>Pages with fewer words than this are treated as scanned and OCR'd when OCR is available.</summary>
    const int MinWordsForText = 3;
    const int OcrDpi = 200;

    public static string Extract(string path) => Formats.KindOf(path) switch
    {
        FileKind.Image => Recognize(ImageIO.ToPngFirst(path)),
        FileKind.Pdf => FromPdf(path),
        _ => throw new UserFacingException($"“{Path.GetFileName(path)}” has no text to copy."),
    };

    static string Recognize(byte[] png)
    {
        var ocr = Platform.Ocr ?? throw new UserFacingException("Text recognition isn't set up in Windows (Settings → Time & language → Language: add a language with OCR).");
        return ocr.Recognize(png).Trim();
    }

    static string FromPdf(string path)
    {
        PdfDocument doc;
        try
        {
            doc = PdfDocument.Open(path);
        }
        catch (Exception e)
        {
            throw new UserFacingException($"Couldn't read “{Path.GetFileName(path)}” (it may be password-protected or damaged).", e);
        }

        using (doc)
        {
            var pages = new List<string>();
            foreach (var page in doc.GetPages())
            {
                if (page.GetWords().Count() >= MinWordsForText)
                {
                    pages.Add(ContentOrderTextExtractor.GetText(page).Trim());
                }
                else if (Platform.Ocr is not null && Platform.Pdf is { } pdf)
                {
                    var png = pdf.RenderPages(path, OcrDpi, [page.Number - 1]).Single();
                    pages.Add(Recognize(png));
                }
            }
            return string.Join("\n\n", pages.Where(p => p.Length > 0));
        }
    }
}
