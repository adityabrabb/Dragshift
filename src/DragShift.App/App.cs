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
            Platform.Pdf = new WindowsPdfRasterizer();
            Platform.Heic = WindowsHeicEncoder.TryCreate();
            var test = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            Theme.Apply(test);
            var code = 1;
            test.Startup += (_, _) => test.Dispatcher.BeginInvoke(() =>
            {
                code = SmokeTest.Run(dir);
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
        Platform.Pdf = new WindowsPdfRasterizer();
        // HEIC is offered on the wheel only if this PC's codecs can actually write it. Checked in the background.
        System.Threading.Tasks.Task.Run(() => Platform.Heic = WindowsHeicEncoder.TryCreate());

        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        Theme.Apply(app);
        app.Run();
        GC.KeepAlive(singleInstance);
        return 0;
    }

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

        menu.Items.Add("Open the wheel", null, (_, _) => Dispatcher.BeginInvoke(ShowPinnedWheel));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(enabled);
        menu.Items.Add(explorerOnly);
        menu.Items.Add(startup);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("How it works", null, (_, _) => Toast.Info("How DragShift works",
            "Start dragging a file, then hold Shift: the wheel appears under the cursor. Drop on a format to convert, or on a tool to edit. Results are saved next to the original."));
        menu.Items.Add("Open-source licenses", null, (_, _) => ShowLicenses());
        menu.Items.Add("Quit DragShift", null, (_, _) => Quit());
        return menu;
    }

    /// <summary>Writes the bundled license texts to a folder and opens it, with the overview in Notepad.</summary>
    static void ShowLicenses()
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DragShift", "licenses");
            Directory.CreateDirectory(dir);
            var asm = typeof(App).Assembly;
            foreach (var name in asm.GetManifestResourceNames())
            {
                if (!name.StartsWith("DragShift.licenses.") && name is not ("DragShift.LICENSE" or "DragShift.THIRD-PARTY-NOTICES.md")) continue;
                var file = name.StartsWith("DragShift.licenses.") ? name["DragShift.licenses.".Length..] : name["DragShift.".Length..] + (name.EndsWith("LICENSE") ? ".txt" : "");
                using var src = asm.GetManifestResourceStream(name)!;
                using var dst = File.Create(Path.Combine(dir, file));
                src.CopyTo(dst);
            }
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("notepad.exe", $"\"{Path.Combine(dir, "THIRD-PARTY-NOTICES.md")}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Toast.Info("Couldn't open the licenses", ex.Message);
        }
    }

    void ShowWheel(int x, int y)
    {
        wheel?.Close();
        wheel = NewWheel();
        wheel.ShowAt(x, y);
    }

    void ShowPinnedWheel()
    {
        wheel?.Close();
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
