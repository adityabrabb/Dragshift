using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using DragShift.App.Editors;
using DragShift.Core;
using ImageMagick;

namespace DragShift.App;

/// <summary>Runs what was chosen on the wheel. Heavy work runs off the UI thread; results go to a toast.</summary>
static class Actions
{
    public static async void Run(string[] files, WheelAction action)
    {
        if (files.Length == 0) return;
        switch (action)
        {
            case WheelAction.Convert when WheelAction.IsCombine(action, files):
                await Batch([files[0]], "Combining into one PDF", _ => [Converter.Combine(files)], $"Combined {files.Length} files");
                break;
            case WheelAction.Convert c:
                await Batch(files, $"Converting to {Formats.Label(c.Format)}", f => Converter.Convert(f, c.Format), $"Converted to {Formats.Label(c.Format)}");
                break;
            case WheelAction.Tool { Kind: ToolKind.Resize }:
                if (ResizePicker.Ask(files.Length) is { } spec)
                    await Batch(files, $"Resizing to {spec.Label}", f => [Tools.Resize(f, spec)], $"Resized to {spec.Label}");
                break;
            case WheelAction.Tool { Kind: ToolKind.CopyText }:
                await CopyText(files);
                break;
            case WheelAction.Tool t when !ToolInfo.IsInteractive(t.Kind):
                await Batch(files, $"{action.Describe}…", f => [Tools.RunInstant(f, t.Kind)], action.Describe switch
                {
                    "Compress" => "Compressed",
                    "Strip metadata" => "Metadata removed",
                    "Remove background" => "Background removed",
                    var d => d,
                });
                break;
            case WheelAction.Tool t:
                // Editors open one file at a time.
                foreach (var file in files)
                {
                    if (!await Edit(file, t.Kind)) break;
                }
                break;
        }
    }

    static async Task Batch(string[] files, string progress, Func<string, IReadOnlyList<string>> work, string done)
    {
        Toast.Progress(progress, files.Length == 1 ? Path.GetFileName(files[0]) : $"{files.Length} files");
        var outputs = new List<string>();
        var errors = new List<string>();
        await Task.Run(() =>
        {
            // A couple at a time: these are CPU-heavy and Magick already uses several threads.
            Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = 2 }, file =>
            {
                try
                {
                    var written = work(file);
                    lock (outputs) outputs.AddRange(written);
                }
                catch (Exception e)
                {
                    lock (errors) errors.Add(Describe(file, e));
                }
            });
        });
        var title = errors.Count == 0 ? done : outputs.Count == 0 ? "Nothing was saved" : $"{done} (with {errors.Count} problem{(errors.Count > 1 ? "s" : "")})";
        Toast.Done(title, outputs, errors);
    }

    /// <summary>Reads the text in images (OCR) and PDFs and puts it on the clipboard. Nothing is saved.</summary>
    static async Task CopyText(string[] files)
    {
        Toast.Progress("Reading text…", files.Length == 1 ? Path.GetFileName(files[0]) : $"{files.Length} files");
        var errors = new List<string>();
        var texts = await Task.Run(() => files.Select(file =>
        {
            try
            {
                return TextExtractor.Extract(file);
            }
            catch (Exception e)
            {
                lock (errors) errors.Add(Describe(file, e));
                return "";
            }
        }).ToList());
        var text = string.Join("\n\n", texts.Where(t => t.Length > 0)).ReplaceLineEndings("\r\n");
        if (text.Length == 0)
        {
            Toast.Done(errors.Count > 0 ? "Couldn't read the text" : "No text found", [], errors);
            return;
        }
        try
        {
            Clipboard.SetText(text);
        }
        catch (Exception e)
        {
            // Another app can hold the clipboard open for a moment.
            Toast.Done("Couldn't use the clipboard", [], [e.Message]);
            return;
        }
        var preview = text.Length > 140 ? text[..140].TrimEnd() + "…" : text;
        var words = text.Split((char[])[' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries).Length;
        Toast.Done($"Copied {words} word{(words == 1 ? "" : "s")}", [], [preview.ReplaceLineEndings(" "), .. errors]);
    }

    /// <summary>Opens the editor for one file. Returns false if the user cancelled (stop processing further files).</summary>
    static async Task<bool> Edit(string file, ToolKind kind)
    {
        MagickImage image;
        try
        {
            Toast.Progress("Opening…", Path.GetFileName(file));
            image = await Task.Run(() => ImageIO.LoadFirst(file));
        }
        catch (Exception e)
        {
            Toast.Done("Couldn't open the file", [], [Describe(file, e)]);
            return true;
        }

        EditorWindow editor = kind switch
        {
            ToolKind.Crop => new CropEditor(file, image),
            ToolKind.Redact => new RedactEditor(file, image),
            ToolKind.Annotate => new AnnotateEditor(file, image),
            ToolKind.Edit => new AdjustEditor(file, image),
            ToolKind.AddBg => new AddBackgroundEditor(file, image),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        Toast.Dismiss();
        var saved = editor.ShowDialog() == true;
        image.Dispose();
        if (!saved || editor.Save is not { } save) return saved;

        Toast.Progress("Saving…", Path.GetFileName(file));
        try
        {
            var output = await Task.Run(save);
            Toast.Done("Saved", [output], []);
        }
        catch (Exception e)
        {
            Toast.Done("Couldn't save", [], [Describe(file, e)]);
        }
        return true;
    }

    public static string Describe(string file, Exception e) => e switch
    {
        UserFacingException u => u.Message,
        MagickException m => $"{Path.GetFileName(file)}: {FirstLine(m.Message)}",
        IOException or UnauthorizedAccessException => $"{Path.GetFileName(file)}: {e.Message}",
        _ => $"{Path.GetFileName(file)}: {e.GetType().Name}: {e.Message}",
    };

    static string FirstLine(string s) => s.Split('\n')[0].Trim();
}
