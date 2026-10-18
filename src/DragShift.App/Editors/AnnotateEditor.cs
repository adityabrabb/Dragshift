using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DragShift.Core;
using ImageMagick;

namespace DragShift.App.Editors;

/// <summary>Annotate: freehand pen and highlighter, drawn at full resolution on save.</summary>
sealed class AnnotateEditor : EditorWindow
{
    static readonly Color[] Palette =
    [
        Color.FromRgb(0xFF, 0x3B, 0x30), Color.FromRgb(0xFF, 0x95, 0x00), Color.FromRgb(0xFF, 0xD6, 0x0A), Color.FromRgb(0x34, 0xC7, 0x59),
        Color.FromRgb(0x0A, 0x84, 0xFF), Color.FromRgb(0xBF, 0x5A, 0xF2), Color.FromRgb(0x11, 0x11, 0x11), Color.FromRgb(0xFF, 0xFF, 0xFF),
    ];

    readonly InkCanvas ink = new() { Background = Brushes.Transparent };
    readonly Stack<Stroke> history = new();
    Color color = Palette[0];
    double size = 0.008; // fraction of the preview's long side
    bool highlighter;

    public AnnotateEditor(string file, MagickImage full) : base(file, full, "Annotate")
    {
        ink.Width = Preview.Width;
        ink.Height = Preview.Height;
        ink.StrokeCollected += (_, e) => history.Push(e.Stroke);
        Overlay.Children.Add(ink);

        var tools = Segmented("tool", [("Pen", "pen"), ("Highlighter", "hl"), ("Eraser", "erase")], "pen", t =>
        {
            highlighter = t == "hl";
            ink.EditingMode = t == "erase" ? InkCanvasEditingMode.EraseByStroke : InkCanvasEditingMode.Ink;
            Apply();
        });
        var undo = new Button { Content = "Undo" };
        undo.Click += (_, _) =>
        {
            while (history.Count > 0)
            {
                var s = history.Pop();
                if (ink.Strokes.Contains(s))
                {
                    ink.Strokes.Remove(s);
                    break;
                }
            }
        };
        tools.Children.Add(new Border { Width = 18 });
        tools.Children.Add(undo);
        AddRow("Tool", tools);

        var swatches = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var c in Palette)
        {
            var rb = new RadioButton { Style = Theme.Style("Swatch"), Background = new SolidColorBrush(c), GroupName = "color", IsChecked = c == color };
            rb.Checked += (_, _) => { color = c; Apply(); };
            swatches.Children.Add(rb);
        }
        swatches.Children.Add(new Border { Width = 18 });
        swatches.Children.Add(Segmented("size", [("S", 0.004), ("M", 0.008), ("L", 0.016)], size, s => { size = s; Apply(); }));
        AddRow("Color", swatches);
        SaveButton.Content = "Save annotated";
        Apply();
    }

    void Apply()
    {
        var w = Math.Max(1, Math.Max(Preview.Width, Preview.Height) * size * (highlighter ? 2.5 : 1));
        ink.DefaultDrawingAttributes = new DrawingAttributes
        {
            Color = highlighter ? Color.FromArgb(0x70, color.R, color.G, color.B) : color,
            Width = w,
            Height = w,
            FitToCurve = true,
            IsHighlighter = highlighter,
            StylusTip = highlighter ? StylusTip.Rectangle : StylusTip.Ellipse,
        };
    }

    internal override void SmokeSetup()
    {
        var points = new System.Windows.Input.StylusPointCollection();
        for (var i = 0; i < 20; i++) points.Add(new System.Windows.Input.StylusPoint(40 + i * 12, 60 + (i % 2) * 18));
        ink.Strokes.Add(new Stroke(points, ink.DefaultDrawingAttributes.Clone()));
    }

    protected override void OnReset()
    {
        ink.Strokes.Clear();
        history.Clear();
    }

    protected override bool PrepareSave()
    {
        if (ink.Strokes.Count == 0) return false;
        // Render the strokes (in preview coordinates) scaled up to the full-resolution pixel grid.
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(PreviewScale, PreviewScale));
            ink.Strokes.Draw(dc);
        }
        var bitmap = new RenderTargetBitmap((int)FullWidth, (int)FullHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        var png = Imaging.EncodePng(bitmap);
        var file = FilePath;
        Save = () => Tools.Annotate(file, png);
        return true;
    }
}
