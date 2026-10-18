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
        void Step(string name, Action action)
        {
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
            // Windows' PDF engine on a PDF made by our own converter.
            var pdf = Converter.Convert(sample, TargetFormat.Pdf).Single();
            var back = Converter.Convert(pdf, TargetFormat.Png).Single();
            using var img = new MagickImage(back);
            if (img.Width < 100) throw new InvalidOperationException("PDF page rendered too small");
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
