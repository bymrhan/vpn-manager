using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

// =====================================================================
// Keeps OpenVPN Connect and FortiClient in the background while this app drives them.
//  - OpenVPN Connect: its window pops up when a connect/disconnect command reaches it; windows
//    that were not open before the command are minimized again without taking focus.
//  - FortiClient: its form has to be on screen (rendered) for the accessibility calls, so the
//    window is parked far off-screen instead. Reveal() moves it back when the user must act
//    (password, token, an error) or when the connection work is over.
// A window the user already had open on screen is never touched.
// =====================================================================

internal static class QuietWindow
{
    const int SW_SHOWNOACTIVATE = 4, SW_RESTORE = 9, SW_SHOWMINNOACTIVE = 7;
    const uint SWP_NOSIZE = 0x1, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10;
    const int Off = -32000;

    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc p, IntPtr l);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool GetWindowPlacement(IntPtr h, ref WINDOWPLACEMENT p);
    [DllImport("user32.dll")] static extern bool SetWindowPlacement(IntPtr h, ref WINDOWPLACEMENT p);

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    struct WINDOWPLACEMENT { public int length, flags, showCmd; public int minX, minY, maxX, maxY; public RECT normal; }

    static readonly object gate = new object();
    static IntPtr parked = IntPtr.Zero;
    static RECT parkedHome;

    // ---------- OpenVPN Connect ----------

    // Call right before sending a connect/disconnect command. For up to `seconds`, windows of the
    // process that were not open on screen before are minimized as soon as they show up.
    public static void KeepProcessInBackground(string processName, int seconds)
    {
        var before = new HashSet<IntPtr>(TopWindows(processName, true));
        var prev = GetForegroundWindow();
        ThreadPool.QueueUserWorkItem(delegate
        {
            try
            {
                var end = DateTime.UtcNow.AddSeconds(seconds);
                var graceEnd = DateTime.MaxValue;
                while (DateTime.UtcNow < end && DateTime.UtcNow < graceEnd)
                {
                    foreach (var h in TopWindows(processName, true))
                    {
                        if (before.Contains(h)) continue;
                        ShowWindow(h, SW_SHOWMINNOACTIVE);
                        if (prev != IntPtr.Zero && IsWindow(prev) && GetForegroundWindow() != prev) SetForegroundWindow(prev);
                        if (graceEnd == DateTime.MaxValue) graceEnd = DateTime.UtcNow.AddSeconds(2);
                    }
                    Thread.Sleep(40);
                }
            }
            catch { }
        });
    }

    // ---------- FortiClient ----------

    // Parks FortiClient's window off-screen as soon as it exists (up to `seconds`); returns at once.
    public static void ParkFortiWhenShown(int seconds)
    {
        var prev = GetForegroundWindow();
        ThreadPool.QueueUserWorkItem(delegate
        {
            try
            {
                var end = DateTime.UtcNow.AddSeconds(seconds);
                var stableUntil = DateTime.MaxValue;
                while (DateTime.UtcNow < end && DateTime.UtcNow < stableUntil)
                {
                    var h = FortiStore.FindMainWindow();
                    if (h != IntPtr.Zero && IsWindowVisible(h) && !IsIconic(h))
                    {
                        if (ParkNow(h)) { RestoreFocus(prev); if (stableUntil == DateTime.MaxValue) stableUntil = DateTime.UtcNow.AddSeconds(2); }
                        else if (stableUntil == DateTime.MaxValue) stableUntil = DateTime.UtcNow.AddMilliseconds(300); // the user's own open window: leave it
                    }
                    Thread.Sleep(30);
                }
            }
            catch { }
        });
    }

    // An already running FortiClient window (maybe hidden or minimized): make it renderable
    // without showing it. A window the user has open on screen stays where it is.
    public static void ParkExisting(IntPtr h)
    {
        if (h == IntPtr.Zero) return;
        var prev = GetForegroundWindow();
        if (IsWindowVisible(h) && !IsIconic(h)) return; // the user has it open on screen: leave it
        RECT r; GetWindowRect(h, out r);
        var wp = new WINDOWPLACEMENT { length = Marshal.SizeOf(typeof(WINDOWPLACEMENT)) };
        if (!GetWindowPlacement(h, ref wp)) return;
        lock (gate) { parked = h; parkedHome = wp.normal; }
        var w = wp.normal.Right - wp.normal.Left; var ht = wp.normal.Bottom - wp.normal.Top;
        wp.normal = new RECT { Left = Off, Top = Off, Right = Off + w, Bottom = Off + ht };
        wp.showCmd = SW_SHOWNOACTIVATE;
        SetWindowPlacement(h, ref wp);
        RestoreFocus(prev);
    }

    // True when the window was moved off-screen (false: it is already parked, or the user's own).
    static bool ParkNow(IntPtr h)
    {
        RECT r;
        if (!GetWindowRect(h, out r)) return false;
        lock (gate)
        {
            if (parked == h) return r.Left > Off + 1000 ? Move(h) : true;      // Chromium moved it back: park again
            if (r.Left <= Off + 1000) { parked = h; return true; }
            // A fresh window (not shown to the user before) is parked; remember where it was.
            parked = h; parkedHome = r;
            return Move(h);
        }
    }

    static bool Move(IntPtr h) { return SetWindowPos(h, IntPtr.Zero, Off, Off, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE); }

    public static bool IsParked { get { lock (gate) return parked != IntPtr.Zero && IsWindow(parked); } }

    // Drops the parked handle without moving the window (it is about to be closed).
    public static void Forget()
    {
        lock (gate) { parked = IntPtr.Zero; }
    }

    // Moves a parked window back on screen. activate: bring it to the front (the user has to act).
    public static void Reveal(bool activate)
    {
        IntPtr h; RECT home;
        lock (gate) { h = parked; home = parkedHome; parked = IntPtr.Zero; }
        if (h == IntPtr.Zero || !IsWindow(h)) return;
        try
        {
            if (home.Left <= Off + 1000 || home.Right - home.Left < 100)
            {
                // Home unknown: centre on the primary screen.
                var a = System.Windows.Forms.Screen.PrimaryScreen.WorkingArea;
                var w = Math.Max(600, home.Right - home.Left); var ht = Math.Max(480, home.Bottom - home.Top);
                home = new RECT { Left = a.Left + (a.Width - w) / 2, Top = a.Top + (a.Height - ht) / 2 };
                home.Right = home.Left + w; home.Bottom = home.Top + ht;
            }
            if (IsIconic(h) || !IsWindowVisible(h))
            {
                var wp = new WINDOWPLACEMENT { length = Marshal.SizeOf(typeof(WINDOWPLACEMENT)) };
                if (GetWindowPlacement(h, ref wp)) { wp.normal = home; SetWindowPlacement(h, ref wp); }
            }
            else SetWindowPos(h, IntPtr.Zero, home.Left, home.Top, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
            if (activate)
            {
                if (IsIconic(h)) ShowWindow(h, SW_RESTORE);
                SetForegroundWindow(h);
            }
        }
        catch { }
    }

    // ---------- helpers ----------

    static void RestoreFocus(IntPtr prev)
    {
        try { if (prev != IntPtr.Zero && IsWindow(prev) && GetForegroundWindow() != prev) SetForegroundWindow(prev); } catch { }
    }

    // Visible, non-minimized top-level Chromium windows of a process (only `onScreen`: also the
    // ones off-screen are excluded when false is not needed; here both flags mean "usable window").
    static List<IntPtr> TopWindows(string processName, bool visibleOnly)
    {
        var pids = new HashSet<uint>();
        var procs = Process.GetProcessesByName(processName);
        try { foreach (var p in procs) pids.Add((uint)p.Id); }
        finally { foreach (var p in procs) p.Dispose(); }
        var list = new List<IntPtr>();
        if (pids.Count == 0) return list;
        EnumWindows((h, l) =>
        {
            uint pid; GetWindowThreadProcessId(h, out pid);
            if (!pids.Contains(pid) || GetWindow(h, 4) != IntPtr.Zero) return true;
            if (visibleOnly && (!IsWindowVisible(h) || IsIconic(h))) return true;
            var cls = new StringBuilder(64); GetClassName(h, cls, 64);
            if (!cls.ToString().StartsWith("Chrome_WidgetWin", StringComparison.Ordinal)) return true;
            RECT r; if (!GetWindowRect(h, out r) || r.Right - r.Left < 200 || r.Bottom - r.Top < 150) return true;
            list.Add(h);
            return true;
        }, IntPtr.Zero);
        return list;
    }
}
