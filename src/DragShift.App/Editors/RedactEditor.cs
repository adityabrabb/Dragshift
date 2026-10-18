using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using DragShift.Core;
using ImageMagick;

namespace DragShift.App.Editors;

/// <summary>Redact: drag boxes over anything sensitive; covered with black, pixelation or blur.</summary>
sealed class RedactEditor : EditorWindow
{
    readonly List<Rect> rects = [];
    readonly Rectangle rubber = new() { Stroke = Theme.AccentBrush, Fill = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0x5A, 0x1F)), Visibility = Visibility.Collapsed, IsHitTestVisible = false };
    readonly Button undo = new() { Content = "Undo" };
    readonly Button clear = new() { Content = "Clear" };
    RedactStyle style = RedactStyle.Box;
    Point? start;
    int version;

    public RedactEditor(string file, MagickImage full) : base(file, full, "Redact")
    {
        Overlay.Children.Add(rubber);
        Overlay.Cursor = Cursors.Cross;
        Overlay.MouseLeftButtonDown += (_, e) =>
        {
            start = e.GetPosition(Overlay);
            Overlay.CaptureMouse();
        };
        Overlay.MouseMove += (_, e) =>
        {
            if (start is not { } s) return;
            var r = new Rect(s, e.GetPosition(Overlay));
            rubber.StrokeThickness = 1.5 / ViewScale;
            rubber.Width = r.Width;
            rubber.Height = r.Height;
            rubber.At(r.X, r.Y);
            rubber.Visibility = Visibility.Visible;
        };
        Overlay.MouseLeftButtonUp += (_, e) =>
        {
            Overlay.ReleaseMouseCapture();
            if (start is not { } s) return;
            start = null;
            rubber.Visibility = Visibility.Collapsed;
            var r = Rect.Intersect(new Rect(s, e.GetPosition(Overlay)), new Rect(0, 0, Preview.Width, Preview.Height));
            if (r.IsEmpty || r.Width < 3 || r.Height < 3) return;
            rects.Add(r);
            Refresh();
        };

        var styles = Segmented("style", [("Black box", RedactStyle.Box), ("Pixelate", RedactStyle.Pixelate), ("Blur", RedactStyle.Blur)], style, s =>
        {
            style = s;
            Refresh();
        });
        undo.Click += (_, _) => { if (rects.Count > 0) { rects.RemoveAt(rects.Count - 1); Refresh(); } };
        clear.Click += (_, _) => OnReset();
        styles.Children.Add(new Border { Width = 18 });
        styles.Children.Add(undo);
        styles.Children.Add(clear);
        AddRow("Style", styles);
        Controls.Children.Add(new TextBlock { Text = "Drag over the areas to hide.", Foreground = Theme.MutedBrush, Margin = new Thickness(96, 2, 0, 0) });
        SaveButton.Content = "Save redacted";
        Refresh();
    }

    PixelRect ToPixels(Rect r, double k) =>
        new((int)Math.Floor(r.X * k), (int)Math.Floor(r.Y * k), (int)Math.Ceiling(r.Width * k), (int)Math.Ceiling(r.Height * k));

    /// <summary>Re-renders the preview with the real effect so what you see is what gets saved.</summary>
    async void Refresh()
    {
        undo.IsEnabled = clear.IsEnabled = SaveButton.IsEnabled = rects.Count > 0;
        var v = ++version;
        var boxes = rects.Select(r => ToPixels(r, 1)).ToList();
        var s = style;
        var bitmap = await Task.Run(() =>
        {
            lock (Preview)
            {
                using var copy = (MagickImage)Preview.Clone();
                Tools.ApplyRedactions(copy, boxes, s);
                return Imaging.ToBitmapSource(copy);
            }
        });
        if (v == version) SetPreview(bitmap);
    }

    internal override void SmokeSetup()
    {
        rects.Add(new Rect(10, 10, Preview.Width / 3.0, Preview.Height / 4.0));
        Refresh();
    }

    protected override void OnReset()
    {
        rects.Clear();
        Refresh();
    }

    protected override bool PrepareSave()
    {
        if (rects.Count == 0) return false;
        var boxes = rects.Select(r => ToPixels(r, PreviewScale)).ToList();
        var (file, s) = (FilePath, style);
        Save = () => Tools.Redact(file, boxes, s);
        return true;
    }
}
