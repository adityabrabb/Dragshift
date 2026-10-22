using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DragShift.App.Editors;
using DragShift.Core;
using ImageMagick;
using ImageMagick.Drawing;

namespace DragShift.App;

/// <summary>
/// <c>DragShift.exe --smoke-test &lt;dir&gt;</c>: opens the wheel, every editor and a toast on a sample image, writes a
/// screenshot of each to the folder and exercises the save paths. Used by CI to catch UI regressions without a person.
/// </summary>
static class SmokeTest
{
    public static int Run(string dir)
    {
        Directory.CreateDirectory(dir);
        var failures = new List<string>();
        var log = Path.Combine(dir, "smoke.log");
        File.WriteAllText(log, "");
        void Log(string line)
        {
            Console.WriteLine(line);
            File.AppendAllText(log, line + Environment.NewLine);
        }
        // SMOKE_ONLY=step,step runs just those steps (for tracking down a failure).
        var only = Environment.GetEnvironmentVariable("SMOKE_ONLY")?.Split(',');
        void Step(string name, Action action)
        {
            if (only is not null && !only.Contains(name)) return;
            try
            {
                action();
                Log($"ok   {name}");
            }
            catch (Exception e)
            {
                failures.Add(name);
                Log($"FAIL {name}: {e}");
            }
        }

        var sample = Path.Combine(dir, "sample.jpg");
        using (var img = new MagickImage("gradient:#87ceeb-#f5deb3", 1200, 800))
        {
            new Drawables().FillColor(new MagickColor("#d02020")).Circle(600, 400, 800, 400).Draw(img);
            img.Write(sample);
        }

        Step("wheel", () =>
        {
            var wheel = new WheelWindow();
            wheel.ShowPinned();
            wheel.SimulateHover([sample], new WheelAction.Convert(TargetFormat.Webp));
            Pump(400);
            Capture((FrameworkElement)wheel.Content, Path.Combine(dir, "wheel.png"));
            wheel.SimulateHover([sample], new WheelAction.Tool(ToolKind.Crop));
            Pump(100);
            Capture((FrameworkElement)wheel.Content, Path.Combine(dir, "wheel-tool.png"));
            wheel.Close();
        });

        Step("toast", () =>
        {
            Toast.Done("Converted to PNG", [Path.Combine(dir, "sample.png")], []);
            Pump(300);
            var toast = Application.Current.Windows.OfType<Toast>().Single();
            Capture((FrameworkElement)toast.Content, Path.Combine(dir, "toast.png"));
            Toast.Dismiss();
        });

        using var full = new MagickImage(sample);
        var editors = new (string name, Func<EditorWindow> make)[]
        {
            ("crop", () => new CropEditor(sample, full)),
            ("redact", () => new RedactEditor(sample, full)),
            ("annotate", () => new AnnotateEditor(sample, full)),
            ("edit", () => new AdjustEditor(sample, full)),
            ("addbg", () => new AddBackgroundEditor(sample, full)),
        };
        foreach (var (name, make) in editors)
        {
            Step($"editor:{name}", () =>
            {
                var editor = make();
                editor.Show();
                editor.SmokeSetup();
                Pump(3000);
                Capture((FrameworkElement)editor.Content, Path.Combine(dir, $"editor-{name}.png"));
                if (editor.SmokeSave() is { } output)
                {
                    if (!File.Exists(output)) throw new InvalidOperationException($"save didn't write {output}");
                    Log($"     saved {Path.GetFileName(output)}");
                }
                editor.Close();
            });
        }

        Step("convert", () =>
        {
            foreach (var f in new[] { TargetFormat.Avif, TargetFormat.Webp, TargetFormat.Docx })
                Log($"     saved {Path.GetFileName(Converter.Convert(sample, f).Single())}");
        });

        Step("remove-bg", () =>
        {
            // Exercises the bundled ONNX model and native runtime inside the single-file exe.
            var output = Tools.RemoveBackground(sample);
            using var img = new MagickImage(output);
            var center = img.GetPixels().GetPixel(600, 400).ToColor()!;
            var corner = img.GetPixels().GetPixel(5, 5).ToColor()!;
            if (center.A < 200 || corner.A > 60) throw new InvalidOperationException($"mask looks wrong: center {center.A}, corner {corner.A}");
            Log($"     saved {Path.GetFileName(output)}");
        });

        Step("pdf", () =>
        {
            // PDFium on a PDF made by our own converter, under a name that isn't plain ASCII.
            var pdf = Path.Combine(dir, "Résumé ü.pdf");
            File.Move(Converter.Convert(sample, TargetFormat.Pdf).Single(), pdf);
            var back = Converter.Convert(pdf, TargetFormat.Png).Single();
            using var img = new MagickImage(back);
            if (img.Width < 100) throw new InvalidOperationException("PDF page rendered too small");
            var center = img.GetPixels().GetPixel((int)img.Width / 2, (int)img.Height / 2).ToColor()!;
            if (center.R < 150 || center.G > 100) throw new InvalidOperationException($"PDF page lost its picture: center is {center}");
        });

        Step("heic", () =>
        {
            // Offered only when the HEIF/HEVC codecs are installed; when offered it must work.
            if (Platform.Heic is null)
            {
                if (Formats.CanConvert(sample, TargetFormat.Heic)) throw new InvalidOperationException("HEIC offered without an encoder");
                Log("     HEIC codecs not installed: HEIC is hidden from the wheel");
                return;
            }
            var output = Converter.Convert(sample, TargetFormat.Heic).Single();
            using var img = new MagickImage(output);
            if (img.Width != 1200) throw new InvalidOperationException("HEIC round-trip lost the image");
            Log($"     saved {Path.GetFileName(output)}");
        });

        Step("pdf-to-docx", () =>
        {
            // A PDF with a real text layer must come out as text, not pictures.
            var builder = new UglyToad.PdfPig.Writer.PdfDocumentBuilder();
            var font = builder.AddStandard14Font(UglyToad.PdfPig.Fonts.Standard14Fonts.Standard14Font.Helvetica);
            builder.AddPage(UglyToad.PdfPig.Content.PageSize.A4).AddText("Hello from a text PDF", 14, new UglyToad.PdfPig.Core.PdfPoint(72, 700), font);
            var pdf = Path.Combine(dir, "text.pdf");
            File.WriteAllBytes(pdf, builder.Build());
            var docx = Converter.Convert(pdf, TargetFormat.Docx).Single();
            using var zip = System.IO.Compression.ZipFile.OpenRead(docx);
            using var reader = new StreamReader(zip.GetEntry("word/document.xml")!.Open());
            if (!reader.ReadToEnd().Contains("Hello from a text PDF")) throw new InvalidOperationException("text missing from DOCX");
            Log($"     saved {Path.GetFileName(docx)} with the PDF's text");
        });

        Step("resize", () =>
        {
            var picker = ResizePicker.ForSmokeTest();
            picker.Show();
            Pump(300);
            Capture((FrameworkElement)picker.Content, Path.Combine(dir, "resize-picker.png"));
            picker.Close();
            var output = Tools.Resize(sample, new ResizeSpec(Percent: 50));
            using var img = new MagickImage(output);
            if (img.Width != 600) throw new InvalidOperationException($"50% of 1200 came out {img.Width} wide");
            Log($"     saved {Path.GetFileName(output)}");
        });

        Step("combine-pdf", () =>
        {
            // Images and an existing PDF dropped together on PDF → one PDF with every page.
            var pdf = Converter.Convert(sample, TargetFormat.Pdf).Single();
            var png = Converter.Convert(sample, TargetFormat.Png).Single();
            var combined = Converter.Combine([png, pdf, sample]);
            using var doc = UglyToad.PdfPig.PdfDocument.Open(combined);
            if (doc.NumberOfPages != 3) throw new InvalidOperationException($"expected 3 pages, got {doc.NumberOfPages}");
            var wheel = new WheelWindow();
            wheel.ShowPinned();
            wheel.SimulateHover([png, pdf], new WheelAction.Convert(TargetFormat.Pdf));
            Pump(300);
            Capture((FrameworkElement)wheel.Content, Path.Combine(dir, "wheel-combine.png"));
            wheel.Close();
            Log($"     saved {Path.GetFileName(combined)}");
        });

        Step("copy-text", () =>
        {
            if (Platform.Ocr is null)
            {
                if (ToolInfo.CanApply(sample, ToolKind.CopyText)) throw new InvalidOperationException("Copy text offered without OCR");
                Log("     no OCR language installed: Copy text is only offered for PDFs");
                return;
            }
            var shot = Path.Combine(dir, "text.png");
            using (var img = new MagickImage(MagickColors.White, 900, 200))
            {
                new Drawables().FontPointSize(48).FillColor(MagickColors.Black).Text(40, 120, "Invoice number 12345").Draw(img);
                img.Write(shot);
            }
            var text = TextExtractor.Extract(shot);
            if (!text.Contains("12345")) throw new InvalidOperationException($"OCR read \"{text}\"");
            Log($"     read \"{text.ReplaceLineEndings(" ")}\"");
        });

        Step("video", () =>
        {
            if (Platform.Ffmpeg is not { } ffmpeg)
            {
                if (Formats.CanConvert("clip.mp4", TargetFormat.Gif)) throw new InvalidOperationException("video offered without FFmpeg");
                Log("     FFmpeg not found: video slices are greyed out");
                return;
            }
            var clip = Path.Combine(dir, "clip.mov");
            var make = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(ffmpeg,
                $"-hide_banner -loglevel error -f lavfi -i testsrc=size=640x360:rate=25:duration=2 -f lavfi -i sine=duration=2 -c:v libx264 -pix_fmt yuv420p -c:a aac -shortest \"{clip}\"")
                { UseShellExecute = false, CreateNoWindow = true })!;
            make.WaitForExit();
            foreach (var f in new[] { TargetFormat.Mp4, TargetFormat.Webm, TargetFormat.Gif, TargetFormat.Mp3 })
                Log($"     saved {Path.GetFileName(Converter.Convert(clip, f).Single())}");
            Log($"     saved {Path.GetFileName(Tools.RunInstant(clip, ToolKind.Metadata))}");
            var wheel = new WheelWindow();
            wheel.ShowPinned();
            wheel.SimulateHover([clip], new WheelAction.Convert(TargetFormat.Gif));
            Pump(300);
            Capture((FrameworkElement)wheel.Content, Path.Combine(dir, "wheel-video.png"));
            wheel.Close();
        });

        Log(failures.Count == 0 ? "SMOKE TEST PASSED" : $"SMOKE TEST FAILED: {string.Join(", ", failures)}");
        return failures.Count == 0 ? 0 : 1;
    }

    /// <summary>Runs the dispatcher for a while so layout, animations and background renders complete.</summary>
    static void Pump(int ms)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    static void Capture(FrameworkElement element, string path)
    {
        element.UpdateLayout();
        var w = (int)Math.Ceiling(element.ActualWidth);
        var h = (int)Math.Ceiling(element.ActualHeight);
        if (w == 0 || h == 0) throw new InvalidOperationException($"{Path.GetFileName(path)}: element has no size");
        var bitmap = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        File.WriteAllBytes(path, Imaging.EncodePng(bitmap));
    }
}
