using System.Runtime.InteropServices;

namespace WorkflowDashboard;

/// <summary>
/// v1.0.2 live-resize stability. Panes blanked briefly while Tim dragged the dashboard frame because every
/// debounced mid-drag retile ran the full ApplyLiveTiling -> AssertAllAssignedLiveTiles, whose
/// RaiseDashboardAboveUnlinked() does SetWindowPos(desk, HWND_TOP): the (topmost) desk jumps to the top of the
/// TOPMOST band - ABOVE every Linked tile - and the tiles are then re-pinned one by one with cross-process
/// SetWindowPos calls (a resizing Chromium answers slowly), so for a few frames the opaque desk covers them.
/// Each tile was also moved separately with HWND_TOP. Mid-drag collapse (one-way) could minimize a tile too.
/// Now, during WM_ENTERSIZEMOVE..WM_EXITSIZEMOVE: the tiles are moved in ONE DeferWindowPos batch with
/// SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER (no z-order change, no show/hide, no activation); the
/// desk is never re-raised above its tiles while it is already topmost; collapse is not evaluated (the
/// WM_EXITSIZEMOVE settle pass does it). Anything the batch can't do safely (fit mode, crop, pane below the
/// app's minimum, minimized/maximized tile) falls back to the normal path. Zone math is unchanged.
/// </summary>
public partial class MainWindow
{
    [DllImport("user32.dll")] private static extern IntPtr BeginDeferWindowPos(int nNumWindows);
    [DllImport("user32.dll")] private static extern IntPtr DeferWindowPos(IntPtr hWinPosInfo, IntPtr hWnd, IntPtr hWndInsertAfter,
        int x, int y, int cx, int cy, uint uFlags);
    [DllImport("user32.dll")] private static extern bool EndDeferWindowPos(IntPtr hWinPosInfo);

    /// <summary>True while the WM_EXITSIZEMOVE settle pass runs (same z-order care as the drag itself).</summary>
    private bool _sizeMoveSettlePass;

    /// <summary>Desk already topmost with no tile beneath it: re-raising it would only bury the tiles.</summary>
    private bool SkipDeskRaiseForResize()
    {
        if (!(_inSizeMove || _sizeMoveSettlePass)) return false;
        try { return NativeMethods.IsWindowTopmost(_ownHwnd) && !AnyLiveTileBuried(); }
        catch { return false; }
    }

    /// <summary>
    /// Mid-drag placement: every placeable Linked tile in one batch, no z-order. False = caller must run the
    /// normal per-window path (nothing has been moved in that case).
    /// </summary>
    private bool TryBatchLiveTilesDuringDrag(NativeMethods.Rect[] zones)
    {
        var items = new List<(IntPtr H, int X, int Y, int Cx, int Cy)>();
        for (int i = 0; i < _paneCount; i++)
        {
            var w = _tiler.GetZone(i);
            if (w == null || w.Parked) continue;
            if (!NativeMethods.IsWindow(w.Hwnd)) continue;
            if (i >= zones.Length || zones[i].IsEmpty) continue;
            if (w.Collapsed || w.Floating || w.PreviewProxy) continue;   // never placed into the zone mid-drag
            if (CropPreviewEnabled && (w.HasCrop || IsCropInteractionZone(i))) return false;
            if (!string.Equals(FitModeForZone(i), "fill", StringComparison.OrdinalIgnoreCase)) return false;
            if (NativeMethods.IsIconic(w.Hwnd) || NativeMethods.IsZoomed(w.Hwnd) || !NativeMethods.IsWindowVisible(w.Hwnd)) return false;
            NativeMethods.GetWindowMinTrackSize(w.Hwnd, out int minW, out int minH);
            if (!NativeMethods.PaneFitsMin(zones[i], minW, minH)) return false;
            var z = zones[i];
            if (NativeMethods.IsVisiblyAt(w.Hwnd, z, tol: 2)) continue;

            // Visible frame == zone: compensate the invisible DWM resize border (same as SetVisibleFrame).
            int x = z.Left, y = z.Top, cx = Math.Max(1, z.Width), cy = Math.Max(1, z.Height);
            if (NativeMethods.GetWindowRect(w.Hwnd, out var outer) && NativeMethods.TryGetVisibleFrame(w.Hwnd, out var vf) && !vf.IsEmpty)
            {
                int ml = vf.Left - outer.Left, mt = vf.Top - outer.Top, mr = outer.Right - vf.Right, mb = outer.Bottom - vf.Bottom;
                if (ml >= 0 && mt >= 0 && mr >= 0 && mb >= 0 && ml + mr < 64 && mt + mb < 64)
                {
                    x -= ml; y -= mt; cx += ml + mr; cy += mt + mb;
                }
            }
            items.Add((w.Hwnd, x, y, cx, cy));
        }
        if (items.Count == 0) return true;
        IntPtr hdwp = BeginDeferWindowPos(items.Count);
        if (hdwp == IntPtr.Zero) return false;
        const uint flags = NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE | SWP_NOOWNERZORDER_MP;
        foreach (var it in items)
        {
            hdwp = DeferWindowPos(hdwp, it.H, IntPtr.Zero, it.X, it.Y, it.Cx, it.Cy, flags);
            if (hdwp == IntPtr.Zero) return false;   // the system freed the batch; fall back
        }
        return EndDeferWindowPos(hdwp);
    }
}
