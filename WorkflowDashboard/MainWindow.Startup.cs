using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace WorkflowDashboard;

/// <summary>
/// v1.0.2 deterministic launch placement (Tim: "always open centered").
///
/// Before: WindowStartupLocation=CenterScreen centers on the monitor that has the mouse cursor, and the
/// saved size preset was then applied in DIPs against whatever monitor the window happened to land on.
/// With Tim's second monitor (1920x1080 at x=-1920) that made the first frame/size depend on where the
/// cursor was and on the DPI transition between monitors, so it sometimes opened off-center.
///
/// Now: always the PRIMARY monitor's work area (not the cursor's), computed in physical pixels with the
/// primary monitor's own DPI, applied with SetWindowPos right after SourceInitialized (before the first
/// paint) and once more at the end of Loaded (after the size preset / zoom have run).
/// Size = the saved preset percentage of the work area (70/80/90; 90 % when none) clamped to MinWidth x
/// MinHeight and never larger than the work area. On Tim's 3840x1152 work area at 90 %: 3456 x 1037 at
/// (192, 58). Preset "full" still maximizes (on the primary). Nothing about panes / zones is touched.
/// No window position is persisted, so nothing saved can put it off-screen.
/// </summary>
public partial class MainWindow
{
    [StructLayout(LayoutKind.Sequential)] private struct StartupPoint { public int X, Y; }
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(StartupPoint pt, uint flags);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr hmon, int dpiType, out uint dpiX, out uint dpiY);
    private const uint MONITOR_DEFAULTTOPRIMARY = 1;
    private const uint SWP_NOZORDER_ = 0x0004, SWP_NOACTIVATE_ = 0x0010, SWP_NOOWNERZORDER_ = 0x0200;


    private bool _userMovedSinceLaunch;

    /// <summary>
    /// Tim: "opens a little low and to the right". The size can still change after Loaded (zoom, MinWidth /
    /// MinHeight from the pane count, late WPF layout), and WPF keeps Left/Top when it grows, so the window
    /// drifts right/down. Re-center the VISIBLE frame (DWM extended frame bounds, i.e. without any invisible
    /// resize border) on the primary work area a few times during startup, until the user moves/resizes it.
    /// </summary>
    private void ScheduleLaunchRecenter()
    {
        foreach (int ms in new[] { 400, 1500, 4000 })
        {
            var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
            t.Tick += (_, _) => { t.Stop(); RecenterVisibleOnPrimary($"launch+{ms}ms"); };
            t.Start();
        }
    }

    /// <summary>Move (never resize) so the visible frame is centered on the primary work area. No-op within 1 px.</summary>
    private void RecenterVisibleOnPrimary(string reason)
    {
        try
        {
            if (_userMovedSinceLaunch || _inSizeMove) return;
            if (WindowState != WindowState.Normal) return;
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            var hMon = MonitorFromPoint(new StartupPoint { X = 0, Y = 0 }, MONITOR_DEFAULTTOPRIMARY);
            if (hMon == IntPtr.Zero) return;
            var info = new NativeMethods.MonitorInfoEx { cbSize = Marshal.SizeOf<NativeMethods.MonitorInfoEx>() };
            if (!NativeMethods.GetMonitorInfo(hMon, ref info)) return;
            var work = info.rcWork;
            if (!NativeMethods.GetWindowRect(hwnd, out var win)) return;
            var vis = win;
            if (NativeMethods.DwmGetWindowAttributeRect(hwnd, NativeMethods.DWMWA_EXTENDED_FRAME_BOUNDS, out var f,
                    Marshal.SizeOf<NativeMethods.Rect>()) == 0 && f.Right > f.Left && f.Bottom > f.Top)
                vis = f;
            int visW = vis.Right - vis.Left, visH = vis.Bottom - vis.Top;
            int workW = work.Right - work.Left, workH = work.Bottom - work.Top;
            if (visW <= 0 || visH <= 0 || workW < 200 || workH < 200) return;
            // Larger than the work area: pin to its top-left instead of centering off-screen.
            int wantL = work.Left + Math.Max(0, (workW - visW) / 2);
            int wantT = work.Top + Math.Max(0, (int)Math.Round((workH - visH) / 2.0));
            int dx = wantL - vis.Left, dy = wantT - vis.Top;
            if (Math.Abs(dx) <= 1 && Math.Abs(dy) <= 1) return;
            const uint SWP_NOSIZE_ = 0x0001;
            _applyingSizePreset = true;
            try
            {
                NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, win.Left + dx, win.Top + dy, 0, 0,
                    SWP_NOSIZE_ | SWP_NOZORDER_ | SWP_NOACTIVATE_ | SWP_NOOWNERZORDER_);
            }
            finally { _applyingSizePreset = false; }
            System.Diagnostics.Debug.WriteLine($"[placement] recenter {reason}: moved {dx},{dy} vis {visW}x{visH}");
        }
        catch { }
    }

    /// <summary>Center the dashboard on the primary monitor's work area at the default/preset size.</summary>
    private void PlaceCenteredOnPrimary(string reason)
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            string preset = (_sizePreset ?? "").Trim().ToLowerInvariant();
            if (WindowState == WindowState.Maximized && preset == "full") return;
            if (WindowState != WindowState.Normal) WindowState = WindowState.Normal;

            var hMon = MonitorFromPoint(new StartupPoint { X = 0, Y = 0 }, MONITOR_DEFAULTTOPRIMARY);
            if (hMon == IntPtr.Zero) return;
            var info = new NativeMethods.MonitorInfoEx { cbSize = Marshal.SizeOf<NativeMethods.MonitorInfoEx>() };
            if (!NativeMethods.GetMonitorInfo(hMon, ref info)) return;
            var work = info.rcWork;
            int workW = work.Right - work.Left, workH = work.Bottom - work.Top;
            if (workW < 200 || workH < 200) return;

            double scale = 1.0;
            try { if (GetDpiForMonitor(hMon, 0, out uint dx, out _) == 0 && dx > 0) scale = dx / 96.0; } catch { }

            double pct = preset switch { "70" => 0.70, "80" => 0.80, _ => 0.90 };
            int minW = (int)Math.Ceiling(MinWidth * scale), minH = (int)Math.Ceiling(MinHeight * scale);
            int w = Math.Min(workW, Math.Max(minW, (int)Math.Round(workW * pct)));
            int h = Math.Min(workH, Math.Max(minH, (int)Math.Round(workH * pct)));
            int x = work.Left + (workW - w) / 2;
            int y = work.Top + (int)Math.Round((workH - h) / 2.0);

            _applyingSizePreset = true;
            try
            {
                NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, x, y, w, h, SWP_NOZORDER_ | SWP_NOACTIVATE_ | SWP_NOOWNERZORDER_);
            }
            finally { _applyingSizePreset = false; }
            RecenterVisibleOnPrimary(reason + "+frame");
            System.Diagnostics.Debug.WriteLine($"[placement] {reason}: {x},{y} {w}x{h} work {work.Left},{work.Top} {workW}x{workH} dpi {scale * 96:0}");
        }
        catch { }
    }
}
