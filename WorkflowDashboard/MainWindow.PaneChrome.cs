using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace WorkflowDashboard;

/// <summary>
/// Topmost chrome above Live HWNDs: per-pane ... / Menu button, pane menu popup, helpers to
/// suspend/restore Live TOPMOST while menu/crop chrome is interactive.
/// </summary>
public partial class MainWindow
{
    private readonly Window?[] _paneChromeWin = new Window?[4];
    private Window? _paneMenuWindow;
    private int _menuSuspendedZone = -1;
    private DateTime _linkQuietUntilUtc = DateTime.MinValue;
    /// <summary>Suppress ClearAllLiveTopmost during pane-click Focus handoff.</summary>
    private DateTime _paneClickQuietUntilUtc = DateTime.MinValue;
    /// <summary>While picking/dragging a window to link (hover phase before Assign lands).</summary>
    private DateTime _linkInteractionQuietUntilUtc = DateTime.MinValue;
    private bool _linkToggleArmed;
    private int _linkOutlineZone = -1;
    private DateTime _linkOutlineShownUtc = DateTime.MinValue;
    private double _linkOutlineHoldMs = LinkTargetIndicator.HoldMs;
    private DispatcherTimer? _linkOutlineTimer;

    /// <summary>Crop drag OR open pane menu — skip Live TOPMOST repair for that zone.</summary>
    private bool IsCropInteractionZone(int zone)
        => zone >= 0 && (_cropModeZone == zone || _menuSuspendedZone == zone);

    private void SuspendPaneLiveTopmost(int zone)
    {
        var w = _tiler.GetZone(zone);
        if (w != null && NativeMethods.IsWindow(w.Hwnd))
            NativeMethods.ClearLiveTopmost(w.Hwnd);
    }

    private void RestorePaneLiveAfterChrome(int zone)
    {
        if (!_liveDesk) return;
        if (zone < 0) return;
        // One-shot repair for all panes so siblings stay visible too.
        RepairLiveVisibilityAndZOrder(keepFocusHwnd: IntPtr.Zero);
    }

    /// <summary>DIP screen rect of a pane content grid (for Topmost tool windows).</summary>
    private bool TryGetPaneDipScreenRect(int zone, out Rect dip)
    {
        dip = default;
        if (zone < 0 || zone >= _slots.Length) return false;
        var host = _slots[zone].ContentGrid ?? (FrameworkElement)_slots[zone].Root;
        if (host == null || !host.IsVisible || host.ActualWidth < 2 || host.ActualHeight < 2)
            return false;
        try
        {
            var tl = host.PointToScreen(new Point(0, 0));
            var br = host.PointToScreen(new Point(host.ActualWidth, host.ActualHeight));
            var src = PresentationSource.FromVisual(this);
            var t = src?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
            var tlDip = t.Transform(tl);
            var brDip = t.Transform(br);
            dip = new Rect(tlDip, brDip);
            return dip.Width >= 2 && dip.Height >= 2;
        }
        catch
        {
            return false;
        }
    }

