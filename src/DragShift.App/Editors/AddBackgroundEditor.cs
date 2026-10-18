using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using DragShift.Core;
using ImageMagick;
using Microsoft.Win32;

namespace DragShift.App.Editors;

/// <summary>Add BG: put the picture on a gradient, color or photo, with padding, rounded corners and a shadow.</summary>
sealed class AddBackgroundEditor : EditorWindow
{
    /// <summary>The preview renders from a smaller copy so slider drags stay smooth.</summary>
    const uint RenderMaxSide = 900;

    readonly MagickImage renderSource;
    readonly double renderScale; // render-source pixels per full-resolution pixel
    readonly DispatcherTimer debounce = new() { Interval = TimeSpan.FromMilliseconds(60) };
    readonly Slider padding, corners, shadow;
    readonly StackPanel ratios;
    readonly RadioButton firstGradient;
    BackgroundOptions options;
    int version;

    public AddBackgroundEditor(string file, MagickImage full) : base(file, full, "Add BG")
    {
        renderSource = Imaging.Downscaled(full, RenderMaxSide);
        renderScale = (double)renderSource.Width / full.Width;
        options = BackgroundOptions.DefaultsFor(FullWidth, FullHeight);
        var longSide = Math.Max(FullWidth, FullHeight);
        var shortSide = Math.Min(FullWidth, FullHeight);

        // Background swatches: gradients + "pick an image" on the first row, solid colors on the second.
        var gradients = new StackPanel { Orientation = Orientation.Horizontal };
        RadioButton? first = null;
        foreach (var g in BackgroundPresets.Gradients)
        {
            var rb = Swatch(GradientThumb(g), g);
            first ??= rb;
            gradients.Children.Add(rb);
        }
        firstGradient = first!;
        firstGradient.IsChecked = true;
        var pick = new Button { Content = "Image…", Padding = new Thickness(10, 2, 10, 2), ToolTip = "Use an image as the background" };
        pick.Click += (_, _) =>
        {
            var dialog = new OpenFileDialog { Filter = "Images|*.png;*.jpg;*.jpeg;*.webp;*.heic;*.avif;*.bmp;*.tif;*.tiff;*.gif|All files|*.*" };
            if (dialog.ShowDialog(this) != true) return;
            foreach (var child in LogicalTreeHelper.GetChildren(Controls)) ClearSwatches(child);
            Set(options with { Backdrop = new Backdrop.Picture(dialog.FileName) });
        };
        gradients.Children.Add(pick);
        var solids = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var c in BackgroundPresets.Solids)
            solids.Children.Add(Swatch(new SolidColorBrush((Color)ColorConverter.ConvertFromString(c)), new Backdrop.Solid(c)));
        var swatchRows = new StackPanel();
        swatchRows.Children.Add(gradients);
        swatchRows.Children.Add(solids);
        AddRow("Background", swatchRows);

        padding = AddSlider("Padding", 0, Math.Round(longSide * 0.5), options.Padding, v => $"{v:0} px", v => Set(options with { Padding = v }));
        corners = AddSlider("Corners", 0, Math.Round(shortSide / 2.0), options.Corners, v => $"{v:0} px", v => Set(options with { Corners = v }));
        shadow = AddSlider("Shadow", 0, Math.Round(longSide * 0.08), options.Shadow, v => $"{v:0} px", v => Set(options with { Shadow = v }));
        ratios = Segmented("ratio", [("Auto", FrameRatio.Auto), ("1:1", FrameRatio.Square), ("16:9", FrameRatio.Wide), ("4:5", FrameRatio.Portrait)], options.Ratio, r => Set(options with { Ratio = r }));
        AddRow("Ratio", ratios);
        SaveButton.Content = "Save with Background";

        debounce.Tick += (_, _) =>
        {
            debounce.Stop();
            Render();
        };
        Closed += (_, _) => renderSource.Dispose();
        Render();
    }

    RadioButton Swatch(Brush look, Backdrop backdrop)
    {
        var rb = new RadioButton { Style = Theme.Style("Swatch"), Background = look, GroupName = "bg" };
        rb.Checked += (_, _) => Set(options with { Backdrop = backdrop });
        return rb;
    }

    static void ClearSwatches(object element)
    {
        if (element is RadioButton { GroupName: "bg" } rb) rb.IsChecked = false;
        if (element is DependencyObject d)
            foreach (var child in LogicalTreeHelper.GetChildren(d)) ClearSwatches(child);
    }

    static Brush GradientThumb(Backdrop.Mesh mesh)
    {
        using var img = BackgroundRenderer.MeshGradient(mesh.Blobs, 48, 48);
        var brush = new ImageBrush(Imaging.ToBitmapSource(img));
        brush.Freeze();
        return brush;
    }

    void Set(BackgroundOptions next)
    {
        options = next;
        debounce.Stop();
        debounce.Start();
    }

    async void Render()
    {
        var v = ++version;
        var o = options.Scaled(renderScale);
        try
        {
            var bitmap = await Task.Run(() =>
            {
                lock (renderSource)
                {
                    using var result = BackgroundRenderer.Render(renderSource, o);
                    return Imaging.ToBitmapSource(result);
                }
            });
            if (v == version) SetPreview(bitmap);
        }
        catch (Exception e)
        {
            if (v == version) MessageBox.Show(this, Actions.Describe(FilePath, e), "DragShift", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    protected override void OnReset()
    {
        var d = BackgroundOptions.DefaultsFor(FullWidth, FullHeight);
        padding.Value = d.Padding;
        corners.Value = d.Corners;
        shadow.Value = d.Shadow;
        ((RadioButton)ratios.Children[0]).IsChecked = true;
        firstGradient.IsChecked = true;
        options = d;
        Render();
    }

    protected override bool PrepareSave()
    {
        var (file, o) = (FilePath, options);
        Save = () => Tools.AddBackground(file, o);
        return true;
    }
}
