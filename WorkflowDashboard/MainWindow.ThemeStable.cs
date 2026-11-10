using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;

namespace WorkflowDashboard;

/// <summary>
/// v1.0.2: (1) Color/theme changes are purely visual for Linked panes: the strip overlays the header (no
/// pane geometry change), theme apply only re-raises tiles when one is actually BELOW the desk in z-order,
/// and Activated during the chrome-quiet window re-raises siblings (it used to skip, leaving the desk's
/// TOPMOST above the tiles = blank panes). (2) "Expand dashboard to fit" for the too-small pane prompt.
/// </summary>
public partial class MainWindow
{
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);
    private const uint GW_HWNDPREV = 3;

    /// <summary>True when <paramref name="hwnd"/> sits below the dashboard in z-order (desk paints over it).</summary>
    private bool IsTileBelowDesk(IntPtr hwnd)
    {
        if (_ownHwnd == IntPtr.Zero || hwnd == IntPtr.Zero) return false;
        IntPtr h = _ownHwnd;
        for (int i = 0; i < 4000; i++)
        {
            h = GetWindow(h, GW_HWNDPREV);
            if (h == IntPtr.Zero) return true;   // reached the top without meeting the tile
            if (h == hwnd) return false;
        }
        return false;
    }

    /// <summary>Any visible, tiled Linked HWND hidden, minimized, or beneath the desk?</summary>
    private bool AnyLiveTileBuried()
    {
        if (!_liveDesk) return false;
        for (int i = 0; i < _paneCount; i++)
        {
            var w = _tiler.GetZone(i);
            if (w == null || w.Parked || w.PreviewProxy || w.Collapsed) continue;
            if (!NativeMethods.IsWindow(w.Hwnd)) continue;
            if (NativeMethods.IsIconic(w.Hwnd) || !NativeMethods.IsWindowVisible(w.Hwnd)) return true;
            if (IsTileBelowDesk(w.Hwnd)) return true;
        }
        return false;
    }

    /// <summary>Theme apply: leave Linked windows alone unless one is actually buried.</summary>
    private void ReassertLiveTilesIfBuried()
    {
        try { if (AnyLiveTileBuried()) ReassertLiveTilesAfterColorChrome(); } catch { }
    }

    private const uint GW_HWNDNEXT = 2;

    /// <summary>
    /// Snip / outside-focus safe heal (desk NOT active): if any tiled Linked HWND sits below the desk, slide
    /// the DESK under the lowest such tile. No TOPMOST changes on tiles, no activation, no move/size, no
    /// show/hide. Cause it repairs: after a snip the desk was flipped Topmost=true then NOTOPMOST, which
    /// re-inserts it at the top of the normal band - above tiles whose TOPMOST the outside-focus yield had
    /// dropped (pane looks empty/grey).
    /// </summary>
    private void LowerDeskBelowBuriedTiles()
    {
        if (!_liveDesk || _ownHwnd == IntPtr.Zero || _snipYieldActive) return;
        if (WindowState == WindowState.Minimized || _deskMinimized) return;
        try
        {
            var buried = new HashSet<IntPtr>();
            for (int i = 0; i < _paneCount; i++)
            {
                var w = _tiler.GetZone(i);
                if (w == null || w.Parked || w.PreviewProxy || w.Collapsed) continue;
                if (!NativeMethods.IsWindow(w.Hwnd) || NativeMethods.IsIconic(w.Hwnd) || !NativeMethods.IsWindowVisible(w.Hwnd)) continue;
                if (IsTileBelowDesk(w.Hwnd)) buried.Add(w.Hwnd);
            }
            if (buried.Count == 0) return;
            // A topmost desk can only be lowered by first dropping it into the normal band.
            if (NativeMethods.IsWindowTopmost(_ownHwnd))
            {
                try { Topmost = false; } catch { }
                NativeMethods.SetWindowPos(_ownHwnd, NativeMethods.HWND_NOTOPMOST, 0, 0, 0, 0,
                    NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
            }
            // Lowest buried tile = last one met walking down from the desk.
            IntPtr lowest = IntPtr.Zero, h = _ownHwnd;
            int left = buried.Count;
            for (int n = 0; n < 4000 && left > 0; n++)
            {
                h = GetWindow(h, GW_HWNDNEXT);
                if (h == IntPtr.Zero) break;
                if (buried.Contains(h)) { lowest = h; left--; }
            }
            if (lowest != IntPtr.Zero)
                NativeMethods.SetWindowPos(_ownHwnd, lowest, 0, 0, 0, 0,
                    NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
        }
        catch { }
    }

    private static bool SameZones(NativeMethods.Rect[] a, NativeMethods.Rect[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
            if (a[i].Left != b[i].Left || a[i].Top != b[i].Top || a[i].Right != b[i].Right || a[i].Bottom != b[i].Bottom)
                return false;
        return true;
    }

    /// <summary>Overlay the color strip on header row 0, left of the right-hand group (it holds the Color key).</summary>
    private void PositionColorStripLayer()
    {
        if (ColorStripLayer == null || HeaderRightGroup == null) return;
        try
        {
            bool rightOnRow0 = System.Windows.Controls.Grid.GetRow(HeaderRightGroup) == 0;
            double right = rightOnRow0 ? HeaderRightGroup.ActualWidth + 8 : 0;
            ColorStripLayer.Margin = new Thickness(0, 0, right, 0);
            ColorStripLayer.HorizontalAlignment = HorizontalAlignment.Stretch;
        }
        catch { }
    }

    // ---------------- Too-small prompt: Expand dashboard to fit ----------------

    private void FitPrompt_ExpandDesk_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not FrameworkElement fe || fe.Tag is not int zone) return;
        var w = _tiler.GetZone(zone);
        if (w == null || !NativeMethods.IsWindow(w.Hwnd)) return;
        ExpandDashboardToFit(zone, w);
    }

    /// <summary>
    /// Grow the dashboard to the SMALLEST size at which pane <paramref name="zone"/> meets the linked app's
    /// min track size. Extra desk width/height is shared by the panes in the layout's ratios, so the
    /// shortfall is divided by this pane's share of the desk; re-measured up to 4 passes (min-track
    /// rebalancing / rounding). Centered on the current window, kept inside and capped at the work area.
    /// </summary>
    private void ExpandDashboardToFit(int zone, WorkingWindow w, int minW = -1, int minH = -1, bool fromCollapseCard = false)
    {
        if (minW < 0 || minH < 0) NativeMethods.GetWindowMinTrackSize(w.Hwnd, out minW, out minH);
        var zones = ComputePaneScreenZones();
        if (zone >= zones.Length || zones[zone].IsEmpty) { StatusText.Text = "Expand to fit: pane not measurable"; return; }
        if (!TryGetWorkAreaDip(out double wl, out double wt, out double ww, out double wh))
        { StatusText.Text = "Expand to fit: monitor work area unavailable"; return; }

        var dpi = VisualTreeHelper.GetDpi(this);
        double sx = dpi.DpiScaleX <= 0 ? 1 : dpi.DpiScaleX, sy = dpi.DpiScaleY <= 0 ? 1 : dpi.DpiScaleY;
        bool maximized = WindowState == WindowState.Maximized;
        double curW = maximized ? ww : ActualWidth, curH = maximized ? wh : ActualHeight;
        double curL = maximized ? wl : Left, curT = maximized ? wt : Top;
        double deskWpx = Math.Max(1, (PaneGrid?.ActualWidth ?? 0) * sx), deskHpx = Math.Max(1, (PaneGrid?.ActualHeight ?? 0) * sy);
        double maxH = _lockHeight ? curH : wh;   // Lock H: height is pinned, grow width only

        // Projection from the layout ratios: this pane receives (zone / desk) of every extra desk pixel.
        (double tw, double th) Project(NativeMethods.Rect z, double baseW, double baseH)
        {
            double rw = Math.Clamp(z.Width / deskWpx, 0.05, 1), rh = Math.Clamp(z.Height / deskHpx, 0.05, 1);
            double dw = Math.Max(0, minW - z.Width) / rw / sx, dh = Math.Max(0, minH - z.Height) / rh / sy;
            return (dw > 0 ? baseW + Math.Ceiling(dw) + 2 : baseW, dh > 0 ? baseH + Math.Ceiling(dh) + 2 : baseH);
        }
        var (needW, needH) = Project(zones[zone], curW, curH);
        if (needW > ww + 0.5 || needH > maxH + 0.5 || (maximized && (needW > curW || needH > curH)))
        {
            ReportExpandTooBig(zone, w, minW, minH, ww, maxH, fromCollapseCard);
            return;
        }

        bool fits = false;
        _applyingSizePreset = true;
        try
        {
            if (maximized) WindowState = WindowState.Normal;
            for (int pass = 0; pass < 4; pass++)
            {
                double nw = Math.Min(ww, Math.Max(curW, needW));
                double nh = Math.Min(maxH, Math.Max(curH, needH));
                double nl = Math.Clamp(curL + (curW - nw) / 2.0, wl, wl + ww - nw);
                double nt = _lockHeight ? curT : Math.Clamp(curT + (curH - nh) / 2.0, wt, wt + wh - nh);
                Left = nl; Top = nt; Width = nw; Height = nh;
                try { UpdateLayout(); PaneGrid?.UpdateLayout(); } catch { }
                zones = ComputePaneScreenZones();
                if (zone >= zones.Length || zones[zone].IsEmpty) break;
                if (NativeMethods.PaneFitsMin(zones[zone], minW, minH)) { fits = true; break; }
                if (nw >= ww - 0.5 && nh >= maxH - 0.5) break;   // at the work-area cap
                deskWpx = Math.Max(1, (PaneGrid?.ActualWidth ?? 0) * sx); deskHpx = Math.Max(1, (PaneGrid?.ActualHeight ?? 0) * sy);
                curW = nw; curH = nh; curL = nl; curT = nt;
                (needW, needH) = Project(zones[zone], nw, nh);
            }
        }
        finally { _applyingSizePreset = false; }

        _sizePreset = "";   // custom size now
        try { SyncSizeUi(); } catch { }
        try { Persist(); } catch { }
        if (fits && fromCollapseCard)
        {
            // Collapse card: the retile's hysteresis re-expands auto collapses; manual/floating ones are
            // docked explicitly now that the pane meets the need.
            AfterDashboardSizeApplied();
            if (w.Collapsed || w.Floating) try { ExpandCollapsedPane(zone); } catch { }
            StatusText.Text = $"Dashboard expanded to {Math.Round(Width)}x{Math.Round(Height)} - {w.ShortTitle} fits (min {minW}x{minH})";
        }
        else if (fits)
        {
            w.ShowFitPrompt = false;
            ShowFitPromptUi(zone, false);
            EnterLiveFill(w);
            AfterDashboardSizeApplied();
            StatusText.Text = $"Dashboard expanded to {Math.Round(Width)}x{Math.Round(Height)} - {w.ShortTitle} fits (min {minW}x{minH})";
        }
        else
        {
            if (_liveDesk) try { ApplyLiveTiling(); } catch { }
            RepairAllTilesZOrderAboveDashboard(keepFocusHwnd: IntPtr.Zero);
            ReportExpandTooBig(zone, w, minW, minH, ww, maxH, fromCollapseCard);
        }
    }

    private void ReportExpandTooBig(int zone, WorkingWindow w, int minW, int minH, double ww, double maxH, bool fromCollapseCard = false)
    {
        if (fromCollapseCard)
        {
            StatusText.Text = $"Even the full work area ({Math.Round(ww)}x{Math.Round(maxH)}) can't fit {w.ShortTitle} (min {minW}x{minH}) in this pane"
                              + " - use Expand (floats it over the desk), Expand pane, or a larger layout";
            return;
        }
        w.ShowFitPrompt = true;
        ShowFitPromptUi(zone, true);
        StatusText.Text = $"Even the full work area ({Math.Round(ww)}x{Math.Round(maxH)}) can't fit {w.ShortTitle} (min {minW}x{minH}) in this pane"
                          + " - try Change layout, Move to main pane, or Keep preview";
    }

    /// <summary>Collapse card (v1.0.1 Live too-small surface): grow so the pane clears the re-expand threshold.</summary>
    private void CollapseCardGrowDesk_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not FrameworkElement fe || fe.Tag is not int zone) return;
        var w = _tiler.GetZone(zone);
        if (w == null || !NativeMethods.IsWindow(w.Hwnd)) return;
        ComputeCollapseNeed(w, zone, out int needW, out int needH);
        // Same 1.10 hysteresis the auto re-expand uses, so the retile docks it instead of re-collapsing.
        ExpandDashboardToFit(zone, w, (int)Math.Ceiling(needW * 1.10), (int)Math.Ceiling(needH * 1.10), fromCollapseCard: true);
    }
}
