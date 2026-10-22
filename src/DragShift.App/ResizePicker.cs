using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using DragShift.Core;

namespace DragShift.App;

/// <summary>A small card of resize presets. One choice applies to every dropped file.</summary>
sealed class ResizePicker : Window
{
    ResizeSpec? chosen;
    readonly TextBox custom = new() { Width = 70, Padding = new Thickness(6, 4, 6, 4), VerticalContentAlignment = VerticalAlignment.Center, FontSize = 13 };

    ResizePicker(int fileCount)
    {
        Title = "Resize";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = false;
        Topmost = true;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        Icon = App.IconImage;

        var stack = new StackPanel { Margin = new Thickness(18, 14, 18, 14), Width = 300 };
        stack.Children.Add(new TextBlock { Text = "Resize", FontSize = 15, FontWeight = FontWeights.SemiBold });
        stack.Children.Add(new TextBlock
        {
            Text = fileCount == 1 ? "Shrinks the picture; it's never enlarged." : $"Applies to all {fileCount} pictures; none is enlarged.",
            Foreground = Theme.MutedBrush, FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 10),
        });

        var presets = new WrapPanel();
        foreach (var spec in ResizeSpec.Presets)
        {
            var b = new Button { Content = spec.Percent is null ? $"{spec.LongSide} px" : spec.Label, MinWidth = 88 };
            b.Click += (_, _) => Choose(spec);
            presets.Children.Add(b);
        }
        stack.Children.Add(presets);

        var row = new DockPanel { Margin = new Thickness(3, 10, 0, 0) };
        var label = new TextBlock { Text = "Long side", Foreground = Theme.MutedBrush, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        var go = new Button { Content = "Resize", Style = Theme.Style("Primary"), IsDefault = true };
        go.Click += (_, _) =>
        {
            if (int.TryParse(custom.Text.Trim().TrimEnd('x', 'p', ' '), out var px) && px is >= 16 and <= 30000) Choose(new ResizeSpec(LongSide: px));
            else custom.Focus();
        };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        DockPanel.SetDock(label, Dock.Left);
        DockPanel.SetDock(custom, Dock.Left);
        row.Children.Add(label);
        row.Children.Add(custom);
        row.Children.Add(new TextBlock { Text = "px", Foreground = Theme.MutedBrush, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(cancel);
        buttons.Children.Add(go);
        stack.Children.Add(row);
        stack.Children.Add(new Border { Height = 8 });
        stack.Children.Add(buttons);

        Content = new Border
        {
            Margin = new Thickness(16),
            CornerRadius = new CornerRadius(14),
            Background = Theme.WindowBackground,
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0x2E, 0x7D, 0x4F)),
            BorderThickness = new Thickness(1),
            Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 3, Opacity = 0.25 },
            Child = stack,
        };
        MouseLeftButtonDown += (_, _) => DragMove();
        // The app lives in the tray, so make sure the card actually comes to the front.
        Loaded += (_, _) =>
        {
            Activate();
            custom.Focus();
        };
    }

    void Choose(ResizeSpec spec)
    {
        chosen = spec;
        DialogResult = true;
    }

    /// <summary>Shows the card and returns the chosen size, or null if cancelled.</summary>
    public static ResizeSpec? Ask(int fileCount)
    {
        var picker = new ResizePicker(fileCount);
        return picker.ShowDialog() == true ? picker.chosen : null;
    }

    /// <summary>For the smoke test: the card as it would appear, without waiting for a choice.</summary>
    internal static ResizePicker ForSmokeTest() => new(3);
}
