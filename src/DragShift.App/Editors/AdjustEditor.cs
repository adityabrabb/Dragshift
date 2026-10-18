using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DragShift.Core;
using ImageMagick;

namespace DragShift.App.Editors;

/// <summary>Edit: rotate, flip, brightness / contrast / saturation, and resize.</summary>
sealed class AdjustEditor : EditorWindow
{
    readonly DispatcherTimer debounce = new() { Interval = TimeSpan.FromMilliseconds(90) };
    readonly TextBlock sizeText = new() { VerticalAlignment = VerticalAlignment.Center, Foreground = Theme.MutedBrush, Margin = new Thickness(12, 0, 0, 0) };
    readonly Slider brightness, contrast, saturation, scale;
    EditOptions options = new();
    int version;

    public AdjustEditor(string file, MagickImage full) : base(file, full, "Edit")
    {
        var transform = new StackPanel { Orientation = Orientation.Horizontal };
        transform.Children.Add(Action("Rotate left", () => options = options with { QuarterTurns = options.QuarterTurns + 3 }));
        transform.Children.Add(Action("Rotate right", () => options = options with { QuarterTurns = options.QuarterTurns + 1 }));
        transform.Children.Add(Action("Flip horizontal", () => options = options with { FlipHorizontal = !options.FlipHorizontal }));
        transform.Children.Add(Action("Flip vertical", () => options = options with { FlipVertical = !options.FlipVertical }));
        transform.Children.Add(sizeText);
        AddRow("Transform", transform);

        static string Signed(double v) => v > 0.5 ? $"+{v:0}" : $"{v:0}";
        brightness = AddSlider("Brightness", -100, 100, 0, Signed, v => Changed(options with { Brightness = Math.Round(v) }));
        contrast = AddSlider("Contrast", -100, 100, 0, Signed, v => Changed(options with { Contrast = Math.Round(v) }));
        saturation = AddSlider("Saturation", -100, 100, 0, Signed, v => Changed(options with { Saturation = Math.Round(v) }));
        scale = AddSlider("Size", 5, 100, 100, v => $"{v:0}%", v => Changed(options with { ScalePercent = Math.Round(v) }));

        debounce.Tick += (_, _) =>
        {
            debounce.Stop();
            Render();
        };
        UpdateSize();
    }

    Button Action(string label, Action change)
    {
        var b = new Button { Content = label };
        b.Click += (_, _) =>
        {
            change();
            UpdateSize();
            Render();
        };
        return b;
    }

    void Changed(EditOptions next)
    {
        options = next;
        UpdateSize();
        debounce.Stop();
        debounce.Start();
    }

    void UpdateSize()
    {
        var k = options.ScalePercent / 100;
        var (w, h) = options.QuarterTurns % 2 == 1 ? (FullHeight, FullWidth) : (FullWidth, FullHeight);
        sizeText.Text = $"{Math.Round(w * k)} × {Math.Round(h * k)} px";
        SaveButton.IsEnabled = !options.IsIdentity;
    }

    async void Render()
    {
        var v = ++version;
        // Preview shows the look at preview size; resizing is reflected in the size read-out, not by shrinking the preview.
        var o = options with { ScalePercent = 100 };
        var bitmap = await Task.Run(() =>
        {
            lock (Preview)
            {
                using var copy = (MagickImage)Preview.Clone();
                Tools.ApplyEdit(copy, o);
                return Imaging.ToBitmapSource(copy);
            }
        });
        if (v == version) SetPreview(bitmap);
    }

    internal override void SmokeSetup() => Changed(options with { QuarterTurns = 1, Saturation = 20 });

    protected override void OnReset()
    {
        options = new EditOptions();
        brightness.Value = contrast.Value = saturation.Value = 0;
        scale.Value = 100;
        UpdateSize();
        Render();
    }

    protected override bool PrepareSave()
    {
        if (options.IsIdentity) return false;
        var (file, o) = (FilePath, options);
        Save = () => Tools.Edit(file, o);
        return true;
    }
}
