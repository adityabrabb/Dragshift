using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using DragShift.Core;
using ImageMagick;

namespace DragShift.App.Editors;

/// <summary>Crop: drag the handles or the box, or draw a new box; optional fixed aspect ratios.</summary>
sealed class CropEditor : EditorWindow
{
    enum Grip { None, Move, N, S, E, W, NE, NW, SE, SW, New }

    readonly Path shade = new() { Fill = new SolidColorBrush(Color.FromArgb(0x90, 0x10, 0x08, 0x04)), IsHitTestVisible = false };
    readonly Rectangle frame = new() { Stroke = Brushes.White, IsHitTestVisible = false };
    readonly Rectangle[] handles = new Rectangle[8];
    readonly TextBlock sizeText = new() { VerticalAlignment = VerticalAlignment.Center, Foreground = Theme.MutedBrush, Margin = new Thickness(12, 0, 0, 0) };
    readonly double pw, ph;

    Rect crop;
    double? aspect; // width / height, null = free
    Grip grip;
    Point dragStart;
    Rect cropAtStart;

    public CropEditor(string file, MagickImage full) : base(file, full, "Crop image")
    {
        pw = Preview.Width;
        ph = Preview.Height;
        Overlay.Children.Add(shade);
        Overlay.Children.Add(frame);
        for (var i = 0; i < handles.Length; i++)
        {
            handles[i] = new Rectangle { Fill = Brushes.White, Stroke = Theme.AccentBrush, IsHitTestVisible = false };
            Overlay.Children.Add(handles[i]);
        }
        Overlay.Cursor = Cursors.Cross;
        Overlay.MouseLeftButtonDown += OnDown;
        Overlay.MouseMove += OnMove;
        Overlay.MouseLeftButtonUp += (_, _) => { grip = Grip.None; Overlay.ReleaseMouseCapture(); };
        SizeChanged += (_, _) => Draw();

        var ratio = (double)FullWidth / FullHeight;
        var presets = Segmented("ratio", [("Free", (double?)null), ("Original", ratio), ("1:1", 1.0), ("4:5", 0.8), ("16:9", 16 / 9.0), ("9:16", 9 / 16.0)], null, a =>
        {
            aspect = a;
            if (a is { } r) crop = FitAspect(crop, r);
            Draw();
        });
        presets.Children.Add(sizeText);
        AddRow("Ratio", presets);
        SaveButton.Content = "Crop";

        OnReset();
    }

    protected override void OnReset()
    {
        crop = new Rect(0, 0, pw, ph);
        if (aspect is { } r) crop = FitAspect(crop, r);
        Draw();
    }

    protected override bool PrepareSave()
    {
        var k = PreviewScale;
        var rect = new PixelRect((int)Math.Round(crop.X * k), (int)Math.Round(crop.Y * k), (int)Math.Round(crop.Width * k), (int)Math.Round(crop.Height * k));
        if (rect.ClampTo(FullWidth, FullHeight) is null) return false;
        var file = FilePath;
        Save = () => Tools.Crop(file, rect);
        return true;
    }

    /// <summary>The largest rect of the given aspect inside <paramref name="within"/>'s bounds, centered on it.</summary>
    Rect FitAspect(Rect within, double r)
    {
        double w = within.Width, h = within.Height;
        if (w / h > r) w = h * r;
        else h = w / r;
        var c = new Point(within.X + within.Width / 2, within.Y + within.Height / 2);
        return Clamp(new Rect(c.X - w / 2, c.Y - h / 2, w, h));
    }

    Rect Clamp(Rect r)
    {
        var w = Math.Min(r.Width, pw);
        var h = Math.Min(r.Height, ph);
        return new Rect(Math.Clamp(r.X, 0, pw - w), Math.Clamp(r.Y, 0, ph - h), w, h);
    }

    double HandleSize => 12 / ViewScale;

    Grip HitTest(Point p)
    {
        var t = HandleSize;
        bool nearL = Math.Abs(p.X - crop.Left) < t, nearR = Math.Abs(p.X - crop.Right) < t;
        bool nearT = Math.Abs(p.Y - crop.Top) < t, nearB = Math.Abs(p.Y - crop.Bottom) < t;
        bool inX = p.X > crop.Left - t && p.X < crop.Right + t, inY = p.Y > crop.Top - t && p.Y < crop.Bottom + t;
        if (nearT && nearL) return Grip.NW;
        if (nearT && nearR) return Grip.NE;
        if (nearB && nearL) return Grip.SW;
        if (nearB && nearR) return Grip.SE;
        if (nearT && inX) return Grip.N;
        if (nearB && inX) return Grip.S;
        if (nearL && inY) return Grip.W;
        if (nearR && inY) return Grip.E;
        return crop.Contains(p) ? Grip.Move : Grip.New;
    }

