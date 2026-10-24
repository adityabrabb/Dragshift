using System;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DragShift.Core;
using Application = System.Windows.Application;

namespace DragShift.App;

/// <summary>Tray app: watches for Shift+drag and shows the wheel.</summary>
sealed class App : Application
{
    static Mutex? singleInstance;

    readonly Settings settings = Settings.Load();
    NotifyIcon? tray;
    DragWatcher? watcher;
    WheelWindow? wheel;

    public static ImageSource? IconImage { get; private set; }

    [STAThread]
    static int Main(string[] args)
    {
        if (args is ["--smoke-test", var dir])
        {
            Platform.Pdf = new PdfiumRasterizer();
            Platform.Heic = WindowsHeicEncoder.TryCreate();
            Platform.Ocr = WindowsTextRecognizer.TryCreate();
            Platform.Ffmpeg = Media.FindFfmpeg();
            Platform.Office = FindOffice();
            var test =new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            Theme.Apply(test);
            var code = 1;
            void Crash(object? error)
            {
                // Anything that escapes the test must still leave a trace for CI.
                var text = $"CRASH {error}";
                Console.Error.WriteLine(text);
                try { Directory.CreateDirectory(dir); File.AppendAllText(Path.Combine(dir, "smoke.log"), text + Environment.NewLine); } catch { }
            }
            AppDomain.CurrentDomain.UnhandledException += (_, e) => Crash(e.ExceptionObject);
            var crashed = false;
            test.DispatcherUnhandledException += (_, e) =>
            {
                // Record it and carry on, so one failure can't hide the steps after it.
                Crash(e.Exception);
                crashed = true;
                e.Handled = true;
            };
            test.Startup += (_, _) => test.Dispatcher.BeginInvoke(() =>
            {
                try
                {
                    code = SmokeTest.Run(dir);
                }
                catch (Exception e)
                {
                    Crash(e);
                    code = 3;
                }
                if (crashed && code == 0) code = 2;
                test.Shutdown(code);
            });
            test.Run();
            return code;
        }

        singleInstance = new Mutex(true, @"Local\DragShift.SingleInstance", out var first);
        if (!first)
        {
            System.Windows.MessageBox.Show("DragShift is already running — look for its icon in the system tray.", "DragShift");
            return 0;
        }
        Platform.Pdf = new PdfiumRasterizer();
        // HEIC is offered on the wheel only if this PC's codecs can actually write it. Checked in the background.
        System.Threading.Tasks.Task.Run(() => Platform.Heic = WindowsHeicEncoder.TryCreate());
        // Likewise copy text (needs an OCR language), video/audio (needs FFmpeg) and slides (need PowerPoint or LibreOffice).
        System.Threading.Tasks.Task.Run(() => Platform.Ocr = WindowsTextRecognizer.TryCreate());
        System.Threading.Tasks.Task.Run(() => Platform.Ffmpeg = Media.FindFfmpeg());
        System.Threading.Tasks.Task.Run(() => Platform.Office = FindOffice());

        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        Theme.Apply(app);
        app.Run();
        GC.KeepAlive(singleInstance);
        return 0;
    }

