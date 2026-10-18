using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using DragShift.Core;

namespace DragShift.App;

/// <summary>What a slice of the wheel does.</summary>
abstract record WheelAction
{
    public sealed record Convert(TargetFormat Format) : WheelAction;
    public sealed record Tool(ToolKind Kind) : WheelAction;

    public string Label => this switch
    {
        Convert c => Formats.Label(c.Format),
        Tool t => ToolInfo.Label(t.Kind),
        _ => "",
    };

    public string Describe => this switch
    {
        Convert c => $"Convert to {Formats.Label(c.Format)}",
        Tool { Kind: ToolKind.Compress } => "Compress",
        Tool { Kind: ToolKind.Metadata } => "Strip metadata",
        Tool { Kind: ToolKind.RemoveBg } => "Remove background",
        Tool { Kind: ToolKind.AddBg } => "Add background…",
        Tool t => ToolInfo.Label(t.Kind)[..1] + ToolInfo.Label(t.Kind)[1..].ToLowerInvariant() + "…",
        _ => "",
    };

    public bool AppliesTo(string path) => this switch
    {
        Convert c => Formats.CanConvert(path, c.Format),
        Tool t => ToolInfo.CanApply(path, t.Kind),
        _ => false,
    };
}

/// <summary>
/// The radial menu. Appears under the cursor during a Shift+drag; dropping on a slice runs that action on the dragged
/// files. Outer ring: formats. Inner ring: tools.
/// </summary>
sealed class WheelWindow : Window
{
    const double Size = 420;
    const double C = Size / 2;
    // Ring radii (DIPs): outer ring for formats, inner ring for tools, then the center disc.
    const double OuterR = 196, OuterInnerR = 128, InnerR = 121, InnerInnerR = 70, CenterR = 63;

    static readonly TargetFormat[] OuterOrder =
        [TargetFormat.Png, TargetFormat.Webp, TargetFormat.Heic, TargetFormat.Jpg, TargetFormat.Avif, TargetFormat.Bmp, TargetFormat.Tiff, TargetFormat.Pdf, TargetFormat.Docx];
    static readonly ToolKind[] InnerOrder =
        [ToolKind.Compress, ToolKind.Metadata, ToolKind.Edit, ToolKind.Annotate, ToolKind.RemoveBg, ToolKind.AddBg, ToolKind.Crop, ToolKind.Redact];

    static readonly Brush SliceFill = Frozen(Color.FromArgb(0xF2, 0xFF, 0xE9, 0xDA));
    static readonly Brush SliceHover = Frozen(Color.FromRgb(0xFF, 0x5A, 0x1F));
    static readonly Brush SliceText = Frozen(Color.FromRgb(0x2B, 0x1D, 0x16));

    sealed class Slice
    {
        public required WheelAction Action;
        public required Path Shape;
        public required TextBlock Text;
        public required double Start, Sweep, R0, R1;
        public bool Enabled = true;
    }

    readonly List<Slice> slices = [];
    readonly Canvas canvas = new() { Width = Size, Height = Size };
    readonly TextBlock centerTitle = new() { FontSize = 13, FontWeight = FontWeights.SemiBold, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, Width = 104, MaxHeight = 52, TextTrimming = TextTrimming.CharacterEllipsis };
    readonly TextBlock centerSub = new() { FontSize = 11, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, Width = 104, Foreground = Theme.MutedBrush };
    readonly ScaleTransform zoom = new(0.85, 0.85, C, C);

    Slice? hover;
    string[] files = [];
    bool dropped;
    bool pinned;
    (int x, int y) anchor;

    /// <summary>Raised when files are dropped on an enabled slice.</summary>
    public event Action<string[], WheelAction>? ActionChosen;

    public WheelWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = false;
        Width = Size;
        Height = Size;
        AllowDrop = true;
        Opacity = 0;
        Title = "DragShift";