    private static void MarkToolWindow(Window w)
    {
        w.SourceInitialized += (_, _) =>
        {
            try
            {
                var hwnd = new WindowInteropHelper(w).Handle;
                if (hwnd == IntPtr.Zero) return;
                long ex = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
                ex |= NativeMethods.WS_EX_TOOLWINDOW;
                ex &= ~NativeMethods.WS_EX_APPWINDOW;
                NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(ex));
                NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
                    NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
            }
            catch { }
        };
    }

    private static void RaiseWindowTopmost(Window w)
    {
        try
        {
            w.Topmost = true;
            var hwnd = new WindowInteropHelper(w).Handle;
            if (hwnd != IntPtr.Zero)
            {
                NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
                    NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
            }
        }
        catch { }
    }

    /// <summary>
    /// Keep a small Topmost ... / Uncrop chrome above Live-Fill and cropped tiles so pane menu is reachable
    /// even when the linked HWND covers in-pane WPF chrome.
    /// </summary>
    /// <summary>
    /// HARD SIMPLIFY: no floating Topmost ... / Uncrop over Live HWND.
    /// Crop/Uncrop live in the side panel only. Always hide legacy chrome windows.
    /// </summary>
    private void SyncLivePaneChromeOverlays()
    {
        HideAllPaneChromeOverlays();
        ClosePaneMenuWindow();
    }
    private void HideAllPaneChromeOverlays()
    {
        for (int i = 0; i < _paneChromeWin.Length; i++)
        {
            try { _paneChromeWin[i]?.Hide(); } catch { }
        }
    }

    private void CloseAllPaneChromeOverlays()
    {
        ClosePaneMenuWindow();
        for (int i = 0; i < _paneChromeWin.Length; i++)
        {
            try { _paneChromeWin[i]?.Close(); } catch { }
            _paneChromeWin[i] = null;
        }
    }

    /// <summary>No Topmost pane menus. Crop/Uncrop are in the side panel.</summary>
    private void OpenPaneMenuWindow(int zone)
    {
        ClosePaneMenuWindow();
        HideAllPaneChromeOverlays();
        if (zone >= 0)
        {
            _activePane = zone + 1;
            HighlightActivePane();
            SyncSideCropButtons();
        }
        try { StatusText.Text = "Use side panel Crop / Uncrop (no floating menu)"; } catch { }
    }
    private void ClosePaneMenuWindow()
    {
        var win = _paneMenuWindow;
        _paneMenuWindow = null;
        if (win == null) return;
        try { win.Close(); } catch { }
    }

    /// <summary>Layout star shares for assign-target chips (mirrors ApplyPaneLayout).</summary>
    private double[] GetLayoutChipShares()
    {
        int n = Math.Max(1, _paneCount);
        var shares = new double[n];
        if (n == 1)
        {
            shares[0] = 1;
            return shares;
        }
        if (n == 2)
        {
            switch (_layout)
            {
                case "main":
                    shares[0] = _mainOnLeft ? 0.70 : 0.30;
                    shares[1] = _mainOnLeft ? 0.30 : 0.70;
                    break;
                case "large":
                    shares[0] = _mainOnLeft ? 0.80 : 0.20;
                    shares[1] = _mainOnLeft ? 0.20 : 0.80;
                    break;
                default:
                    shares[0] = 0.50;
                    shares[1] = 0.50;
                    break;
            }
            return shares;
        }
        // n == 3
        if (_layout == "large")
        {
            // Center-stage, slightly bigger middle than Main (15|70|15).
            shares[0] = 0.15;
            shares[1] = 0.70;
            shares[2] = 0.15;
        }
        else if (_layout == "main")
        {
            shares[0] = 0.22;
            shares[1] = 0.56;
            shares[2] = 0.22;
        }
        else
        {
            shares[0] = shares[1] = shares[2] = 1.0;
        }
        return shares;
    }

    // ---- Full-screen "Show UI" Topmost overlay (above Live tiles) ----

    private void EnsureShowUiOverlay()
    {
        if (_showUiOverlay != null) return;
        var btn = new Button
        {
            Content = "Show UI",
            Padding = new Thickness(12, 6, 12, 6),
            FontSize = 12,
            FontWeight = FontWeights.Bold,
            Cursor = Cursors.Hand,
            BorderThickness = new Thickness(1),
            ToolTip = "Exit full screen — restore header + side panel (Esc / Ctrl+Shift+D)"
        };
        btn.SetResourceReference(Control.BackgroundProperty, "AccentDimBrush");
        btn.SetResourceReference(Control.ForegroundProperty, "TextBrush");
        btn.SetResourceReference(Control.BorderBrushProperty, "AccentBrush");
        btn.Click += (_, _) =>
        {
            try { SetDistractionFree(false); }
            catch { }
        };

        var win = new Window
        {
            Title = "WorkflowDashboard.ShowUi",
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
            ResizeMode = ResizeMode.NoResize,
            SizeToContent = SizeToContent.WidthAndHeight,
            Topmost = true,
            ShowActivated = false,
            Content = btn,
        };
        MarkToolWindow(win);
        win.Closed += (_, _) =>
        {
            if (ReferenceEquals(_showUiOverlay, win))
                _showUiOverlay = null;
        };
        _showUiOverlay = win;
    }

    private void HideShowUiOverlay()
    {
        try { _showUiOverlay?.Hide(); } catch { }
    }

    private void CloseShowUiOverlay()
    {
        var w = _showUiOverlay;
        _showUiOverlay = null;
        if (w == null) return;
        try { w.Close(); } catch { }
    }

    /// <summary>
    /// Keep the full-screen exit chip above Live HWND_TOPMOST tiles, pinned to the desk's top-right.
    /// </summary>
    private void SyncShowUiOverlay()
    {
        try
        {
            if (!_distractionFree || !IsVisible || WindowState == WindowState.Minimized)
            {
                HideShowUiOverlay();
                return;
            }

            EnsureShowUiOverlay();
            var win = _showUiOverlay;
            if (win == null) return;

            // Measure desired size
            win.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double bw = Math.Max(72, win.DesiredSize.Width);
            double bh = Math.Max(28, win.DesiredSize.Height);

            // Top-right of the Dashboard window, in DIP screen coords
            var src = PresentationSource.FromVisual(this);
            var fromDevice = src?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
            // Frameless: client (0,0) is now the window corner, so sit below the custom title bar
            // (and inside the maximized overhang pad) instead of covering min/max/close.
            Point screenPx = PointToScreen(new Point(ActualWidth - 10 - _maxOverhangPad.Right, 8 + TitleBarBottomOffsetDip()));
            Point topRightDip = fromDevice.Transform(screenPx);
            win.Left = topRightDip.X - bw;
            win.Top = topRightDip.Y;
            if (win.Left < 0) win.Left = 0;
            if (win.Top < 0) win.Top = 0;

            if (!win.IsVisible) win.Show();
            RaiseWindowTopmost(win);
            // Second raise: Live tiles re-pin TOPMOST often bury this chip.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try { if (_distractionFree && win != null) RaiseWindowTopmost(win); } catch { }
            }), DispatcherPriority.Loaded);
        }
        catch
        {
            // never break desk for overlay chrome
        }
    }


    /// <summary>
    /// Accent outline around the pane chosen as the link/assign target.
    /// Lives on the WPF pane border (outside the tiled HWND), never activates
    /// another window, and fades away after a few seconds or when a link lands.
    /// </summary>
    private void ShowLinkTargetOutline(int zone)
    {
        if (zone < 0 || zone >= _paneCount) return;
        _linkOutlineZone = zone;
        _linkOutlineShownUtc = DateTime.UtcNow;
        _linkOutlineHoldMs = LinkTargetIndicator.HoldMs;
        EnsureLinkOutlineTimer();
        ApplyLinkTargetOutlineVisual();
    }

    private void BeginLinkTargetOutlineFade()
    {
        if (_linkOutlineZone < 0) return;
        _linkOutlineShownUtc = DateTime.UtcNow;
        _linkOutlineHoldMs = 0;
        EnsureLinkOutlineTimer();
        ApplyLinkTargetOutlineVisual();
    }

    private void EnsureLinkOutlineTimer()
    {
        if (_linkOutlineTimer != null)
        {
            if (!_linkOutlineTimer.IsEnabled)
                _linkOutlineTimer.Start();
            return;
        }
        _linkOutlineTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        _linkOutlineTimer.Tick += (_, _) =>
        {
            try { ApplyLinkTargetOutlineVisual(); }
            catch { }
        };
        _linkOutlineTimer.Start();
    }

    private void ApplyLinkTargetOutlineVisual()
    {
        double envelope = 0;
        int zone = _linkOutlineZone;
        if (zone >= 0 && zone < _slots.Length && zone < _paneCount)
        {
            double elapsed = (DateTime.UtcNow - _linkOutlineShownUtc).TotalMilliseconds;
            envelope = LinkTargetIndicator.OpacityAt(elapsed, _linkOutlineHoldMs, LinkTargetIndicator.FadeMs);
        }
        double draw = envelope * LinkTargetIndicator.PeakOpacity;
        bool show = LinkTargetIndicator.IsVisible(envelope);

        for (int i = 0; i < _slots.Length && i < 4; i++)
        {
            var slot = _slots[i];
            var outline = slot?.LinkOutline;
            if (outline == null) continue;
            if (i == zone && show)
            {
                outline.CornerRadius = slot!.Root?.CornerRadius ?? new CornerRadius(0);
                outline.BorderThickness = new Thickness(LinkTargetIndicator.Thickness);
                outline.SetResourceReference(Border.BorderBrushProperty, "AccentBrush");
                outline.SetResourceReference(UIElement.EffectProperty, "AccentGlowEffect");
                outline.Opacity = draw;
                outline.Visibility = Visibility.Visible;
            }
            else
            {
                outline.Visibility = Visibility.Collapsed;
                outline.Opacity = 0;
            }
        }

        if (!show)
        {
            _linkOutlineZone = -1;
            if (_linkOutlineTimer != null)
            {
                _linkOutlineTimer.Stop();
                _linkOutlineTimer = null;
            }
        }
    }

}
