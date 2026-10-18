using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImageMagick;
using TextAlignment = System.Windows.TextAlignment;

namespace DragShift.App.Editors;

/// <summary>
/// Shared editor chrome: the picture on a stage, a controls area, and Reset / Cancel / Save. The picture is shown as a
/// downscaled preview; <see cref="Overlay"/> sits on top of it in preview-pixel coordinates (the stage scales both), and
/// <see cref="PreviewScale"/> converts preview pixels to full-resolution pixels.
/// </summary>
abstract class EditorWindow : Window
{
    protected const uint PreviewMaxSide = 1600;

    protected string FilePath { get; }
    /// <summary>The downscaled working copy shown on screen. Owned by the editor.</summary>
    protected MagickImage Preview { get; }
    /// <summary>Full-resolution pixels per preview pixel.</summary>
    protected double PreviewScale { get; }
    protected uint FullWidth { get; }
    protected uint FullHeight { get; }

    protected readonly Grid Host = new();
    protected readonly Image Picture = new() { Stretch = Stretch.Fill };
    protected readonly Canvas Overlay = new() { Background = Brushes.Transparent, ClipToBounds = true };
    protected readonly StackPanel Controls = new() { Margin = new Thickness(18, 6, 18, 0) };
    protected readonly Button SaveButton = new() { Content = "Save", Style = Theme.Style("Primary"), IsDefault = true, MinWidth = 96 };
    readonly Viewbox viewbox = new() { Stretch = Stretch.Uniform, StretchDirection = StretchDirection.Both };

    /// <summary>Set by <see cref="PrepareSave"/>: the work to run in the background; returns the file written.</summary>
    public Func<string>? Save { get; protected set; }

    protected EditorWindow(string file, MagickImage full, string title)
    {
        FilePath = file;
        FullWidth = full.Width;
        FullHeight = full.Height;
        Preview = Imaging.Downscaled(full, PreviewMaxSide);
        PreviewScale = (double)full.Width / Preview.Width;

        Title = $"{title} — {Path.GetFileName(file)}";
        Width = 1000;
        Height = 780;
        MinWidth = 640;
        MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Theme.WindowBackground;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        UseLayoutRounding = true;
        Icon = App.IconImage;

        Host.Children.Add(Picture);
        Host.Children.Add(Overlay);
        SetPreview(Imaging.ToBitmapSource(Preview));
        viewbox.Child = Host;

        var heading = new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 12, 0, 4) };
        var stage = new Border
        {
            Margin = new Thickness(18, 6, 18, 6),
            CornerRadius = new CornerRadius(12),
            Background = Checkerboard(),
            Padding = new Thickness(12),
            Child = viewbox,
        };

        var reset = new Button { Content = "Reset", MinWidth = 80 };
        reset.Click += (_, _) => OnReset();
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80 };
        SaveButton.Click += (_, _) =>
        {
            try
            {
                if (!PrepareSave()) return;
                DialogResult = true;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "DragShift", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        };
        var bar = new DockPanel { Margin = new Thickness(15, 8, 15, 14), LastChildFill = false };
        DockPanel.SetDock(reset, Dock.Left);
        bar.Children.Add(reset);
        var right = new StackPanel { Orientation = Orientation.Horizontal };
        right.Children.Add(cancel);
        right.Children.Add(SaveButton);
        DockPanel.SetDock(right, Dock.Right);
        bar.Children.Add(right);

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(stage, 1);
        Grid.SetRow(Controls, 2);
        Grid.SetRow(bar, 3);
        root.Children.Add(heading);
        root.Children.Add(stage);
        root.Children.Add(Controls);
        root.Children.Add(bar);
        Content = root;

        // The app lives in the tray, so make sure the editor actually comes to the front.
        Loaded += (_, _) =>
        {
            Topmost = true;
            Activate();
            Topmost = false;
        };
        Closed += (_, _) => Preview.Dispose();
    }

    /// <summary>Shows a new preview bitmap; the stage adopts its size (so rotations and re-framing display correctly).</summary>
    protected void SetPreview(BitmapSource bitmap)
    {
        Picture.Source = bitmap;
        Host.Width = Overlay.Width = bitmap.PixelWidth;
        Host.Height = Overlay.Height = bitmap.PixelHeight;
    }

    /// <summary>On-screen DIPs per preview pixel, for sizing handles and strokes so they look the same at any zoom.</summary>
    protected double ViewScale => Host.Width > 0 && viewbox.ActualWidth > 0 ? viewbox.ActualWidth / Host.Width : 1;

    protected abstract void OnReset();

    /// <summary>For the smoke test: put the editor into a state that has something to save.</summary>
    internal virtual void SmokeSetup() { }

    /// <summary>For the smoke test: runs Save as the button would and returns the file written (null if nothing to save).</summary>
    internal string? SmokeSave() => PrepareSave() && Save is { } save ? save() : null;

    /// <summary>Collects the user's choices into <see cref="Save"/>. Return false to keep the window open.</summary>
    protected abstract bool PrepareSave();

    // ---- Control builders ----

    protected static TextBlock Label(string text) => new() { Text = text, Width = 96, VerticalAlignment = VerticalAlignment.Center, Foreground = Theme.MutedBrush, FontSize = 13 };

    protected void AddRow(string label, UIElement content)
    {
        var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
        var l = Label(label);
        DockPanel.SetDock(l, Dock.Left);
        row.Children.Add(l);
        row.Children.Add(content);
        Controls.Children.Add(row);
    }

    /// <summary>A labelled slider with a live value read-out.</summary>
    protected Slider AddSlider(string label, double min, double max, double value, Func<double, string> format, Action<double> changed)
    {
        var slider = new Slider { Minimum = min, Maximum = max, Value = Math.Clamp(value, min, max) };
        var readout = new TextBlock { Width = 72, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Foreground = Theme.MutedBrush, Text = format(slider.Value) };
        slider.ValueChanged += (_, e) =>
        {
            readout.Text = format(e.NewValue);
            changed(e.NewValue);
        };
        var panel = new DockPanel();
        DockPanel.SetDock(readout, Dock.Right);
        panel.Children.Add(readout);
        panel.Children.Add(slider);
        AddRow(label, panel);
        return slider;
    }

    /// <summary>A row of mutually exclusive pill buttons.</summary>
    protected static StackPanel Segmented<T>(string group, (string label, T value)[] options, T selected, Action<T> changed)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (label, value) in options)
        {
            var rb = new RadioButton { Content = label, GroupName = group, IsChecked = Equals(value, selected), Padding = new Thickness(12, 4, 12, 4) };
            rb.Checked += (_, _) => changed(value);
            panel.Children.Add(rb);
        }
        return panel;
    }

    static Brush Checkerboard()
    {
        var light = new SolidColorBrush(Color.FromArgb(0x70, 0xFF, 0xFF, 0xFF));
        var dark = new SolidColorBrush(Color.FromArgb(0x40, 0xC6, 0xE3, 0xD0));
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(light, null, new RectangleGeometry(new Rect(0, 0, 16, 16))));
        group.Children.Add(new GeometryDrawing(dark, null, new RectangleGeometry(new Rect(0, 0, 8, 8))));
        group.Children.Add(new GeometryDrawing(dark, null, new RectangleGeometry(new Rect(8, 8, 8, 8))));
        var brush = new DrawingBrush(group) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 16, 16), ViewportUnits = BrushMappingMode.Absolute };
        brush.Freeze();
        return brush;
    }
}
