using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;

namespace WorkflowDashboard;

public partial class MainWindow
{
    private int _cropModeZone = -1;
    private bool _cropDragging;
    private Point _cropStartDip;
    private Rectangle? _cropRubber;
    private Window? _cropToolWindow;
    private Canvas? _cropToolCanvas;

    private static bool IsPreviewProxy(WorkingWindow? w)
        => w != null && w.PreviewProxy;

    /// <summary>Cropped Live panes show a DWM thumb fill overlay (assignment kept).</summary>
    private static bool UsesCropThumbOverlay(WorkingWindow? w)
        => CropPreviewEnabled && w != null && w.HasCrop;

    private void EnterPreviewProxy(WorkingWindow w, bool showPrompt)
    {
        w.PreviewProxy = true;
        w.ShowFitPrompt = showPrompt;
        // Keep real window where it is; do not TOPMOST over the desk thumbnail.
        if (NativeMethods.IsWindow(w.Hwnd))
            NativeMethods.ClearLiveTopmost(w.Hwnd);
        if (CropPreviewEnabled)
            TryLoadPersistedCrop(w);
    }

    private void EnterLiveFill(WorkingWindow w)
    {
        w.PreviewProxy = false;
        w.ShowFitPrompt = false;
        if (!w.HasCrop) UnparkCroppedHwnd(w);
    }
    private void ParkCroppedHwnd(WorkingWindow w)
    {
        // STABILIZE: never cloak/hide HWNDs for crop this build.
        if (!CropPreviewEnabled) return;
        if (w == null || !NativeMethods.IsWindow(w.Hwnd)) return;
        NativeMethods.ParkForCropPreview(w.Hwnd);
    }

    private void UnparkCroppedHwnd(WorkingWindow w)
    {
        if (w == null || !NativeMethods.IsWindow(w.Hwnd)) return;
        NativeMethods.UnparkFromCropPreview(w.Hwnd);
    }

    private void EnsureCroppedHwndsParked(IntPtr keepUnparked = default)
    {
        if (!CropPreviewEnabled) return;
        var fg = NativeMethods.GetForegroundWindow();
        for (int i = 0; i < _paneCount; i++)
        {
            var w = _tiler.GetZone(i);
            if (w == null || w.Parked || !w.HasCrop) continue;
            if (!NativeMethods.IsWindow(w.Hwnd)) continue;
            // Leave alone while user is interacting after click-to-focus.
            if (w.Hwnd == fg || w.Hwnd == keepUnparked) continue;
            ParkCroppedHwnd(w);
        }
        if (_cropModeZone >= 0)
        {
            var w = _tiler.GetZone(_cropModeZone);
            if (w != null) ParkCroppedHwnd(w);
        }
    }

    private void TryLoadPersistedCrop(WorkingWindow w)
    {
        if (!CropPreviewEnabled) return;
        if (w.HasCrop) return;
        var key = SettingsStore.CropKey(w.ProcessName, w.Title);
        if (_settings.PreviewCrops != null
            && _settings.PreviewCrops.TryGetValue(key, out var dto)
            && dto != null)
        {
            var r = new NativeMethods.Rect
            {
                Left = dto.Left,
                Top = dto.Top,
                Right = dto.Right,
                Bottom = dto.Bottom
            };
            if (r.Width > 1 && r.Height > 1)
                w.CropRect = r;
        }
    }

    private void PersistCropFor(WorkingWindow w)
    {
        var key = SettingsStore.CropKey(w.ProcessName, w.Title);
        _settings.PreviewCrops ??= new Dictionary<string, CropRectDto>(StringComparer.OrdinalIgnoreCase);
        if (w.HasCrop)
        {
            var r = w.CropRect!.Value;
            _settings.PreviewCrops[key] = new CropRectDto
            {
                Left = r.Left,
                Top = r.Top,
                Right = r.Right,
                Bottom = r.Bottom
            };
        }
        else
        {
            _settings.PreviewCrops.Remove(key);
        }
        Persist();
    }

