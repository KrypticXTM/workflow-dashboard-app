using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace WorkflowDashboard;

/// <summary>
/// v1.0.1 pane collapse.
///
/// When a pane becomes too small for its linked window (even after the existing Fill / FitWidth +
/// HardClamp / HonorAssignedMinTracks steps), the real window is minimized (SW_SHOWMINNOACTIVE, never
/// SW_HIDE) and stays Linked + tracked, while the pane shows a compact themed card (live DWM thumbnail,
/// or icon fallback, + title + Expand). Collapsed windows also carry PreviewProxy=true, so every existing
/// Live z-order / heal / TOPMOST path (all of which already skip PreviewProxy) leaves them alone.
///
/// Threshold (physical px, per pane, computed on the zones ApplyLiveTiling is about to use):
///   needW = max(raw WM_GETMINMAXINFO ptMinTrackSize.x, _overflowMinWidth[i], 240 DIP)
///   needH = max(raw ptMinTrackSize.y, 160 DIP)
///   collapse    when zone.W &lt; needW || zone.H &lt; needH
///   re-expand   (auto collapses only) when zone.W &gt;= 1.10*needW &amp;&amp; zone.H &gt;= 1.10*needH
/// Between the Live-desk tile floor (280x200) and the threshold, the existing Fill / FitWidth+center
/// behavior still runs (the app can really shrink there, so nothing overflows).
///
/// Live resize: WM_ENTERSIZEMOVE..WM_EXITSIZEMOVE stretches the retile debounce to 250 ms and only
/// never collapses or expands mid-drag (v1.0.2: tiles move in one DeferWindowPos batch, no z-order -
/// MainWindow.LiveResize.cs); WM_EXITSIZEMOVE schedules one settle pass
/// (140 ms) that runs the full evaluation incl. hysteresis re-expand. Maximize/restore take the
/// normal 75 ms SizeChanged settle.
///
/// Expand: if the pane now fits (zone &gt;= need), the window is restored straight into the pane (Live).
/// Otherwise it floats centered over the desk (max(min size, 60% of the desk), clamped to the monitor
/// work area), pinned in the Live TOPMOST band, still linked; the card switches to "Floating" with a
/// Collapse button, and it docks automatically once the pane grows past the hysteresis threshold.
/// </summary>
public partial class MainWindow
{
    private const double CollapseFloorWidthDip = 240;
    private const double CollapseFloorHeightDip = 160;
    private const double CollapseReexpandFactor = 1.10;
    private const int LiveRetileNormalMs = 75;
    private const int LiveRetileSizeMoveMs = 250;
    private const int SizeMoveSettleMs = 140;
    private const int WmEnterSizeMove = 0x0231;
    private const int WmExitSizeMove = 0x0232;

    private bool _inSizeMove;
    private DispatcherTimer? _sizeMoveSettleTimer;
    private readonly HashSet<IntPtr> _collapseSeenIconic = new();

    private sealed class CollapseCard
    {
        public Grid Host = null!;
        public Border Glow = null!;
        public Border Card = null!;
        public ThumbnailHost Thumb = null!;
        public Image Icon = null!;
        public TextBlock Title = null!;
        public TextBlock Status = null!;
        public Button PrimaryBtn = null!;
        public Button SecondaryBtn = null!;
        public Button GrowDeskBtn = null!;
    }
    private readonly CollapseCard?[] _collapseCards = new CollapseCard?[4];

    // ---------------------------------------------------------------- size-move tracking

    private void InitCollapseFeature()
    {
        try
        {
            var src = PresentationSource.FromVisual(this) as HwndSource;
            src?.AddHook(CollapseWndProc);
        }
        catch { }
    }

