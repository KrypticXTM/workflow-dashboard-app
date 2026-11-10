using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace WorkflowDashboard;

/// <summary>
/// v1.0.2 pop-out (Picture in Picture) watchdog. Parking used to live only inside the media-preview resolve,
/// which runs for an ACTIVE SMTC session whose tab is in the background - so a PiP with no session (Opera
/// drops its SMTC session while the video is popped out), a session for another tab, or a PiP opened between
/// refreshes sat on screen over the dashboard, and after 3 re-park attempts it gave up for good.
/// Now: whenever "Hide browser pop-out video" is on and any Chromium PiP window exists, park it (1 px column,
/// keeps painting), regardless of the shown session. Opera moving it back -> re-park with backoff
/// (1 s doubling to 30 s, never gives up). Never while Tim is on it (foreground or pointer over it).
/// Setting off / exit -> put it back where the browser had it.
/// </summary>
public partial class MainWindow
{
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativeMethods.POINT p);

    private DispatcherTimer? _pipTimer;
    private DateTime _pipNextTryUtc;
    private double _pipBackoffSec = 1;
    private DateTime _pipStableSinceUtc;

    private void StartPipWatch()
    {
        if (_pipTimer != null) return;
        _pipTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(1000) };
        _pipTimer.Tick += (_, _) => PipWatchTick();
        _pipTimer.Start();
    }

    private void StopPipWatch()
    {
        try { _pipTimer?.Stop(); } catch { }
        _pipTimer = null;
    }

    /// <summary>Any visible browser pop-out window (title "Picture in Picture", Chrome_WidgetWin_*).</summary>
    private static IntPtr FindAnyBrowserPip()
    {
        IntPtr found = IntPtr.Zero;
        NativeMethods.EnumWindows((h, _) =>
        {
            if (!NativeMethods.IsWindowVisible(h) || NativeMethods.IsIconic(h)) return true;
            if (!IsPopOutWindow(h)) return true;
            if (!BrowserProcs.Contains(ProcessNameOfHwnd(h))) return true;
            found = h;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    private static bool UserIsOnWindow(IntPtr h)
    {
        var fg = NativeMethods.GetForegroundWindow();
        if (fg == h || (fg != IntPtr.Zero && GetAncestor(fg, GA_ROOT) == h)) return true;
        if (GetCursorPos(out var p) && NativeMethods.GetWindowRect(h, out var r))
            return p.X >= r.Left && p.X < r.Right && p.Y >= r.Top && p.Y < r.Bottom;
        return false;
    }

    private void PipWatchTick()
    {
        try
        {
            // Setting off, or the dashboard (and so the Media preview) is minimized: the pop-out is the only
            // place the video can be seen -> leave it / put it back on screen.
            if (!_settings.HideBrowserPopOut || WindowState == System.Windows.WindowState.Minimized || _deskMinimized)
            {
                if (_popOutParked) { UnparkPopOut(); UpdateMediaPreview(); }
                return;
            }
            var pip = FindAnyBrowserPip();
            if (pip == IntPtr.Zero)
            {
                // Closed, or (Opera) HIDDEN - Opera keeps the "Picture in Picture" window alive but invisible after
                // the video returns to the tab. A hidden pop-out has no DWM content (the preview showed a generic
                // app-icon placeholder): forget it and put it back where the browser had it.
                if (_popOutHwnd != IntPtr.Zero)
                {
                    if (NativeMethods.IsWindow(_popOutHwnd)) ReleasePopOut();
                    _popOutHwnd = IntPtr.Zero;
                    _popOutParked = false;
                    _popOutParkTries = 0;
                    UpdateMediaPreview();
                }
                return;
            }
            var now = DateTime.UtcNow;
            if (pip != _popOutHwnd)
            {
                ReleasePopOut();
                _popOutHwnd = pip;
                _popOutParked = false;
                _popOutParkTries = 0;
                _pipBackoffSec = 1;
                _pipNextTryUtc = now;
            }
            if (!NativeMethods.GetWindowRect(pip, out var r)) return;
            bool atPark = _popOutParked && r.Left == _popOutParkRect.Left && r.Top == _popOutParkRect.Top;
            if (atPark)
            {
                // Stayed parked 10 s -> forget earlier fights.
                if ((now - _pipStableSinceUtc).TotalSeconds > 10) _pipBackoffSec = 1;
                return;
            }
            // Tim is using it (dragged it out, clicking its controls): leave it until he moves on.
            if (UserIsOnWindow(pip)) { _pipNextTryUtc = now; return; }
            if (now < _pipNextTryUtc) return;

            bool firstPark = !_popOutParked;
            if (firstPark) _popOutOrigRect = r;   // where the browser put it (restored on exit / setting off)
            ParkPopOut(pip, r);
            _pipStableSinceUtc = now;
            if (!firstPark)
            {
                // The browser (or Tim) moved it back on screen: retry with backoff, never give up.
                _pipNextTryUtc = now.AddSeconds(_pipBackoffSec);
                _pipBackoffSec = Math.Min(30, _pipBackoffSec * 2);
            }
            UpdateMediaPreview();
        }
        catch { }
    }
}