        // A layered window is click/drop-through wherever it's fully transparent, so the whole disc gets a
        // (barely) visible backdrop — otherwise gaps between slices would drop onto whatever is behind.
        canvas.Children.Add(new Ellipse
        {
            Width = 2 * (OuterR + 6), Height = 2 * (OuterR + 6),
            Fill = Frozen(Color.FromArgb(0xB8, 0xFF, 0xF1, 0xE6)),
            Stroke = Frozen(Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF)), StrokeThickness = 1.5,
            Effect = new DropShadowEffect { BlurRadius = 24, ShadowDepth = 4, Opacity = 0.28 },
        }.At(C - OuterR - 6, C - OuterR - 6));

        BuildRing(OuterOrder.Select(f => (WheelAction)new WheelAction.Convert(f)).ToArray(), OuterInnerR, OuterR, 14.5);
        BuildRing(InnerOrder.Select(t => (WheelAction)new WheelAction.Tool(t)).ToArray(), InnerInnerR, InnerR, 10);

        canvas.Children.Add(new Ellipse
        {
            Width = 2 * CenterR, Height = 2 * CenterR,
            Fill = Frozen(Color.FromArgb(0xFF, 0xFF, 0xF7, 0xF1)),
            Stroke = Frozen(Color.FromArgb(0x30, 0xA0, 0x52, 0x2D)), StrokeThickness = 1,
        }.At(C - CenterR, C - CenterR));
        var center = new StackPanel { Width = 104, VerticalAlignment = VerticalAlignment.Center };
        center.Children.Add(centerTitle);
        center.Children.Add(centerSub);
        var centerHost = new Grid { Width = 2 * CenterR, Height = 2 * CenterR, IsHitTestVisible = false };
        centerHost.Children.Add(new Border { Child = center, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center });
        canvas.Children.Add(centerHost.At(C - CenterR, C - CenterR));

        canvas.RenderTransform = zoom;
        Content = canvas;

        SourceInitialized += (_, _) =>
        {
            if (!pinned) Native.MakeNoActivate(new WindowInteropHelper(this).Handle);
        };
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Render, () => PlaceAt(anchor.x, anchor.y));
        DragEnter += OnDragEnter;
        DragOver += OnDragOver;
        DragLeave += (_, _) => SetHover(null);
        Drop += OnDrop;
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        Deactivated += (_, _) => { if (pinned && !dropped) Close(); };
        MouseLeftButtonUp += (_, e) =>
        {
            // In pinned mode, clicking the middle closes the wheel.
            var p = e.GetPosition(canvas);
            if (pinned && Distance(p.X - C, p.Y - C) < CenterR) Close();
        };
        ResetCenter();
    }

    /// <summary>Shows the wheel centered on a point in physical screen pixels, for a drag in progress.</summary>
    public void ShowAt(int x, int y)
    {
        anchor = (x, y);
        Show();
        PlaceAt(x, y);
        Appear();
    }

    /// <summary>Shows the wheel in the middle of the screen, staying open until something is dropped or it loses focus.</summary>
    public void ShowPinned()
    {
        pinned = true;
        ShowActivated = true;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        centerSub.Text = "Drag files here · Esc to close";
        Show();
        Activate();
        Appear();
    }

    /// <summary>The drag ended. Close unless a drop is being handled.</summary>
    public void OnDragReleased()
    {
        if (pinned) return;
        // OLE delivers the drop just after the button-up the hook sees, so give it a moment.
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        t.Tick += (_, _) =>
        {
            t.Stop();
            if (!dropped) Close();
        };
        t.Start();
    }

    void Appear()
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(110)));
        zoom.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(160)) { EasingFunction = ease });
        zoom.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(160)) { EasingFunction = ease });
    }

    void PlaceAt(int x, int y)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || !Native.GetWindowRect(hwnd, out var r)) return;
        int w = r.Right - r.Left, h = r.Bottom - r.Top;
        var area = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(x, y)).WorkingArea;
        var left = Math.Clamp(x - w / 2, area.Left, Math.Max(area.Left, area.Right - w));
        var top = Math.Clamp(y - h / 2, area.Top, Math.Max(area.Top, area.Bottom - h));
        Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, left, top, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
    }

    void BuildRing(WheelAction[] actions, double r0, double r1, double fontSize)
    {
        var sweep = 360.0 / actions.Length;
        for (var i = 0; i < actions.Length; i++)
        {
            var start = -90 - sweep / 2 + i * sweep;
            var shape = new Path
            {
                Data = SliceGeometry(start, sweep, r0, r1),
                Fill = SliceFill,
                Stroke = SliceFill,
                StrokeThickness = 7,
                StrokeLineJoin = PenLineJoin.Round,
            };
            var mid = (start + sweep / 2) * Math.PI / 180;
            var rm = (r0 + r1) / 2;
            var text = new TextBlock
            {
                Text = actions[i].Label,
                FontSize = fontSize,
                FontWeight = FontWeights.SemiBold,
                Foreground = SliceText,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                Width = r1 - r0 + 10,
                IsHitTestVisible = false,
            };
            text.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
            var tx = C + Math.Cos(mid) * rm - text.Width / 2;
            var ty = C + Math.Sin(mid) * rm - text.DesiredSize.Height / 2;
            canvas.Children.Add(shape);
            canvas.Children.Add(text.At(tx, ty));
            slices.Add(new Slice { Action = actions[i], Shape = shape, Text = text, Start = start, Sweep = sweep, R0 = r0, R1 = r1 });
        }
    }

    /// <summary>An annular sector with parallel-edged gaps, inset so the rounded stroke stays inside its bounds.</summary>
    static Geometry SliceGeometry(double startDeg, double sweepDeg, double r0, double r1)
    {
        const double gap = 4 + 7; // visible gap + stroke thickness
        const double inset = 3.5; // half the stroke
        r0 += inset;
        r1 -= inset;
        double Off(double r) => gap / 2 / r * 180 / Math.PI;
        Point P(double deg, double r) => new(C + Math.Cos(deg * Math.PI / 180) * r, C + Math.Sin(deg * Math.PI / 180) * r);

        double a0o = startDeg + Off(r1), a1o = startDeg + sweepDeg - Off(r1);
        double a0i = startDeg + Off(r0), a1i = startDeg + sweepDeg - Off(r0);
        var fig = new PathFigure { StartPoint = P(a0o, r1), IsClosed = true };
        fig.Segments.Add(new ArcSegment(P(a1o, r1), new System.Windows.Size(r1, r1), 0, a1o - a0o > 180, SweepDirection.Clockwise, true));
        fig.Segments.Add(new LineSegment(P(a1i, r0), true));
        fig.Segments.Add(new ArcSegment(P(a0i, r0), new System.Windows.Size(r0, r0), 0, a1i - a0i > 180, SweepDirection.Counterclockwise, true));
        var g = new PathGeometry([fig]);
        g.Freeze();
        return g;
    }

    Slice? SliceAt(Point p)
    {
        double dx = p.X - C, dy = p.Y - C, r = Distance(dx, dy);
        var deg = Math.Atan2(dy, dx) * 180 / Math.PI;
        foreach (var s in slices)
        {
            if (r < s.R0 || r > s.R1) continue;
            var rel = ((deg - s.Start) % 360 + 360) % 360;
            if (rel < s.Sweep) return s;
        }
        return null;
    }

    void OnDragEnter(object sender, DragEventArgs e)
    {
        var dragged = e.Data.GetDataPresent(DataFormats.FileDrop) ? (e.Data.GetData(DataFormats.FileDrop) as string[] ?? []) : [];
        SetFiles(dragged.Where(System.IO.File.Exists).ToArray());
        OnDragOver(sender, e);
    }

    /// <summary>For the smoke test: show the wheel as if these files were dragged over the given action.</summary>
    internal void SimulateHover(string[] dragged, WheelAction action)
    {
        SetFiles(dragged);
        SetHover(slices.First(s => s.Action == action));
    }

    void SetFiles(string[] dragged)
    {
        files = dragged;
        foreach (var s in slices)
        {
            s.Enabled = files.Any(s.Action.AppliesTo);
            s.Shape.Opacity = s.Enabled ? 1 : 0.4;
            s.Text.Opacity = s.Enabled ? 1 : 0.45;
        }
        ResetCenter();
    }

    void OnDragOver(object sender, DragEventArgs e)
    {
        var s = SliceAt(e.GetPosition(canvas));
        SetHover(s is { Enabled: true } ? s : null);
        if (s is { Enabled: false } && files.Length > 0)
        {
            // Say why a greyed-out slice can't take these files.
            centerTitle.Text = s.Action.Describe;
            centerSub.Text = s.Action is WheelAction.Convert { Format: var f } && !Formats.IsAvailable(f)
                ? $"{Formats.Label(f)} isn't available on this PC"
                : "Not for these files";
        }
        else if (hover is null) ResetCenter();
        e.Effects = hover is null ? DragDropEffects.None : DragDropEffects.Copy;
        e.Handled = true;
    }

    void OnDrop(object sender, DragEventArgs e)
    {
        var s = SliceAt(e.GetPosition(canvas));
        e.Handled = true;
        if (s is not { Enabled: true } || files.Length == 0)
        {
            e.Effects = DragDropEffects.None;
            if (!pinned) Close();
            return;
        }
        e.Effects = DragDropEffects.Copy;
        dropped = true;
        var chosen = files.Where(s.Action.AppliesTo).ToArray();
        // Let the drag source finish before doing anything that might open windows.
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            Close();
            ActionChosen?.Invoke(chosen, s.Action);
        });
    }

    void SetHover(Slice? s)
    {
        if (hover == s) return;
        if (hover is not null)
        {
            hover.Shape.Fill = hover.Shape.Stroke = SliceFill;
            hover.Text.Foreground = SliceText;
        }
        hover = s;
        if (s is not null)
        {
            s.Shape.Fill = s.Shape.Stroke = SliceHover;
            s.Text.Foreground = Brushes.White;
            centerTitle.Text = s.Action.Describe;
            centerSub.Text = FilesSummary();
        }
        else ResetCenter();
    }

    void ResetCenter()
    {
        if (files.Length == 0)
        {
            centerTitle.Text = "DragShift";
            if (!pinned) centerSub.Text = "Drop on a format or tool";
            return;
        }
        centerTitle.Text = files.Length == 1 ? System.IO.Path.GetFileName(files[0]) : $"{files.Length} files";
        centerSub.Text = slices.Any(x => x.Enabled) ? "Drop on a format or tool" : "Not an image or PDF";
    }

    string FilesSummary() => files.Length == 1 ? System.IO.Path.GetFileName(files[0]) : $"{files.Length} files";

    static double Distance(double dx, double dy) => Math.Sqrt(dx * dx + dy * dy);

    static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}

static class CanvasExtensions
{
    public static T At<T>(this T element, double left, double top) where T : UIElement
    {
        Canvas.SetLeft(element, left);
        Canvas.SetTop(element, top);
        return element;
    }
}