    private IntPtr CollapseWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmEnterSizeMove)
        {
            _inSizeMove = true;
            _userMovedSinceLaunch = true;   // user grabbed the frame: launch re-centering stops (Startup.cs)
            try { _sizeMoveSettleTimer?.Stop(); } catch { }
        }
        else if (msg == WmExitSizeMove)
        {
            _inSizeMove = false;
            ScheduleSizeMoveSettle();
        }
        return IntPtr.Zero;
    }

    private int CurrentLiveRetileIntervalMs() => _inSizeMove ? LiveRetileSizeMoveMs : LiveRetileNormalMs;

    /// <summary>One authoritative layout + collapse/expand pass after the user lets go of the frame.</summary>
    private void ScheduleSizeMoveSettle()
    {
        if (!_liveDesk) return;
        if (_sizeMoveSettleTimer == null)
        {
            _sizeMoveSettleTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(SizeMoveSettleMs) };
            _sizeMoveSettleTimer.Tick += (_, _) =>
            {
                _sizeMoveSettleTimer!.Stop();
                if (_inSizeMove || !_liveDesk) return;
                if (WindowState == WindowState.Minimized || _deskMinimized) return;
                try { _liveRetileTimer?.Stop(); } catch { }
                try
                {
                    UpdateLayout();
                    PaneGrid?.UpdateLayout();
                }
                catch { }
                _sizeMoveSettlePass = true;
                try { ApplyLiveTiling(); }
                finally { _sizeMoveSettlePass = false; }
            };
        }
        _sizeMoveSettleTimer.Stop();
        _sizeMoveSettleTimer.Start();
    }

    // ---------------------------------------------------------------- threshold

    private void GetCollapseFloorPx(out int floorW, out int floorH)
    {
        double sx = 1.0, sy = 1.0;
        try
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            sx = dpi.DpiScaleX;
            sy = dpi.DpiScaleY;
        }
        catch { }
        floorW = (int)Math.Ceiling(CollapseFloorWidthDip * sx);
        floorH = (int)Math.Ceiling(CollapseFloorHeightDip * sy);
    }

    private void ComputeCollapseNeed(WorkingWindow w, int zone, out int needW, out int needH)
    {
        GetCollapseFloorPx(out int floorW, out int floorH);
        int rawW = 0, rawH = 0;
        try { NativeMethods.GetWindowMinTrackSizeRaw(w.Hwnd, out rawW, out rawH); } catch { }
        if (rawW > 8000) rawW = 0;
        if (rawH > 8000) rawH = 0;
        int overflow = zone >= 0 && zone < _overflowMinWidth.Length ? _overflowMinWidth[zone] : 0;
        needW = Math.Max(floorW, Math.Max(rawW, overflow));
        needH = Math.Max(floorH, rawH);
    }

    private static bool ZoneMeets(NativeMethods.Rect z, int needW, int needH, double factor)
        => z.Width >= (int)Math.Ceiling(needW * factor) && z.Height >= (int)Math.Ceiling(needH * factor);

    /// <summary>
    /// Called by ApplyLiveTiling before placement. Returns true when any collapse state changed
    /// (caller recomputes zones: collapsed panes no longer reserve min-track width).
    /// </summary>
    private bool EvaluateCollapseStates(NativeMethods.Rect[] zones)
    {
        if (!_liveDesk || zones == null || zones.Length == 0) return false;
        // Fresh link: keep the HWND Live/clamped; collapsing here minimizes it
        // (it looks like the window vanished the moment it was linked).
        if (LiveLinkGuard.ShouldSkipCollapseDuringLinkSettle(DateTime.UtcNow, _linkQuietUntilUtc))
            return false;
        bool changed = false;
        for (int i = 0; i < _paneCount && i < zones.Length; i++)
        {
            var w = _tiler.GetZone(i);
            if (w == null || w.Parked || !NativeMethods.IsWindow(w.Hwnd)) continue;
            if (zones[i].IsEmpty) continue;
            if (CropPreviewEnabled && (w.HasCrop || IsCropInteractionZone(i))) continue;

            if (w.Collapsed || w.Floating)
            {
                if (w.CollapsedManual || _inSizeMove) continue; // never auto-expand mid-drag
                ComputeCollapseNeed(w, i, out int nw, out int nh);
                nw = Math.Max(nw, w.CollapseNeedW);
                nh = Math.Max(nh, w.CollapseNeedH);
                if (ZoneMeets(zones[i], nw, nh, CollapseReexpandFactor))
                {
                    DockCollapsedWindow(w);
                    changed = true;
                }
                continue;
            }

            // Legacy Fit-mode chooser preview stays as-is; fill-mode previews are about to be
            // promoted to Live by ApplyLiveTiling, so they are evaluated like Live tiles.
            bool wantFill = string.Equals(FitModeForZone(i), "fill", StringComparison.OrdinalIgnoreCase);
            if (w.PreviewProxy && !wantFill) continue;

            // v1.0.2: never collapse during an active drag; the WM_EXITSIZEMOVE settle pass decides.
            if (_inSizeMove) continue;
            ComputeCollapseNeed(w, i, out int needW, out int needH);
            if (!ZoneMeets(zones[i], needW, needH, 1.0))
            {
                CollapseWindow(w, manual: false, needW, needH);
                changed = true;
            }
        }
        return changed;
    }

    // ---------------------------------------------------------------- state transitions

    private void CollapseWindow(WorkingWindow w, bool manual, int needW, int needH)
    {
        if (w == null) return;
        w.Collapsed = true;
        w.Floating = false;
        w.CollapsedManual = manual;
        w.CollapseNeedW = Math.Max(1, needW);
        w.CollapseNeedH = Math.Max(1, needH);
        w.PreviewProxy = true;
        w.ShowFitPrompt = false;
        _collapseSeenIconic.Remove(w.Hwnd);
        if (!NativeMethods.IsWindow(w.Hwnd)) return;
        ClearLiveTopmostTracked(w.Hwnd);
        try
        {
            // Minimizing the foreground window would hand activation to some random app (desk yield).
            if (NativeMethods.GetForegroundWindow() == w.Hwnd && _ownHwnd != IntPtr.Zero)
                NativeMethods.ForceForegroundWindow(_ownHwnd);
        }
        catch { }
        if (!NativeMethods.IsIconic(w.Hwnd))
            NativeMethods.ShowWindow(w.Hwnd, NativeMethods.SW_SHOWMINNOACTIVE);
        if (NativeMethods.IsIconic(w.Hwnd))
            _collapseSeenIconic.Add(w.Hwnd);
        int vn = VisualPaneNumber(w.ZoneIndex >= 0 ? w.ZoneIndex : 0);
        try { StatusText.Text = manual ? $"Pane {vn}: collapsed {w.ShortTitle}" : $"Pane {vn} too small - collapsed {w.ShortTitle}"; } catch { }
    }

    /// <summary>Back to Live in its pane. Restores without activation; ApplyLiveTiling places it.</summary>
    private void DockCollapsedWindow(WorkingWindow w)
    {
        bool wasCollapsed = w.Collapsed;
        w.Collapsed = false;
        w.Floating = false;
        w.CollapsedManual = false;
        _collapseSeenIconic.Remove(w.Hwnd);
        EnterLiveFill(w);
        if (wasCollapsed && NativeMethods.IsWindow(w.Hwnd) && NativeMethods.IsIconic(w.Hwnd))
            NativeMethods.ShowWindow(w.Hwnd, NativeMethods.SW_SHOWNOACTIVATE);
    }

    private NativeMethods.Rect[] ComputeZonesAsIfLive(WorkingWindow w)
    {
        bool prev = w.PreviewProxy;
        try
        {
            w.PreviewProxy = false; // let HonorAssignedMinTracks reserve its min-track width
            return ComputePaneScreenZones();
        }
        finally
        {
            w.PreviewProxy = prev;
        }
    }

    /// <summary>Card Expand / click / taskbar restore.</summary>
    private void ExpandCollapsedPane(int zone)
    {
        if (zone < 0 || zone >= _paneCount) return;
        var w = _tiler.GetZone(zone);
        if (w == null || w.Parked || !NativeMethods.IsWindow(w.Hwnd)) return;
        if (!w.Collapsed && !w.Floating) return;

        try { UpdateLayout(); PaneGrid?.UpdateLayout(); } catch { }
        var zones = ComputeZonesAsIfLive(w);
        ComputeCollapseNeed(w, zone, out int nw, out int nh);
        bool fits = zone < zones.Length && !zones[zone].IsEmpty && ZoneMeets(zones[zone], nw, nh, 1.0);
        _paneClickQuietUntilUtc = DateTime.UtcNow.AddMilliseconds(900);
        if (fits)
        {
            DockCollapsedWindow(w);
            ApplyLiveTiling(activateZone: zone);
            try { StatusText.Text = $"Expanded {w.ShortTitle} into pane {VisualPaneNumber(zone)}"; } catch { }
            return;
        }
        if (w.Floating)
        {
            // Already floating: just bring it forward.
            PinLiveTopmost(w.Hwnd, force: true);
            NativeMethods.FocusWindow(w.Hwnd);
            return;
        }
        FloatCollapsedWindow(w, nw, nh);
        RefreshAllThumbnails();
        HighlightActivePane();
    }

    /// <summary>Pane still too small: float centered over the desk (Live TOPMOST band), still linked.</summary>
    private void FloatCollapsedWindow(WorkingWindow w, int needW, int needH)
    {
        w.Collapsed = false;
        w.Floating = true;
        w.CollapsedManual = false;
        w.PreviewProxy = true;
        w.ShowFitPrompt = false;
        _collapseSeenIconic.Remove(w.Hwnd);

        NativeMethods.Rect desk;
        if (!TryGetDeskClientScreenRect(out desk) || desk.IsEmpty)
        {
            if (!NativeMethods.GetWindowRect(_ownHwnd, out desk)) return;
        }
        NativeMethods.Rect work = desk;
        try
        {
            if (!NativeMethods.TryGetMonitorWorkArea(_ownHwnd, out work, out _))
                work = desk;
        }
        catch { work = desk; }

        int width = Math.Max(needW, (int)(desk.Width * 0.6));
        int height = Math.Max(needH, (int)(desk.Height * 0.6));
        width = Math.Min(width, Math.Max(200, work.Width));
        height = Math.Min(height, Math.Max(150, work.Height));
        int cx = desk.Left + desk.Width / 2;
        int cy = desk.Top + desk.Height / 2;
        int left = Math.Clamp(cx - width / 2, work.Left, Math.Max(work.Left, work.Right - width));
        int top = Math.Clamp(cy - height / 2, work.Top, Math.Max(work.Top, work.Bottom - height));
        var rect = new NativeMethods.Rect { Left = left, Top = top, Right = left + width, Bottom = top + height };

        if (NativeMethods.IsIconic(w.Hwnd))
            NativeMethods.ShowWindow(w.Hwnd, NativeMethods.SW_SHOWNOACTIVATE);
        NativeMethods.TileWindow(w.Hwnd, rect, activate: false, "fill");
        PinLiveTopmost(w.Hwnd, force: true);
        NativeMethods.FocusWindow(w.Hwnd);
        try { StatusText.Text = $"Pane {VisualPaneNumber(w.ZoneIndex)} still too small - {w.ShortTitle} floating over the desk"; } catch { }
    }

    /// <summary>Manual collapse (pane menu / chip menu / Floating card "Collapse").</summary>
    private void CollapsePaneManually(int zone, bool manual = true)
    {
        if (zone < 0 || zone >= _paneCount) return;
        var w = _tiler.GetZone(zone);
        if (w == null || w.Parked || !NativeMethods.IsWindow(w.Hwnd)) return;
        if (w.Collapsed) { w.CollapsedManual = manual; return; }
        ComputeCollapseNeed(w, zone, out int nw, out int nh);
        CollapseWindow(w, manual, nw, nh);
        ApplyLiveTiling();
        HighlightActivePane();
    }

    /// <summary>
    /// SoftRefresh hook (800 ms): user restored a collapsed window from the taskbar -> treat as Expand;
    /// user minimized a floating window -> back to the collapsed card. Idempotent, one action per tick.
    /// </summary>
    private void ReconcileCollapsedWindows()
    {
        if (!_liveDesk || _inSizeMove) return;
        if (WindowState == WindowState.Minimized || _deskMinimized) return;
        for (int i = 0; i < _paneCount; i++)
        {
            var w = _tiler.GetZone(i);
            if (w == null || w.Parked || !NativeMethods.IsWindow(w.Hwnd)) continue;
            if (w.Collapsed)
            {
                if (NativeMethods.IsIconic(w.Hwnd)) { _collapseSeenIconic.Add(w.Hwnd); continue; }
                // Only after we've seen it minimized (apps that refuse SW_MINIMIZE must not loop).
                if (!_collapseSeenIconic.Contains(w.Hwnd)) continue;
                _collapseSeenIconic.Remove(w.Hwnd);
                ExpandCollapsedPane(i);
                return;
            }
            if (w.Floating && NativeMethods.IsIconic(w.Hwnd))
            {
                ComputeCollapseNeed(w, i, out int nw, out int nh);
                w.Collapsed = true;
                w.Floating = false;
                w.CollapsedManual = false;
                w.CollapseNeedW = Math.Max(nw, w.CollapseNeedW);
                w.CollapseNeedH = Math.Max(nh, w.CollapseNeedH);
                w.PreviewProxy = true;
                ClearLiveTopmostTracked(w.Hwnd);
                _collapseSeenIconic.Add(w.Hwnd);
                RefreshAllThumbnails();
                HighlightActivePane();
                return;
            }
        }
    }

    // ---------------------------------------------------------------- card UI

    private CollapseCard EnsureCollapseCard(int i)
    {
        var existing = _collapseCards[i];
        if (existing != null) return existing;
        var slot = _slots[i];

        var thumb = new ThumbnailHost { Width = 176, Height = 99, MinWidth = 40, MinHeight = 40 };
        var icon = new Image
        {
            Width = 32, Height = 32, Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        };
        var well = new Border
        {
            Width = 176, Height = 99, CornerRadius = new CornerRadius(4),
            Margin = new Thickness(0, 0, 0, 8), BorderThickness = new Thickness(1),
            Child = new Grid { Children = { icon, thumb } }
        };
        well.SetResourceReference(Border.BackgroundProperty, "BgBrush");
        well.SetResourceReference(Border.BorderBrushProperty, "BorderBrushQuiet");

        var title = new TextBlock
        {
            FontSize = 12, FontWeight = FontWeights.Bold, TextTrimming = TextTrimming.CharacterEllipsis,
            TextAlignment = TextAlignment.Center, MaxWidth = 176, HorizontalAlignment = HorizontalAlignment.Center
        };
        title.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        var status = new TextBlock
        {
            FontSize = 10, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap,
            MaxWidth = 176, Margin = new Thickness(0, 2, 0, 8), HorizontalAlignment = HorizontalAlignment.Center
        };
        status.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");

        var primary = new Button { Content = "Expand", Tag = i, FontSize = 11, Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 6, 0) };
        primary.Click += CollapseCardPrimary_Click;
        var secondary = new Button { Content = "Collapse", Tag = i, FontSize = 11, Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0), Visibility = Visibility.Collapsed };
        secondary.Click += CollapseCardSecondary_Click;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Children = { primary, secondary } };
        // v1.0.2: same "Expand dashboard to fit" as the PreviewProxy fit prompt (themed keycap, own row).
        var growDesk = new Button { Content = "Expand dashboard to fit", Tag = i, FontSize = 11, Padding = new Thickness(10, 4, 10, 4),
            Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Stretch,
            ToolTip = "Grow the dashboard window just enough for this app's minimum size (stays on this monitor)" };
        growDesk.Click += CollapseCardGrowDesk_Click;

        var card = new Border
        {
            CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Padding = new Thickness(12, 12, 12, 10),
            Cursor = Cursors.Hand, SnapsToDevicePixels = true,
            Child = new StackPanel { Children = { well, title, status, buttons, growDesk } },
            ToolTip = "Click to expand"
        };
        card.SetResourceReference(Border.BackgroundProperty, "PanelAltBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "BorderBrushQuiet");

        // Accent glow on hover lives on an empty sibling layer (text stays crisp, no effect on the card).
        var glow = new Border { CornerRadius = new CornerRadius(8), IsHitTestVisible = false, Visibility = Visibility.Collapsed };
        glow.SetResourceReference(Border.BackgroundProperty, "AccentDimBrush");
        glow.SetResourceReference(UIElement.EffectProperty, "AccentGlowEffect");

        var cardGrid = new Grid { Children = { glow, card } };
        var viewbox = new Viewbox
        {
            Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly,
            Margin = new Thickness(8), Child = cardGrid,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        };
        var host = new Grid { Visibility = Visibility.Collapsed, Children = { viewbox } };
        Panel.SetZIndex(host, 150);

        card.MouseEnter += (_, _) =>
        {
            glow.Visibility = Visibility.Visible;
            card.SetResourceReference(Border.BorderBrushProperty, "AccentBrush");
        };
        card.MouseLeave += (_, _) =>
        {
            glow.Visibility = Visibility.Collapsed;
            card.SetResourceReference(Border.BorderBrushProperty, "BorderBrushQuiet");
        };
        card.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            var w = _tiler.GetZone(i);
            if (w == null) return;
            ExpandCollapsedPane(i);
        };
        // Keep the card thumbnail glued to the card while the desk resizes/moves.
        cardGrid.LayoutUpdated += (_, _) =>
        {
            if (host.Visibility == Visibility.Visible && thumb.HasThumbnail) thumb.RefreshLayout();
        };

        slot.ContentGrid.Children.Add(host);
        var cc = new CollapseCard
        {
            Host = host, Glow = glow, Card = card, Thumb = thumb, Icon = icon,
            Title = title, Status = status, PrimaryBtn = primary, SecondaryBtn = secondary, GrowDeskBtn = growDesk
        };
        _collapseCards[i] = cc;
        return cc;
    }

    private void HideCollapseCard(int i)
    {
        var cc = _collapseCards[i];
        if (cc == null) return;
        if (cc.Thumb.HasThumbnail) cc.Thumb.Clear();
        cc.Host.Visibility = Visibility.Collapsed;
    }

    /// <summary>RefreshAllThumbnails branch for Collapsed / Floating panes. Returns true when it owned the pane.</summary>
    private bool RefreshCollapsedPaneUi(int i, WorkingWindow assigned)
    {
        if (!_liveDesk || (!assigned.Collapsed && !assigned.Floating))
        {
            HideCollapseCard(i);
            return false;
        }
        var slot = _slots[i];
        var cc = EnsureCollapseCard(i);

        // Pane itself: same quiet gradient as an empty pane (brush only; host margins/borders untouched).
        slot.Thumb.Clear();
        slot.Placeholder.Visibility = Visibility.Collapsed;
        slot.Hint.Visibility = Visibility.Collapsed;
        ShowFitPromptUi(i, false);
        if (slot.ContentGrid != null)
            slot.ContentGrid.SetResourceReference(Panel.BackgroundProperty, "PaneGradientBrush");

        cc.Icon.Source = assigned.Icon;
        cc.Title.Text = assigned.ShortTitle;
        cc.Title.ToolTip = assigned.Title;
        if (assigned.Floating)
        {
            cc.Status.Text = "Floating over the desk - pane too small";
            cc.PrimaryBtn.Content = "Show";
            cc.SecondaryBtn.Visibility = Visibility.Visible;
            cc.Card.ToolTip = "Click to bring the floating window forward";
            cc.GrowDeskBtn.Visibility = Visibility.Visible;
        }
        else
        {
            cc.Status.Text = assigned.CollapsedManual ? "Collapsed" : "Collapsed - pane too small";
            cc.PrimaryBtn.Content = "Expand";
            cc.SecondaryBtn.Visibility = Visibility.Collapsed;
            cc.Card.ToolTip = "Click to expand";
            // Manual collapse is not a size problem - only offer growing the desk when it is too small.
            cc.GrowDeskBtn.Visibility = assigned.CollapsedManual ? Visibility.Collapsed : Visibility.Visible;
        }
        cc.Host.Visibility = Visibility.Visible;

        // Live DWM thumbnail of the (minimized) window; icon shows through if DWM has nothing.
        if (cc.Thumb.SourceHwnd != assigned.Hwnd)
            cc.Thumb.Assign(assigned.Hwnd);
        else
            cc.Thumb.RefreshLayout();
        cc.Icon.Visibility = cc.Thumb.HasThumbnail ? Visibility.Collapsed : Visibility.Visible;
        return true;
    }

    private void CollapseCardPrimary_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement fe && fe.Tag is int zone)
            ExpandCollapsedPane(zone);
    }

    private void CollapseCardSecondary_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement fe && fe.Tag is int zone)
            CollapsePaneManually(zone, manual: false);
    }

    // ---------------------------------------------------------------- menus

    private MenuItem BuildCollapseMenuItem(int zone)
    {
        var mi = new MenuItem { Header = "Collapse window", Tag = zone };
        mi.Click += (_, e) =>
        {
            e.Handled = true;
            ToggleCollapseFromMenu(zone);
        };
        return mi;
    }

    private void ToggleCollapseFromMenu(int zone)
    {
        var w = zone >= 0 && zone < _paneCount ? _tiler.GetZone(zone) : null;
        if (w == null) return;
        if (w.Collapsed) ExpandCollapsedPane(zone);
        else CollapsePaneManually(zone, manual: true);
    }

    /// <summary>Label + enable the Collapse/Expand item for the pane's current state.</summary>
    private void SyncCollapseMenuItem(MenuItem mi, int zone)
    {
        var w = zone >= 0 && zone < _paneCount ? _tiler.GetZone(zone) : null;
        bool assigned = _liveDesk && w != null && !w.Parked && NativeMethods.IsWindow(w.Hwnd);
        mi.IsEnabled = assigned;
        mi.Header = assigned && w!.Collapsed ? "Expand window" : "Collapse window";
    }

    /// <summary>Right-click menu on the sidebar pane chips (Live panes are covered by the real window).</summary>
    private void AttachPaneChipCollapseMenu(Button chip, int visualNumber)
    {
        var menu = new ContextMenu();
        var item = new MenuItem { Header = "Collapse window" };
        item.Click += (_, e) =>
        {
            e.Handled = true;
            ToggleCollapseFromMenu(ZoneIndexFromVisual(visualNumber));
        };
        menu.Items.Add(item);
        menu.Opened += (_, _) =>
        {
            _chromePopupQuietUntilUtc = DateTime.UtcNow.AddSeconds(2);
            SyncCollapseMenuItem(item, ZoneIndexFromVisual(visualNumber));
        };
        chip.ContextMenu = menu;
    }
}
