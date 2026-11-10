using System.Windows;

namespace WorkflowDashboard;

/// <summary>
/// v1.0.2 "panes blink when I click the dashboard" fix.
///
/// Root cause (measured on the running app): SoftRefresh (800 ms) -> EnsureLiveTilesTopmostIfNeeded compared each
/// tile's OUTER rect (GetWindowRect, which includes the 7 px invisible resize borders) with the pane zone, which
/// is a VISIBLE-frame rect. Every correctly placed tile failed the check, so a "heal" Assert ran on every tick,
/// forever. While the desk is active that Assert first lifted the desk (Topmost=true / SetWindowPos HWND_TOP)
/// above all three TOPMOST tiles and only then re-pinned them one by one - three cross-process SetWindowPos calls
/// into Opera / Electron. Whenever those apps were slow to answer (busy UI thread: list rebuilds, Docs, color
/// changes, media actions...), the opaque desk was on screen over every pane for a few frames.
///
/// Fix: (1) the heal check uses the same visible-frame test as EnsureTiledVisibleNoActivate, so healthy tiles
/// are a no-op; (2) the desk is made topmost and immediately tucked directly BELOW the lowest Linked tile
/// (two calls on our own window, no cross-process wait), so it never sits above a pane. Zones are unchanged.
/// </summary>
public partial class MainWindow
{
    /// <summary>Preview auto-close probe: pane <paramref name="z"/> holds a live linked window.</summary>
    private bool IsPaneFilled(int z)
    {
        if (z < 0 || z >= _paneCount) return true;
        var w = _tiler.GetZone(z);
        return w != null && !w.Parked && NativeMethods.IsWindow(w.Hwnd);
    }

    /// <summary>Visible, placed Linked tiles that are in the TOPMOST band (the ones the desk must stay under).</summary>
    private HashSet<IntPtr> TopmostLiveTiles()
    {
        var set = new HashSet<IntPtr>();
        if (!_liveDesk) return set;
        for (int i = 0; i < _paneCount; i++)
        {
            var w = _tiler.GetZone(i);
            if (w == null || w.Parked || w.PreviewProxy || w.Collapsed) continue;
            if (!NativeMethods.IsWindow(w.Hwnd) || NativeMethods.IsIconic(w.Hwnd) || !NativeMethods.IsWindowVisible(w.Hwnd)) continue;
            if (!NativeMethods.IsWindowTopmost(w.Hwnd)) continue;
            set.Add(w.Hwnd);
        }
        return set;
    }

    /// <summary>
    /// Desk topmost (above ordinary apps) but directly beneath the lowest Linked tile: never covers a pane.
    /// Falls back to the old Topmost=true when no tile is in the TOPMOST band (nothing to cover).
    /// </summary>
    private void SetDeskTopmostBelowTiles()
    {
        if (_ownHwnd == IntPtr.Zero || _snipYieldActive) return;
        if (WindowState == WindowState.Minimized || _deskMinimized) return;
        var tiles = TopmostLiveTiles();
        // Normal-layer case: the desk is NOT topmost but is the active window - Windows already put it at the top
        // of the normal band, directly under the TOPMOST tiles. Making it topmost now (WPF Topmost=true =
        // SetWindowPos HWND_TOPMOST) would lift it over every tile first. Nothing to gain: as the foreground window
        // it is already above every ordinary app, so leave it there.
        if (tiles.Count > 0 && IsActive && !NativeMethods.IsWindowTopmost(_ownHwnd)) return;
        try { if (!Topmost) Topmost = true; } catch { }
        if (!NativeMethods.IsWindowTopmost(_ownHwnd))
            NativeMethods.SetWindowPos(_ownHwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
        if (tiles.Count == 0) return;
        TuckDeskUnderTiles(tiles);
    }

    /// <summary>Insert the (topmost) desk right after the lowest of <paramref name="tiles"/> in z-order.</summary>
    private void TuckDeskUnderTiles(HashSet<IntPtr> tiles)
    {
        try
        {
            IntPtr lowest = IntPtr.Zero, h = _ownHwnd;
            int left = tiles.Count;
            // Tiles below the desk: the lowest is the last one met walking down.
            for (int n = 0; n < 4000 && left > 0; n++)
            {
                h = GetWindow(h, GW_HWNDNEXT);
                if (h == IntPtr.Zero) break;
                if (tiles.Contains(h)) { lowest = h; left--; }
            }
            if (lowest == IntPtr.Zero) return;   // every tile already above the desk: leave z-order alone
            NativeMethods.SetWindowPos(_ownHwnd, lowest, 0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
        }
        catch { }
    }

    // ---------------------------------------------------------------- activation intercept

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct WINDOWPOS { public IntPtr hwnd, hwndInsertAfter; public int x, y, cx, cy; public uint flags; }
    private const int WM_WINDOWPOSCHANGING = 0x0046;
    private const uint SWP_NOZORDER_Z = 0x0004;

    /// <summary>&gt;0 while a caller deliberately needs the desk above the panes (owned dialogs).</summary>
    private int _deskAboveTilesAllowed;

    /// <summary>
    /// Clicking the dashboard makes Windows raise it to the top of the TOPMOST band (above every Linked tile)
    /// before Activated runs; for 2-5 ms the desk covered the panes. When the (already topmost) desk is asked to
    /// go to HWND_TOP / HWND_TOPMOST, insert it right under the lowest topmost Linked tile instead. Only the
    /// insert-after handle changes; position, size, flags and activation are untouched. Not applied when the desk
    /// is not yet topmost (inserting after a topmost window would drop it to the normal band), when nothing is
    /// linked, minimized, during a snip, or while a dialog path explicitly allows the desk on top.
    /// </summary>
    private IntPtr DeskZWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_WINDOWPOSCHANGING || lParam == IntPtr.Zero) return IntPtr.Zero;
        try
        {
            if (!_liveDesk || _snipYieldActive || _deskAboveTilesAllowed > 0 || _deskMinimized) return IntPtr.Zero;
            var wp = System.Runtime.InteropServices.Marshal.PtrToStructure<WINDOWPOS>(lParam);
            if ((wp.flags & SWP_NOZORDER_Z) != 0) return IntPtr.Zero;
            if (wp.hwndInsertAfter != IntPtr.Zero && wp.hwndInsertAfter != NativeMethods.HWND_TOPMOST) return IntPtr.Zero;
            if (!NativeMethods.IsWindowTopmost(hwnd) || NativeMethods.IsIconic(hwnd)) return IntPtr.Zero;
            var tiles = TopmostLiveTiles();
            if (tiles.Count == 0) return IntPtr.Zero;
            // Lowest topmost tile in the current z-order (walk from the top of the desktop).
            IntPtr lowest = IntPtr.Zero;
            int left = tiles.Count;
            for (IntPtr h = GetWindow(hwnd, GW_HWNDFIRST); h != IntPtr.Zero && left > 0; h = GetWindow(h, GW_HWNDNEXT))
                if (tiles.Contains(h)) { lowest = h; left--; }
            if (lowest == IntPtr.Zero || lowest == hwnd) return IntPtr.Zero;
            wp.hwndInsertAfter = lowest;
            System.Runtime.InteropServices.Marshal.StructureToPtr(wp, lParam, false);
        }
        catch { }
        return IntPtr.Zero;
    }
    private const uint GW_HWNDFIRST = 0;
}
