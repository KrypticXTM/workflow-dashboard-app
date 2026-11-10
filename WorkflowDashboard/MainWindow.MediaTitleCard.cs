using System;
using System.Windows;
using System.Windows.Threading;

namespace WorkflowDashboard;

/// <summary>
/// v1.0.2 Media preview mode: live window preview vs. title card (replaces the curtain, ad9cfcc).
/// Auto (setting AutoTitleCardMedia, default On): while the playing window is itself on screen - the foreground
/// window, or Live-linked in a shown pane - the preview is redundant, so the title card (big accent title, site,
/// muted note such as "Showing in pane 2") replaces the live DWM thumbnail, which is released. The switch (both
/// ways) only happens after the condition has held steadily for TitleCardSettleMs; a flip back cancels it.
/// The header toggle forces the other mode and sticks until clicked again; right-click: Return to auto and the
/// auto on/off setting. Music sources keep the album-art card (ShowNowCard keeps square art).
/// </summary>
public partial class MainWindow
{
    private const int TitleCardSettleMs = 2000;
    private bool _titleCardOn;               // title card shown instead of the live preview
    private bool _titleCardAutoOn = true;
    private bool? _titleCardManual;          // forced (true = title card, false = live) until Return to auto
    private bool _titleCardPendingWant;
    private bool _titleCardReady;
    private IntPtr _titleCardSrc;
    private bool _titleCardSrcLive;          // source would be shown live (Window / PopOut mode)
    private bool _titleCardActive;
    private DispatcherTimer? _titleCardTimer;

    private void InitMediaTitleCard()
    {
        try
        {
            _titleCardAutoOn = _settings.AutoTitleCardMedia;
            if (MiAutoTitleCard != null) MiAutoTitleCard.IsChecked = _titleCardAutoOn;
            _titleCardReady = true;
            SyncTitleCardButton();
        }
        catch { }
    }

    private void SetTitleCard(bool on)
    {
        if (!_titleCardReady || on == _titleCardOn) return;
        _titleCardOn = on;
        SyncTitleCardButton();
        UpdateMediaPreview();
    }

    private void SyncTitleCardButton()
    {
        if (TbMediaLive == null) return;
        if (TbMediaLive.IsChecked != _titleCardOn) TbMediaLive.IsChecked = _titleCardOn;
        bool manual = _titleCardManual.HasValue;
        string state = _titleCardOn ? "Showing the title card (live preview paused)." : "Showing the live preview.";
        TbMediaLive.ToolTip = manual
            ? state + "\nManual - click to switch; right-click > Return to auto."
            : state + (_titleCardAutoOn ? " Auto: title card while the player window is visible." : " Auto title card is Off.")
              + "\nClick to show the " + (_titleCardOn ? "live preview" : "title card") + " (stays until you click again). Right-click: auto";
        System.Windows.Automation.AutomationProperties.SetName(TbMediaLive,
            (_titleCardOn ? "Show live preview" : "Show title card") + (manual ? " (manual)" : ""));
        if (MiTitleCardReturnAuto != null) MiTitleCardReturnAuto.Visibility = manual ? Visibility.Visible : Visibility.Collapsed;
        TbMediaLive.Opacity = manual ? 0.8 : 1.0;   // subtle manual cue
    }

    private void TbMediaLive_Click(object sender, RoutedEventArgs e)
    {
        bool on = !_titleCardOn;
        _titleCardManual = on;                   // sticks until clicked again / Return to auto
        _titleCardTimer?.Stop();
        SetTitleCard(on);
        SyncTitleCardButton();
        StatusText.Text = (on ? "Media: title card" : "Media: live preview") + " - manual (right-click > Return to auto)";
    }

    private void MiTitleCardReturnAuto_Click(object sender, RoutedEventArgs e)
    {
        _titleCardManual = null;
        _titleCardTimer?.Stop();
        SyncTitleCardButton();
        ReevaluateTitleCard();
        StatusText.Text = _titleCardAutoOn ? "Media preview: back to auto" : "Media preview: back to auto (Auto title card is Off)";
    }

    private void MiAutoTitleCard_Click(object sender, RoutedEventArgs e)
    {
        _titleCardAutoOn = MiAutoTitleCard.IsChecked;
        _settings.AutoTitleCardMedia = _titleCardAutoOn;
        try { Persist(); } catch { }
        ReevaluateTitleCard();
        SyncTitleCardButton();
        StatusText.Text = _titleCardAutoOn ? "Auto title card: On (while the player window is visible)" : "Auto title card: Off";
    }

    /// <summary>From UpdateMediaPreview: the current source and whether it would be shown live.</summary>
    private void EvaluateAutoTitleCard(IntPtr src, bool live, bool active)
    {
        _titleCardSrc = src;
        _titleCardSrcLive = live;
        _titleCardActive = active;
        ReevaluateTitleCard();
    }

    private bool AutoTitleCardWanted() =>
        _titleCardAutoOn && _titleCardActive && _titleCardSrcLive && IsMediaSourceOnScreen(_titleCardSrc, out _);

    private void ReevaluateTitleCard()
    {
        if (!_titleCardReady) return;
        try
        {
            if (_titleCardManual is bool m)
            {
                _titleCardTimer?.Stop();
                SetTitleCard(m);
                return;
            }
            bool want = AutoTitleCardWanted();
            if (want == _titleCardOn) { _titleCardTimer?.Stop(); return; }   // flipped back: cancel the pending switch
            if (_titleCardTimer == null)
            {
                _titleCardTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(TitleCardSettleMs) };
                _titleCardTimer.Tick += (_, _) =>
                {
                    _titleCardTimer!.Stop();
                    if (_titleCardManual.HasValue) return;
                    bool still = AutoTitleCardWanted();
                    if (still == _titleCardPendingWant && still != _titleCardOn) SetTitleCard(still);
                };
            }
            if (_titleCardTimer.IsEnabled && _titleCardPendingWant == want) return;   // already settling toward it
            _titleCardPendingWant = want;
            _titleCardTimer.Stop();
            _titleCardTimer.Start();
        }
        catch { }
    }

    /// <summary>Small muted note under the title card.</summary>
    private string TitleCardNote(IntPtr src)
    {
        if (IsMediaSourceOnScreen(src, out int visualPane))
            return visualPane > 0 ? $"Showing in pane {visualPane}" : "Open in front";
        return _titleCardManual == true ? "Title card - toggle in the header for live" : "";
    }

    /// <summary>The playing window is itself visible: foreground (pane 0), or Live-linked in a shown pane (its visual number).</summary>
    private bool IsMediaSourceOnScreen(IntPtr src, out int visualPane)
    {
        visualPane = 0;
        if (src == IntPtr.Zero || !NativeMethods.IsWindow(src) || NativeMethods.IsIconic(src) || !NativeMethods.IsWindowVisible(src))
            return false;
        if (_liveDesk && !_deskMinimized && WindowState != WindowState.Minimized)
        {
            for (int i = 0; i < _paneCount; i++)
            {
                var w = _tiler.GetZone(i);
                if (w != null && w.Hwnd == src && !w.Parked && !w.Collapsed && !w.PreviewProxy)
                {
                    visualPane = VisualPaneNumber(i);
                    return true;
                }
            }
        }
        IntPtr fg = NativeMethods.GetForegroundWindow();
        IntPtr root = fg == IntPtr.Zero ? IntPtr.Zero : GetAncestor(fg, GA_ROOT);
        return fg == src || root == src;
    }
}
