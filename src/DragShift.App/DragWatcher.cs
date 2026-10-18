using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows.Threading;

namespace DragShift.App;

/// <summary>
/// Watches the mouse system-wide (low-level hook) and fires <see cref="Triggered"/> when the user is dragging with the
/// left button and holds Shift — the moment the wheel should appear under the cursor.
/// </summary>
sealed class DragWatcher : IDisposable
{
    /// <summary>How far the mouse must travel with the button down before it counts as a drag (physical px).</summary>
    const int DragThreshold = 8;

    /// <summary>Root window classes of File Explorer and the desktop.</summary>
    static readonly HashSet<string> ExplorerClasses = new(StringComparer.Ordinal) { "CabinetWClass", "ExploreWClass", "Progman", "WorkerW" };

    readonly Native.LowLevelProc proc; // kept alive: the hook holds only a native pointer to it
    readonly DispatcherTimer poll;
    readonly Func<bool> explorerOnly;
    IntPtr hook;

    bool down, eligible, moved, fired;
    Native.POINT downAt, last;

    /// <summary>Raised on the UI thread with the cursor position in physical screen pixels.</summary>
    public event Action<int, int>? Triggered;

    /// <summary>Raised on the UI thread when the left button is released after a drag that triggered the wheel.</summary>
    public event Action? Released;

    public bool Enabled { get; set; } = true;

    public DragWatcher(Func<bool> explorerOnly)
    {
        this.explorerOnly = explorerOnly;
        proc = HookProc;
        // Shift may be pressed while the mouse is standing still, so also check on a timer during a drag.
        poll = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(40) };
        poll.Tick += (_, _) => Check();
    }

    public void Start()
    {
        if (hook != IntPtr.Zero) return;
        using var module = Process.GetCurrentProcess().MainModule;
        hook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, proc, Native.GetModuleHandle(module?.ModuleName), 0);
        if (hook == IntPtr.Zero) throw new InvalidOperationException("Couldn't install the mouse hook.");
    }

    IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        // Keep this fast: Windows silently drops low-level hooks that take too long.
        if (nCode >= 0)
        {
            var info = System.Runtime.InteropServices.Marshal.PtrToStructure<Native.MSLLHOOKSTRUCT>(lParam);
            switch ((int)wParam)
            {
                case Native.WM_LBUTTONDOWN:
                    down = true;
                    moved = fired = false;
                    downAt = last = info.pt;
                    eligible = Enabled && (!explorerOnly() || ExplorerClasses.Contains(Native.RootClassName(Native.WindowFromPoint(info.pt))));
                    if (eligible) poll.Start();
                    break;
                case Native.WM_MOUSEMOVE:
                    last = info.pt;
                    if (down && eligible && !moved &&
                        (Math.Abs(info.pt.X - downAt.X) > DragThreshold || Math.Abs(info.pt.Y - downAt.Y) > DragThreshold))
                        moved = true;
                    if (moved && !fired) Check();
                    break;
                case Native.WM_LBUTTONUP:
                    var wasFired = fired;
                    down = eligible = moved = fired = false;
                    poll.Stop();
                    if (wasFired) Dispatcher.CurrentDispatcher.BeginInvoke(() => Released?.Invoke());
                    break;
            }
        }
        return Native.CallNextHookEx(hook, nCode, wParam, lParam);
    }

    void Check()
    {
        if (!down || !eligible || !moved || fired) return;
        // A missed button-up (e.g. the hook was briefly dropped) shouldn't leave us thinking a drag is in progress.
        if (!Native.IsKeyDown(Native.VK_LBUTTON) && !Native.IsKeyDown(0x02 /* swapped buttons */))
        {
            down = false;
            poll.Stop();
            return;
        }
        if (!Native.IsKeyDown(Native.VK_SHIFT)) return;
        fired = true;
        poll.Stop();
        var (x, y) = (last.X, last.Y);
        Dispatcher.CurrentDispatcher.BeginInvoke(() => Triggered?.Invoke(x, y));
    }

    public void Dispose()
    {
        poll.Stop();
        if (hook != IntPtr.Zero) Native.UnhookWindowsHookEx(hook);
        hook = IntPtr.Zero;
    }
}
