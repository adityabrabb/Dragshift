using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace DragShift.App;

/// <summary>A small card in the bottom-right corner reporting progress and results. Never takes focus.</summary>
sealed class Toast : Window
{
    static Toast? current;

    readonly TextBlock title = new() { FontWeight = FontWeights.SemiBold, FontSize = 14, TextTrimming = TextTrimming.CharacterEllipsis };
    readonly TextBlock detail = new() { FontSize = 12.5, Foreground = Theme.MutedBrush, TextWrapping = TextWrapping.Wrap, MaxHeight = 70, TextTrimming = TextTrimming.CharacterEllipsis };
    readonly Button reveal = new() { Content = "Show in folder", Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(10, 4, 10, 4) };
    readonly DispatcherTimer hideTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    string? revealPath;

    Toast()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.Height;
        Width = 340;

        reveal.Click += (_, _) =>
        {
            if (revealPath is { } p && (File.Exists(p) || Directory.Exists(p)))
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{p}\"") { UseShellExecute = true });
            Close();
        };
        var stack = new StackPanel { Margin = new Thickness(16, 12, 16, 14) };
        stack.Children.Add(title);
        stack.Children.Add(detail);
        stack.Children.Add(reveal);
        Content = new Border
        {
            Margin = new Thickness(14),
            CornerRadius = new CornerRadius(14),
            Background = Theme.WindowBackground,
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0x2E, 0x7D, 0x4F)),
            BorderThickness = new Thickness(1),
            Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 3, Opacity = 0.25 },
            Child = stack,
        };
        hideTimer.Tick += (_, _) => FadeOut();
        MouseEnter += (_, _) => hideTimer.Stop();
        MouseLeave += (_, _) => { if (!busy) hideTimer.Start(); };
        SourceInitialized += (_, _) => Native.MakeNoActivate(new WindowInteropHelper(this).Handle);
        Loaded += (_, _) => PlaceBottomRight();
        SizeChanged += (_, _) => PlaceBottomRight();
        Closed += (_, _) => { if (current == this) current = null; };
    }

    bool busy;

    void PlaceBottomRight()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Right - ActualWidth - 4;
        Top = area.Bottom - ActualHeight - 4;
    }

    static Toast Get()
    {
        if (current is { IsLoaded: true }) return current;
        current = new Toast();
        current.Show();
        return current;
    }

    /// <summary>"Working…" state that stays up until a result replaces it.</summary>
    public static void Progress(string text, string? sub = null)
    {
        var t = Get();
        t.busy = true;
        t.hideTimer.Stop();
        t.BeginAnimation(OpacityProperty, null);
        t.Opacity = 1;
        t.title.Text = text;
        t.detail.Text = sub ?? "";
        t.detail.Visibility = sub is null ? Visibility.Collapsed : Visibility.Visible;
        t.reveal.Visibility = Visibility.Collapsed;
    }

    public static void Done(string text, IReadOnlyList<string> outputs, IReadOnlyList<string> errors)
    {
        var t = Get();
        t.busy = false;
        t.BeginAnimation(OpacityProperty, null);
        t.Opacity = 1;
        t.title.Text = text;
        var lines = outputs.Select(Path.GetFileName).Concat(errors).Take(4).ToList();
        var more = outputs.Count + errors.Count - lines.Count;
        if (more > 0) lines.Add($"…and {more} more");
        t.detail.Text = string.Join("\n", lines);
        t.detail.Visibility = lines.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        t.revealPath = outputs.LastOrDefault();
        t.reveal.Visibility = t.revealPath is null ? Visibility.Collapsed : Visibility.Visible;
        t.hideTimer.Interval = TimeSpan.FromSeconds(errors.Count > 0 ? 9 : 5);
        t.hideTimer.Stop();
        t.hideTimer.Start();
    }

    public static void Info(string text, string sub) => Done(text, [], [sub]);

    /// <summary>Closes a progress card (e.g. when an editor window takes over).</summary>
    public static void Dismiss()
    {
        if (current is { } t)
        {
            t.busy = false;
            t.Close();
        }
    }

    void FadeOut()
    {
        hideTimer.Stop();
        var anim = new DoubleAnimation(0, TimeSpan.FromMilliseconds(250));
        anim.Completed += (_, _) => { if (!busy && IsLoaded) Close(); };
        BeginAnimation(OpacityProperty, anim);
    }
}