    private void ShowFitPromptUi(int zone, bool show)
    {
        if (zone < 0 || zone >= _slots.Length) return;
        var slot = _slots[zone];
        if (slot.FitPrompt != null)
            slot.FitPrompt.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateCropButtons(int zone)
    {
        if (zone < 0 || zone >= _slots.Length) return;
        var slot = _slots[zone];
        // Crop/Uncrop live in the SIDE PANEL only — never floating over Live HWND.
        if (slot.CropBtn != null)
        {
            slot.CropBtn.Visibility = Visibility.Collapsed;
            slot.CropBtn.IsHitTestVisible = false;
        }
        if (slot.UncropBtn != null)
        {
            slot.UncropBtn.Visibility = Visibility.Collapsed;
            slot.UncropBtn.IsHitTestVisible = false;
        }
        if (slot.MenuBtn != null)
        {
            slot.MenuBtn.Visibility = Visibility.Collapsed;
            slot.MenuBtn.IsHitTestVisible = false;
        }
        SyncSideCropButtons();
    }

    private void FitPrompt_ResizeDesk_Click(object sender, RoutedEventArgs e)
    {
        // Renamed UX: Change layout (equal->main->large), never auto-Expand.
        e.Handled = true;
        if (sender is not FrameworkElement fe || fe.Tag is not int zone) return;
        var w = _tiler.GetZone(zone);
        if (w == null) return;
        w.ShowFitPrompt = false;
        ShowFitPromptUi(zone, false);
        ChangeLayoutTowardLarger(zone, w);
    }

    private void FitPrompt_MoveMain_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not FrameworkElement fe || fe.Tag is not int zone) return;
        var w = _tiler.GetZone(zone);
        if (w == null) return;
        w.ShowFitPrompt = false;
        ShowFitPromptUi(zone, false);
        MoveAssignmentToLargestPane(zone, w);
    }

    private void FitPrompt_KeepPreview_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not FrameworkElement fe || fe.Tag is not int zone) return;
        var w = _tiler.GetZone(zone);
        if (w == null) return;
        w.ShowFitPrompt = false;
        ShowFitPromptUi(zone, false);
        EnterPreviewProxy(w, showPrompt: false);
        RefreshAllThumbnails();
        StatusText.Text = $"Preview kept for {w.ShortTitle} — Crop available; click thumb to Focus";
    }

    /// <summary>
    /// Step layout toward a larger primary pane: Equal→Main, Main→Large.
    /// Retile and promote to Live Fill when the pane now fits. Never Expand / Unassign.
    /// </summary>
    private void ChangeLayoutTowardLarger(int zone, WorkingWindow w)
    {
        string prior = _layout;
        if (_layout == "equal")
            _layout = "main";
        else if (_layout == "main")
            _layout = "large";
        // large stays large — still retile / remeasure

        if (_expandedMode)
            _expandedMode = false;

        SyncToggleUi();
        ApplyPaneLayout();
        Persist();

        try
        {
            UpdateLayout();
            PaneGrid?.UpdateLayout();
        }
        catch { }

        var zones = ComputePaneScreenZones();
        NativeMethods.GetWindowMinTrackSize(w.Hwnd, out int minW, out int minH);
        bool fits = zone < zones.Length && !zones[zone].IsEmpty
                    && NativeMethods.PaneFitsMin(zones[zone], minW, minH);

        if (fits)
        {
            EnterLiveFill(w);
            if (_liveDesk)
                ApplyLiveTiling(activateZone: zone);
            else
                RefreshAllThumbnails();
            StatusText.Text = prior == _layout
                ? $"Layout {_layout} — Live Fill {w.ShortTitle}"
                : $"Layout {prior}→{_layout} — Live Fill {w.ShortTitle}";
        }
        else
        {
            EnterPreviewProxy(w, showPrompt: false);
            RefreshAllThumbnails();
            StatusText.Text = prior == _layout
                ? $"Layout {_layout} still too small for {w.ShortTitle} (min {minW}x{minH}) — Preview"
                : $"Layout {prior}→{_layout} still too small (min {minW}x{minH}) — Preview";
        }
    }

    private void MoveAssignmentToLargestPane(int fromZone, WorkingWindow w)
    {
        var zones = ComputePaneScreenZones();
        if (zones.Length == 0)
        {
            StatusText.Text = "Move to main: no pane zones";
            return;
        }

        int best = 0;
        long bestArea = -1;
        for (int i = 0; i < zones.Length && i < _paneCount; i++)
        {
            if (zones[i].IsEmpty) continue;
            long area = (long)zones[i].Width * zones[i].Height;
            if (area > bestArea)
            {
                bestArea = area;
                best = i;
            }
        }

        NativeMethods.GetWindowMinTrackSize(w.Hwnd, out int minW, out int minH);

        if (best == fromZone)
        {
            bool fitsHere = NativeMethods.PaneFitsMin(zones[best], minW, minH);
            if (fitsHere)
            {
                EnterLiveFill(w);
                if (_liveDesk) ApplyLiveTiling(activateZone: best);
                StatusText.Text = $"Already on largest pane — Live Fill {w.ShortTitle}";
            }
            else
            {
                EnterPreviewProxy(w, showPrompt: false);
                RefreshAllThumbnails();
                StatusText.Text = $"Largest pane still too small for {w.ShortTitle} — Preview";
            }
            _activePane = best + 1;
            HighlightActivePane();
            return;
        }

        var entry = new WindowEntry
        {
            Hwnd = w.Hwnd,
            Title = w.Title,
            ProcessName = w.ProcessName,
            Icon = w.Icon,
            ProcessId = 0
        };
        bool hadCrop = w.HasCrop;
        var crop = w.CropRect;
        bool wasPreview = w.PreviewProxy;
        _tiler.Assign(entry, best, tile: false, activate: false, swapIfOccupied: true);
        var moved = _tiler.GetZone(best);
        if (moved == null)
        {
            StatusText.Text = "Move to main failed";
            return;
        }
        if (hadCrop) moved.CropRect = crop;
        if (wasPreview) moved.PreviewProxy = true;

        zones = ComputePaneScreenZones();
        bool fits = best < zones.Length && !zones[best].IsEmpty
                    && NativeMethods.PaneFitsMin(zones[best], minW, minH);
        if (fits)
        {
            EnterLiveFill(moved);
            if (_liveDesk) ApplyLiveTiling(activateZone: best);
            StatusText.Text = $"Moved to main pane — Live Fill {moved.ShortTitle}";
        }
        else
        {
            EnterPreviewProxy(moved, showPrompt: false);
            RefreshAllThumbnails();
            StatusText.Text = $"Moved to main pane — still too small; Preview for {moved.ShortTitle}";
        }

        _activePane = best + 1;
        HighlightActivePane();
        SyncLinkedFlagsInList();
    }

    private void PaneCropBtn_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not FrameworkElement fe || fe.Tag is not int zone) return;
        BeginCropMode(zone);
    }

    private void PaneUncropBtn_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not FrameworkElement fe || fe.Tag is not int zone) return;
        UncropPane(zone);
    }

    private void BeginCropMode(int zone)
    {
        if (!CropPreviewEnabled)
        {
            try { StatusText.Text = "Crop temporarily disabled — Live Fill only"; } catch { }
            return;
        }
        var w = _tiler.GetZone(zone);
        if (w == null || w.Parked) return;

        ClosePaneMenuWindow();
        EndCropMode(apply: false);
        _cropModeZone = zone;
        var slot = _slots[zone];

        // Architecture: park real HWND; crop UI is in-pane WPF over DWM thumb (no TOPMOST tool window).
        ParkCroppedHwnd(w);
        HideAllPaneChromeOverlays();

        if (slot.ContentGrid != null)
            slot.ContentGrid.Background = Brushes.Black;
        slot.Root.Background = new SolidColorBrush(Color.FromRgb(0x16, 0x19, 0x22));
        slot.Thumb.FillHost = w.HasCrop;
        slot.Thumb.SourceCrop = w.HasCrop ? w.CropRect : null;
        if (slot.Thumb.SourceHwnd != w.Hwnd)
            slot.Thumb.Assign(w.Hwnd);
        else
            slot.Thumb.RefreshLayout();
        slot.Placeholder.Visibility = Visibility.Collapsed;
        slot.Hint.Visibility = Visibility.Visible;
        slot.Hint.Text = "Drag to select crop region. Esc cancels.";
        if (slot.MenuBtn != null)
            slot.MenuBtn.Visibility = Visibility.Collapsed;
        if (slot.UncropBtn != null)
            slot.UncropBtn.Visibility = Visibility.Collapsed;

        if (slot.CropOverlay == null)
        {
            EndCropMode(apply: false);
            StatusText.Text = "Crop failed — no overlay";
            return;
        }

        slot.CropOverlay.Children.Clear();
        // High-contrast drag rect: yellow outer + red inner stroke, dim mask — must be clearly visible.
        _cropRubber = new Rectangle
        {
            Stroke = Brushes.Yellow,
            StrokeThickness = 3,
            Fill = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xD7, 0x00)),
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false
        };
        var cropRubberInner = new Rectangle
        {
            Stroke = Brushes.Red,
            StrokeThickness = 2,
            Fill = Brushes.Transparent,
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
            Tag = "inner"
        };
        slot.CropOverlay.Children.Add(_cropRubber);
        slot.CropOverlay.Children.Add(cropRubberInner);
        slot.CropOverlay.Background = new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0));
        slot.CropOverlay.Visibility = Visibility.Visible;
        slot.CropOverlay.IsHitTestVisible = true;
        Panel.SetZIndex(slot.CropOverlay, 200);
        _cropToolCanvas = slot.CropOverlay;
        _cropToolWindow = null;

        try { slot.CropOverlay.Focus(); } catch { }
        StatusText.Text = "Drag bright yellow/red box to crop. Esc cancels. (HWND parked)";
        SyncLivePaneChromeOverlays();
    }

    private void EndCropMode(bool apply)
    {
        int zone = _cropModeZone;
        try { _cropToolCanvas?.ReleaseMouseCapture(); } catch { }

        // Legacy topmost crop tool window (if any) — close; preferred path is in-pane overlay.
        var tool = _cropToolWindow;
        _cropToolWindow = null;
        if (tool != null)
        {
            try { tool.Close(); } catch { }
        }
        _cropToolCanvas = null;

        if (zone >= 0 && zone < _slots.Length)
        {
            var slot = _slots[zone];
            if (slot.CropOverlay != null)
            {
                try { slot.CropOverlay.ReleaseMouseCapture(); } catch { }
                slot.CropOverlay.Children.Clear();
                slot.CropOverlay.Visibility = Visibility.Collapsed;
                slot.CropOverlay.IsHitTestVisible = false;
                slot.CropOverlay.Background = new SolidColorBrush(Color.FromArgb(0x01, 0, 0, 0));
            }
            if (slot.MenuBtn != null)
            {
                slot.MenuBtn.Visibility = Visibility.Collapsed;
                slot.MenuBtn.IsHitTestVisible = false;
            }
        }
        _cropDragging = false;
        _cropRubber = null;
        _cropModeZone = -1;

        // Keep cropped HWND parked; only Live Fill panes get normal z-order raise.
        if (zone >= 0)
        {
            var w = _tiler.GetZone(zone);
            if (w != null && w.HasCrop)
                ParkCroppedHwnd(w);
            else if (zone >= 0)
                RestorePaneLiveAfterChrome(zone);
        }
        SyncLivePaneChromeOverlays();
    }

    private void CropOverlay_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_cropModeZone < 0) return;
        if (sender is not Canvas canvas) return;
        _cropDragging = true;
        _cropStartDip = e.GetPosition(canvas);
        if (_cropRubber != null)
        {
            _cropRubber.Visibility = Visibility.Visible;
            Canvas.SetLeft(_cropRubber, _cropStartDip.X);
            Canvas.SetTop(_cropRubber, _cropStartDip.Y);
            _cropRubber.Width = 1;
            _cropRubber.Height = 1;
        }
        if (sender is Canvas cDown)
        {
            foreach (UIElement child in cDown.Children)
            {
                if (child is Rectangle ir && Equals(ir.Tag, "inner"))
                {
                    ir.Visibility = Visibility.Visible;
                    Canvas.SetLeft(ir, _cropStartDip.X);
                    Canvas.SetTop(ir, _cropStartDip.Y);
                    ir.Width = 1;
                    ir.Height = 1;
                }
            }
        }
        if (!canvas.IsMouseCaptured)
            canvas.CaptureMouse();
        e.Handled = true;
    }

    private void CropOverlay_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_cropDragging || _cropRubber == null || sender is not Canvas canvas) return;
        var p = e.GetPosition(canvas);
        double x = Math.Min(_cropStartDip.X, p.X);
        double y = Math.Min(_cropStartDip.Y, p.Y);
        double w = Math.Abs(p.X - _cropStartDip.X);
        double h = Math.Abs(p.Y - _cropStartDip.Y);
        Canvas.SetLeft(_cropRubber, x);
        Canvas.SetTop(_cropRubber, y);
        _cropRubber.Width = Math.Max(1, w);
        _cropRubber.Height = Math.Max(1, h);
        foreach (UIElement child in canvas.Children)
        {
            if (child is Rectangle ir && Equals(ir.Tag, "inner"))
            {
                ir.Visibility = Visibility.Visible;
                Canvas.SetLeft(ir, x + 2);
                Canvas.SetTop(ir, y + 2);
                ir.Width = Math.Max(1, w - 4);
                ir.Height = Math.Max(1, h - 4);
            }
        }
        e.Handled = true;
    }

    private void CropOverlay_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_cropDragging || sender is not Canvas canvas) return;
        canvas.ReleaseMouseCapture();
        _cropDragging = false;
        e.Handled = true;

        int zone = _cropModeZone;
        if (zone < 0 || _cropRubber == null)
        {
            EndCropMode(apply: false);
            return;
        }

        double left = Canvas.GetLeft(_cropRubber);
        double top = Canvas.GetTop(_cropRubber);
        double rw = _cropRubber.Width;
        double rh = _cropRubber.Height;
        if (rw < 8 || rh < 8)
        {
            EndCropMode(apply: false);
            RefreshAllThumbnails();
            StatusText.Text = "Crop cancelled — drag a larger rectangle";
            return;
        }

        var slot = _slots[zone];
        var w = _tiler.GetZone(zone);
        if (w == null || !slot.Thumb.TryGetSourceSize(out int srcW, out int srcH))
        {
            EndCropMode(apply: false);
            RefreshAllThumbnails();
            StatusText.Text = "Crop failed — no thumbnail source size";
            return;
        }

        // Map rubber-band from topmost overlay (same size as pane content) into source crop.
        double hostW = Math.Max(1, canvas.ActualWidth > 1 ? canvas.ActualWidth : slot.ContentGrid.ActualWidth);
        double hostH = Math.Max(1, canvas.ActualHeight > 1 ? canvas.ActualHeight : slot.ContentGrid.ActualHeight);

        int useSrcW = srcW;
        int useSrcH = srcH;
        NativeMethods.Rect prior = default;
        bool hasPrior = w.HasCrop;
        if (hasPrior)
        {
            prior = w.CropRect!.Value;
            useSrcW = prior.Width;
            useSrcH = prior.Height;
        }

        double scale = Math.Min(hostW / useSrcW, hostH / useSrcH);
        if (hasPrior || slot.Thumb.FillHost)
            scale = Math.Max(hostW / useSrcW, hostH / useSrcH);

        double drawW = useSrcW * scale;
        double drawH = useSrcH * scale;
        double ox = (hostW - drawW) / 2.0;
        double oy = (hostH - drawH) / 2.0;

        double relL = (left - ox) / scale;
        double relT = (top - oy) / scale;
        double relR = (left + rw - ox) / scale;
        double relB = (top + rh - oy) / scale;

        int baseL = hasPrior ? prior.Left : 0;
        int baseT = hasPrior ? prior.Top : 0;

        int srcL = Math.Clamp(baseL + (int)Math.Floor(relL), 0, srcW - 2);
        int srcT = Math.Clamp(baseT + (int)Math.Floor(relT), 0, srcH - 2);
        int srcR = Math.Clamp(baseL + (int)Math.Ceiling(relR), srcL + 2, srcW);
        int srcB = Math.Clamp(baseT + (int)Math.Ceiling(relB), srcT + 2, srcH);

        w.CropRect = new NativeMethods.Rect { Left = srcL, Top = srcT, Right = srcR, Bottom = srcB };
        PersistCropFor(w);
        EndCropMode(apply: true);
        // Crop applied: park HWND, show in-pane DWM crop thumb + WPF Uncrop/... (same window).
        ParkCroppedHwnd(w);
        if (slot.CropOverlay != null)
        {
            slot.CropOverlay.Children.Clear();
            slot.CropOverlay.Visibility = Visibility.Collapsed;
            slot.CropOverlay.IsHitTestVisible = false;
        }
        RefreshAllThumbnails();
        UpdateCropButtons(zone);
        SyncLivePaneChromeOverlays();
        StatusText.Text = $"Crop applied ({srcR - srcL}x{srcB - srcT}) — click preview to Interact; Uncrop in side panel";
    }

    private void UncropPane(int zone)
    {
        if (!CropPreviewEnabled)
        {
            var ww = _tiler.GetZone(zone);
            if (ww != null)
            {
                ww.CropRect = null;
                UnparkCroppedHwnd(ww);
                EnterLiveFill(ww);
                if (_liveDesk) ApplyLiveTiling(activateZone: zone);
            }
            return;
        }

        var w = _tiler.GetZone(zone);
        if (w == null) return;
        w.CropRect = null;
        PersistCropFor(w);
        EndCropMode(apply: false);
        UnparkCroppedHwnd(w);

        if (_liveDesk && !w.PreviewProxy)
        {
            EnterLiveFill(w);
            ApplyLiveTiling(activateZone: zone);
            SyncLivePaneChromeOverlays();
            StatusText.Text = $"Uncropped — Live Fill {w.ShortTitle}";
            return;
        }

        if (_liveDesk)
        {
            var zones = ComputePaneScreenZones();
            NativeMethods.GetWindowMinTrackSize(w.Hwnd, out int minW, out int minH);
            if (zone < zones.Length && NativeMethods.PaneFitsMin(zones[zone], minW, minH))
            {
                EnterLiveFill(w);
                ApplyLiveTiling(activateZone: zone);
                SyncLivePaneChromeOverlays();
                StatusText.Text = $"Uncropped — Live Fill {w.ShortTitle}";
                return;
            }
        }

        RefreshAllThumbnails();
        SyncLivePaneChromeOverlays();
        StatusText.Text = $"Uncropped — full window preview for {w.ShortTitle}";
    }

    private void PreviewThumb_ClickFocus(int zone)
    {
        var w = _tiler.GetZone(zone);
        if (w == null) return;
        if (!IsPreviewProxy(w) && !UsesCropThumbOverlay(w)) return;
        if (NativeMethods.IsWindow(w.Hwnd))
        {
            // Interact: unpark + show + focus REAL hwnd so user can use the app.
            UnparkCroppedHwnd(w);
            NativeMethods.ForceShowAndRedraw(w.Hwnd);
            NativeMethods.RaiseAbovePeersNoActivate(w.Hwnd);
            NativeMethods.FocusWindow(w.Hwnd);
            StatusText.Text = w.HasCrop
                ? $"Interact {w.ShortTitle} — real window focused; Uncrop in side panel for Live Fill"
                : $"Focused {w.ShortTitle} (PreviewProxy — desk thumbnail kept)";
        }
    }

    /// <summary>After desk resize: if PreviewProxy now fits, promote to LiveFill automatically.</summary>
    private void PromotePreviewIfFits()
    {
        if (!_liveDesk) return;
        var zones = ComputePaneScreenZones();
        bool any = false;
        for (int i = 0; i < _paneCount; i++)
        {
            var w = _tiler.GetZone(i);
            if (w == null || !w.PreviewProxy || w.Parked || w.HasCrop) continue;
            if (w.Collapsed || w.Floating) continue; // collapse logic owns re-expand
            if (i >= zones.Length || zones[i].IsEmpty) continue;
            NativeMethods.GetWindowMinTrackSize(w.Hwnd, out int minW, out int minH);
            if (NativeMethods.PaneFitsMin(zones[i], minW, minH))
            {
                EnterLiveFill(w);
                any = true;
            }
        }
        if (any)
            ApplyLiveTiling();
    }

    /// <summary>
    /// SoftRefresh: if the user resized a linked HWND, NEVER Unassign.
    /// PreviewProxy when it no longer fits the pane; keep Live Fill when still within pane.
    /// </summary>
    private void ReconcileUserResizedLinks()
    {
        if (!_liveDesk) return;
        if (WindowState == WindowState.Minimized || _deskMinimized) return;
        // Quiet window after Assign/Link so SoftRefresh cannot race the first tile.
        if (DateTime.UtcNow < _linkQuietUntilUtc) return;

        var zones = ComputePaneScreenZones();
        bool needRefresh = false;
        for (int i = 0; i < _paneCount; i++)
        {
            var w = _tiler.GetZone(i);
            if (w == null || w.Parked || !NativeMethods.IsWindow(w.Hwnd)) continue;
            if (NativeMethods.IsIconic(w.Hwnd)) continue;
            if (i >= zones.Length || zones[i].IsEmpty) continue;
            if (w.HasCrop) continue; // cropped overlay owns the pane visually

            var pane = zones[i];
            NativeMethods.GetWindowMinTrackSize(w.Hwnd, out int minW, out int minH);

            if (!NativeMethods.TryGetVisibleFrame(w.Hwnd, out var frame) || frame.IsEmpty)
            {
                if (!NativeMethods.GetWindowRect(w.Hwnd, out frame) || frame.IsEmpty)
                    continue;
            }

            // SoftRefresh must NOT thrash Live <-> Preview on tiny border/DPI mismatch.
            // Only leave Live Fill when the pane truly cannot host the app min size.
            bool minTooBig = !NativeMethods.PaneFitsMin(pane, minW, minH);

            if (w.PreviewProxy)
                continue;

            // Live Fill: never Unassign on size change. Ignore mild "oversized" frames here
            // (user resize past pane is handled by explicit actions / next ApplyLiveTiling).
            if (minTooBig)
            {
                // Fill mode: keep Live and let ApplyLiveTiling force-stretch; don't flip to Preview.
                if (string.Equals(FitModeForZone(i), "fill", StringComparison.OrdinalIgnoreCase))
                    continue;
                EnterPreviewProxy(w, showPrompt: false);
                needRefresh = true;
            }
        }

        if (needRefresh)
            RefreshAllThumbnails();
    }
}