    void OnDown(object sender, MouseButtonEventArgs e)
    {
        dragStart = e.GetPosition(Overlay);
        cropAtStart = crop;
        grip = HitTest(dragStart);
        Overlay.CaptureMouse();
    }

    void OnMove(object sender, MouseEventArgs e)
    {
        var p = e.GetPosition(Overlay);
        if (grip == Grip.None)
        {
            Overlay.Cursor = HitTest(p) switch
            {
                Grip.Move => Cursors.SizeAll,
                Grip.N or Grip.S => Cursors.SizeNS,
                Grip.E or Grip.W => Cursors.SizeWE,
                Grip.NE or Grip.SW => Cursors.SizeNESW,
                Grip.NW or Grip.SE => Cursors.SizeNWSE,
                _ => Cursors.Cross,
            };
            return;
        }
        p = new Point(Math.Clamp(p.X, 0, pw), Math.Clamp(p.Y, 0, ph));
        var d = p - dragStart;
        var r = cropAtStart;
        const double min = 4;
        switch (grip)
        {
            case Grip.Move:
                crop = Clamp(new Rect(r.X + d.X, r.Y + d.Y, r.Width, r.Height));
                Draw();
                return;
            case Grip.New:
                crop = new Rect(dragStart, p);
                break;
            default:
            {
                double l = r.Left, t = r.Top, rr = r.Right, b = r.Bottom;
                if (grip is Grip.W or Grip.NW or Grip.SW) l = Math.Min(p.X, rr - min);
                if (grip is Grip.E or Grip.NE or Grip.SE) rr = Math.Max(p.X, l + min);
                if (grip is Grip.N or Grip.NW or Grip.NE) t = Math.Min(p.Y, b - min);
                if (grip is Grip.S or Grip.SW or Grip.SE) b = Math.Max(p.Y, t + min);
                crop = new Rect(new Point(l, t), new Point(rr, b));
                break;
            }
        }
        if (aspect is { } a && crop.Width > 0 && crop.Height > 0)
        {
            // Keep the anchored corner/edge fixed while forcing the ratio.
            var w = crop.Width;
            var h = w / a;
            if (grip is Grip.N or Grip.S) { h = crop.Height; w = h * a; }
            var anchorRight = grip is Grip.W or Grip.NW or Grip.SW || (grip == Grip.New && p.X < dragStart.X);
            var anchorBottom = grip is Grip.N or Grip.NW or Grip.NE || (grip == Grip.New && p.Y < dragStart.Y);
            var x = anchorRight ? crop.Right - w : crop.Left;
            var y = anchorBottom ? crop.Bottom - h : crop.Top;
            crop = new Rect(x, y, w, h);
            if (crop.Left < 0 || crop.Top < 0 || crop.Right > pw || crop.Bottom > ph) crop = FitAspect(Rect.Intersect(crop, new Rect(0, 0, pw, ph)) is { IsEmpty: false } i ? i : crop, a);
        }
        crop = Rect.Intersect(crop, new Rect(0, 0, pw, ph));
        if (crop.IsEmpty) crop = cropAtStart;
        Draw();
    }

    void Draw()
    {
        var all = new RectangleGeometry(new Rect(0, 0, pw, ph));
        shade.Data = new CombinedGeometry(GeometryCombineMode.Exclude, all, new RectangleGeometry(crop));
        var line = 1.5 / ViewScale;
        frame.StrokeThickness = line;
        frame.Width = crop.Width;
        frame.Height = crop.Height;
        frame.At(crop.X, crop.Y);

        var s = HandleSize * 0.75;
        Point[] pts =
        [
            crop.TopLeft, new(crop.X + crop.Width / 2, crop.Top), crop.TopRight, new(crop.Right, crop.Y + crop.Height / 2),
            crop.BottomRight, new(crop.X + crop.Width / 2, crop.Bottom), crop.BottomLeft, new(crop.Left, crop.Y + crop.Height / 2),
        ];
        for (var i = 0; i < handles.Length; i++)
        {
            handles[i].Width = handles[i].Height = s;
            handles[i].StrokeThickness = line;
            handles[i].At(pts[i].X - s / 2, pts[i].Y - s / 2);
        }
        sizeText.Text = $"{Math.Round(crop.Width * PreviewScale)} × {Math.Round(crop.Height * PreviewScale)} px";
    }
}