    static IOfficeConverter? FindOffice() => (IOfficeConverter?)PowerPointConverter.TryCreate() ?? LibreOfficeConverter.TryCreate();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            Toast.Done("Something went wrong", [], [args.Exception.Message]);
            args.Handled = true;
        };

        using var iconStream = typeof(App).Assembly.GetManifestResourceStream("DragShift.icon.ico")!;
        var icon = new Icon(iconStream);
        iconStream.Position = 0;
        IconImage = BitmapFrame.Create(iconStream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);

        watcher = new DragWatcher(() => settings.ExplorerOnly) { Enabled = settings.Enabled };
        watcher.Triggered += ShowWheel;
        watcher.Released += () => wheel?.OnDragReleased();
        try
        {
            watcher.Start();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"DragShift couldn't watch the mouse: {ex.Message}", "DragShift");
        }

        tray = new NotifyIcon { Icon = icon, Text = "DragShift — drag a file and hold Shift", Visible = true, ContextMenuStrip = BuildMenu() };
        tray.MouseClick += (_, args) =>
        {
            if (args.Button == MouseButtons.Left) Dispatcher.BeginInvoke(ShowPinnedWheel);
        };

        if (!settings.WelcomeShown)
        {
            settings.WelcomeShown = true;
            settings.Save();
            Toast.Info("DragShift is running", "Drag a file in Explorer or on the desktop and hold Shift to open the wheel. Right-click the tray icon for options.");
        }
    }

    ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        var enabled = new ToolStripMenuItem("Shift + drag opens the wheel") { Checked = settings.Enabled, CheckOnClick = true };
        enabled.CheckedChanged += (_, _) =>
        {
            settings.Enabled = enabled.Checked;
            if (watcher is not null) watcher.Enabled = enabled.Checked;
            settings.Save();
        };
        var explorerOnly = new ToolStripMenuItem("Only for drags from Explorer and the desktop") { Checked = settings.ExplorerOnly, CheckOnClick = true };
        explorerOnly.CheckedChanged += (_, _) =>
        {
            settings.ExplorerOnly = explorerOnly.Checked;
            settings.Save();
        };
        var startup = new ToolStripMenuItem("Start with Windows") { Checked = Settings.StartsWithWindows, CheckOnClick = true };
        startup.CheckedChanged += (_, _) =>
        {
            try
            {
                Settings.StartsWithWindows = startup.Checked;
            }
            catch (Exception ex)
            {
                Toast.Info("Couldn't change startup setting", ex.Message);
            }
        };

        var video = new ToolStripMenuItem("Add video support (FFmpeg)…");
        video.Click += (_, _) => InstallFfmpeg();
        menu.Opening += (_, _) => video.Visible = Platform.Ffmpeg is null;

        menu.Items.Add("Open the wheel", null, (_, _) => Dispatcher.BeginInvoke(ShowPinnedWheel));
        menu.Items.Add(video);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(enabled);
        menu.Items.Add(explorerOnly);
        menu.Items.Add(startup);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("How it works", null, (_, _) => Toast.Info("How DragShift works",
            "Start dragging files, then hold Shift: the wheel appears under the cursor. Drop on a format to convert, or on a tool to edit. Several files on PDF become one PDF. Results are saved next to the originals."));
        menu.Items.Add("Quit DragShift", null, (_, _) => Quit());
        return menu;
    }

    /// <summary>Installs FFmpeg with winget (per user, no admin prompt) and starts using it right away.</summary>
    static async void InstallFfmpeg()
    {
        Toast.Progress("Adding video support…", "Installing FFmpeg with winget. This can take a minute.");
        var error = await System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("winget",
                    "install --id Gyan.FFmpeg -e --silent --accept-package-agreements --accept-source-agreements --disable-interactivity")
                {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                };
                using var p = System.Diagnostics.Process.Start(psi);
                p?.StandardOutput.ReadToEnd();
                p?.WaitForExit();
            }
            catch (Exception e)
            {
                return $"winget isn't available ({e.Message}). Install FFmpeg from ffmpeg.org, then restart DragShift.";
            }
            Platform.Ffmpeg = Media.FindFfmpeg();
            return Platform.Ffmpeg is null ? "FFmpeg didn't install. Install it from ffmpeg.org, then restart DragShift." : null;
        });
        if (error is null) Toast.Info("Video support is on", "Drop videos on MP4, WEBM, GIF or MP3, or on Compress and Metadata.");
        else Toast.Info("Couldn't add video support", error);
    }

    void ShowWheel(int x, int y)
    {
        wheel?.Dismiss();
        wheel = NewWheel();
        wheel.ShowAt(x, y);
    }

    void ShowPinnedWheel()
    {
        wheel?.Dismiss();
        wheel = NewWheel();
        wheel.ShowPinned();
    }

    WheelWindow NewWheel()
    {
        var w = new WheelWindow();
        w.ActionChosen += Actions.Run;
        w.Closed += (_, _) => { if (wheel == w) wheel = null; };
        return w;
    }

    void Quit()
    {
        watcher?.Dispose();
        if (tray is not null)
        {
            tray.Visible = false;
            tray.Dispose();
        }
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        watcher?.Dispose();
        tray?.Dispose();
        base.OnExit(e);
    }
}
