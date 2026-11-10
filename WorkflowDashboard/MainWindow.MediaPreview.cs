using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace WorkflowDashboard;

/// <summary>
/// v1.0.2 Media section live preview.
///  - Source = the SHOWN SMTC session (MediaSessionService.SelectedEntry): click-to-lock via a foreground WinEvent
///    hook, the switcher arrows, or (no lock yet) Windows' current session. Never follows the foreground window.
///  - The session's window is bound once and kept while its title still shows the media title. Same window but the
///    tab title no longer matches = background tab: the browser's pop-out (PiP) is used if one is open, otherwise an
///    album-art / title card "Playing in a background tab - click to show".
///  - Crop: YouTube in a browser shows only the player (UIA "YouTube Video Player" rect, cached, re-read every 1.5 s
///    while the preview is live); other sources use the window's visible frame (invisible borders excluded).
///  - Fill: the crop is center-trimmed to the slot's aspect (cover), so the thumbnail reaches all four edges.
///  - Pop-out: optional "Hide browser pop-out video" parks the PiP with only a 1 px column on the screen edge
///    (Chromium stops painting windows that are fully off-screen or fully covered), so it keeps rendering into
///    the preview; restored to its original spot when the tab comes back, the PiP closes, or the app exits.
/// </summary>
public partial class MainWindow
{
    private const double MediaPreviewPanelShare = 0.28;
    private const double MediaPreviewMinHeight = 72;
    private const double MediaCardBaseMaxHeight = 230;

    private enum MediaPreviewMode { None, Window, PopOut, BackgroundTab, Minimized, Missing }

    private IntPtr _mediaPreviewHwnd;
    private bool _mediaPreviewCollapsed;
    private bool _mediaPreviewShowingLive;
    private DispatcherTimer? _mediaEventTimer;
    private bool _nowPlayingRefreshBusy;
    private bool _nowPlayingRefreshAgain;   // v1.0.11: a request that arrived while busy runs right after
    private Point _mediaPreviewLastPos = new(double.NaN, double.NaN);
    private MediaPreviewMode _mediaPreviewMode;
    private bool _mediaLoggedTitleCard;

    // Sticky binding: session -> window.
    private IntPtr _mediaBoundHwnd;
    private object? _mediaBoundSession;
    private IntPtr _mediaPendingHwnd;
    private object? _mediaPendingSession;
    private string _mediaBoundFrag = "";
    private HashSet<uint> _mediaPids = new();
    private string _mediaPidsProc = "";
    private DateTime _mediaPidsAt;

    // Pop-out (PiP).
    private IntPtr _popOutHwnd;
    private NativeMethods.Rect _popOutOrigRect;
    private NativeMethods.Rect _popOutParkRect;
    private bool _popOutParked;
    private int _popOutParkTries;

    // Crop.
    private DispatcherTimer? _mediaCropTimer;
    private AutomationElement? _playerEl;
    private IntPtr _playerElHwnd;
    private NativeMethods.Rect? _playerRel;
    private IntPtr _playerRelHwnd;
    private bool _playerProbeBusy;
    private DateTime _playerMissUntil;
    private NativeMethods.Rect _cropLastWr;
    private string _cropLastTitle = "";

    // Foreground hook (click-to-lock).
    private IntPtr _fgHook;
    private WinEventDelegate? _fgHookProc;

    private delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint idEventThread, uint dwmsEventTime);
    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmod, WinEventDelegate proc, uint idProcess, uint idThread, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hWinEventHook);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
    [StructLayout(LayoutKind.Sequential)] private struct MpPoint { public int X, Y; }
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(MpPoint pt, uint flags);
    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private const uint GA_ROOT = 2;
    private const uint SWP_NOOWNERZORDER_MP = 0x0200;

    private static readonly string[] PopOutTitles =
        { "Picture in Picture", "Picture-in-Picture", "Picture in picture", "Video pop out", "Video pop-out", "Pop out video" };
    private static readonly HashSet<string> BrowserProcs = new(StringComparer.OrdinalIgnoreCase)
        { "opera", "opera_gx", "chrome", "msedge", "brave", "vivaldi", "firefox" };

    private void InitMediaPreview()
    {
        try
        {
            _mediaPreviewCollapsed = _settings.MediaPreviewCollapsed;
            TbMediaCollapse.IsChecked = _mediaPreviewCollapsed;
            SidePanel.SizeChanged += (_, _) => UpdateMediaPreviewSize();
            MediaCard.SizeChanged += (_, e) => { if (Math.Abs(e.PreviousSize.Width - e.NewSize.Width) > 0.5) UpdateMediaPreviewSize(); };
            MediaPreviewThumb.IsVisibleChanged += (_, e) =>
            {
                if (e.NewValue is bool v && !v) MediaPreviewThumb.Clear();
                else if (_mediaPreviewShowingLive && _mediaPreviewHwnd != IntPtr.Zero) AssignMediaThumb(_mediaPreviewHwnd);
            };
            MediaPreviewThumb.SizeChanged += (_, _) => ApplyMediaPreviewCrop();
            // v1.1.1: arm the popup quiet window on the right-button press, before the menu takes activation.
            MediaPreviewFrame.PreviewMouseRightButtonDown += (_, _) => ArmMediaMenuQuiet(null);
            if (TbMediaLive != null) TbMediaLive.PreviewMouseRightButtonDown += (_, _) => ArmMediaMenuQuiet(null);
            LayoutUpdated += (_, _) => TrackMediaPreviewPosition();
            _media.NowPlayingChanged += () =>
            {
                try { Dispatcher.BeginInvoke(ScheduleNowPlayingRefresh, DispatcherPriority.Background); } catch { }
            };
            _mediaCropTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(1500) };
            _mediaCropTimer.Tick += (_, _) => MediaCropTick();
            _fgHookProc = OnForegroundChanged;
            _fgHook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _fgHookProc, 0, 0, WINEVENT_OUTOFCONTEXT);
            StartPipWatch();
            UpdateMediaPreviewSize();
            InitMediaTitleCard();
            UpdateMediaPreview();
        }
        catch { }
    }

    /// <summary>App exit: unhook, and put a parked pop-out back where the browser had it.</summary>
    private void ShutdownMediaPreview()
    {
        try { if (_fgHook != IntPtr.Zero) UnhookWinEvent(_fgHook); } catch { }
        _fgHook = IntPtr.Zero;
        try { _mediaCropTimer?.Stop(); } catch { }
        StopPipWatch();
        try { ReleasePopOut(); } catch { }
        try { MediaPreviewThumb.Clear(); } catch { }
    }

    private void ScheduleNowPlayingRefresh()
    {
        if (_mediaEventTimer == null)
        {
            _mediaEventTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            _mediaEventTimer.Tick += async (_, _) =>
            {
                _mediaEventTimer!.Stop();
                await RefreshNowPlayingNowAsync();
            };
        }
        _mediaEventTimer.Stop();
        _mediaEventTimer.Start();
    }

    /// <summary>Shared by the 1.2 s media tick and SMTC events; never overlaps itself.</summary>
    private async Task RefreshNowPlayingNowAsync()
    {
        // v1.0.11: a foreground-change / SMTC-event refresh that arrived while a tick refresh was running used to
        // be DROPPED (it then waited for the next 1.2 s tick or longer). Now it is queued and runs right after.
        if (_nowPlayingRefreshBusy) { _nowPlayingRefreshAgain = true; return; }
        _nowPlayingRefreshBusy = true;
        try
        {
            int rounds = 0;
            do
            {
                _nowPlayingRefreshAgain = false;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                string before = _media.NowPlayingLabel;
                await _media.RefreshNowPlayingAsync();
                long svcMs = sw.ElapsedMilliseconds;
                SyncMediaNowPlayingUi();
                ScheduleMediaDecisionRefresh();
                long allMs = sw.ElapsedMilliseconds;
                if (allMs >= 250 || before != _media.NowPlayingLabel)
                    MediaLog($"refresh {allMs} ms (service {svcMs} ms, ui {allMs - svcMs} ms){(before != _media.NowPlayingLabel ? " -> '" + _media.NowPlayingLabel + "'" : "")}" +
                             $" src={(_media.NowPlayingFromAudio ? "audio" : "smtc")} dur={_media.NowPlayingDurationSec:0} pinned={_media.IsPinned}");
            } while (_nowPlayingRefreshAgain && ++rounds < 3);
        }
        catch { }
        finally { _nowPlayingRefreshBusy = false; }
    }

    /// <summary>v1.0.11 media trace (%LOCALAPPDATA%\WorkflowDashboard\media.log, rolled at 256 KB): slow refreshes and source switches.</summary>
    private static readonly object _mediaLogLock = new();
    internal static void MediaLog(string msg)
    {
        lock (_mediaLogLock)   // v1.1.3: called from UI and service threads
        try
        {
            var path = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WorkflowDashboard", "media.log");
            var fi = new System.IO.FileInfo(path);
            if (fi.Exists && fi.Length > 256 * 1024) System.IO.File.Move(path, path + ".1", overwrite: true);
            System.IO.File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {msg}{Environment.NewLine}");
        }
        catch { }
    }

    private DispatcherTimer? _mediaDueTimer;
    private DateTime _mediaDueAt;

    /// <summary>v1.0.2: one extra refresh at the idle hand-off deadline (pause primary -> switch in ~2 s).</summary>
    private void ScheduleMediaDecisionRefresh()
    {
        var due = _media.DecisionDueUtc;
        var now = DateTime.UtcNow;
        if (due <= now || due == _mediaDueAt) return;
        _mediaDueAt = due;
        if (_mediaDueTimer == null)
        {
            _mediaDueTimer = new DispatcherTimer();
            _mediaDueTimer.Tick += async (_, _) => { _mediaDueTimer!.Stop(); await RefreshNowPlayingNowAsync(); };
        }
        _mediaDueTimer.Stop();
        _mediaDueTimer.Interval = due - now;
        _mediaDueTimer.Start();
    }

    private void TbMediaCollapse_Click(object sender, RoutedEventArgs e)
    {
        _mediaPreviewCollapsed = TbMediaCollapse.IsChecked == true;
        _settings.MediaPreviewCollapsed = _mediaPreviewCollapsed;
        try { Persist(); } catch { }
        UpdateMediaPreview();
        StatusText.Text = _mediaPreviewCollapsed ? "Media preview hidden (chevron to show)" : "Media preview shown";
    }

    private void UpdateMediaPreviewSize()
    {
        try
        {
            double inner = MediaCard.ActualWidth > 40 ? MediaCard.ActualWidth - 2 - 16 - 2 : SidePanel.Width - 2 - 16 - 2 - 16 - 2;
            if (inner < 40) return;
            double h = inner * 9.0 / 16.0;
            if (SidePanel.ActualHeight > 100) h = Math.Min(h, SidePanel.ActualHeight * MediaPreviewPanelShare);
            h = Math.Round(Math.Max(MediaPreviewMinHeight, h));
            if (Math.Abs(MediaPreviewGrid.Height - h) > 0.5) MediaPreviewGrid.Height = h;
            ApplyMediaCardMaxHeight();
        }
        catch { }
    }

    private void ApplyMediaCardMaxHeight()
    {
        double extra = MediaPreviewFrame.Visibility == Visibility.Visible ? MediaPreviewGrid.Height + 2 + 6
            : MediaNothingStrip.Visibility == Visibility.Visible ? 26 : 0;
        if (MediaSwitcher.Visibility == Visibility.Visible) extra += 24;
        MediaCard.MaxHeight = MediaCardBaseMaxHeight + extra;
    }

    private void TrackMediaPreviewPosition()
    {
        try
        {
            if (!_mediaPreviewShowingLive || !MediaPreviewThumb.IsVisible) return;
            var p = MediaPreviewThumb.TransformToAncestor(this).Transform(new Point(0, 0));
            if (p == _mediaPreviewLastPos) return;
            _mediaPreviewLastPos = p;
            MediaPreviewThumb.RefreshLayout();
        }
        catch { }
    }

    // ---------------------------------------------------------------- switcher (2+ sessions)

    private void UpdateMediaSwitcher()
    {
        var all = _media.AllSessions;
        int idx = _media.SelectedIndex;
        bool show = !_mediaPreviewCollapsed && all.Count >= 2;
        MediaSwitcher.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show) return;
        var sel = idx >= 0 ? all[idx] : null;
        string label = sel?.Label ?? "";
        if (sel != null && !sel.IsPlaying && sel.Status != "") label += " (" + sel.Status.ToLowerInvariant() + ")";
        TxtMediaSrc.Text = $"{(idx >= 0 ? idx + 1 : 0)}/{all.Count}  {label}";
        var tip = new StringBuilder(_media.HasLock ? "Locked source (click a media window or use the arrows to change):" : "Media sources:");
        for (int i = 0; i < all.Count; i++)
            tip.Append('\n').Append(i == idx ? "> " : "   ").Append(i + 1).Append(". ").Append(all[i].Label).Append(all[i].HostSuffix)
               .Append(all[i].IsPlaying ? "" : " (" + all[i].Status.ToLowerInvariant() + ")");
        TxtMediaSrc.ToolTip = tip.ToString();
    }

    private void BtnMediaSrcPrev_Click(object sender, RoutedEventArgs e) => StepMediaSource(-1);
    private void BtnMediaSrcNext_Click(object sender, RoutedEventArgs e) => StepMediaSource(+1);

    private void StepMediaSource(int dir)
    {
        try
        {
            var all = _media.AllSessions;
            if (all.Count < 2) return;
            int idx = _media.SelectedIndex;
            int next = ((idx < 0 ? 0 : idx) + dir + all.Count) % all.Count;
            _media.LockSession(all[next], manual: true);   // switcher pick pins it
            _mediaPendingHwnd = IntPtr.Zero;
            _mediaPendingSession = null;
            StatusText.Text = "Media source locked: " + all[next].Label;
            ScheduleNowPlayingRefresh();
            UpdateMediaSwitcher();
        }
        catch { }
    }

    // ---------------------------------------------------------------- click-to-lock (foreground WinEvent)

    private void OnForegroundChanged(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        try { Dispatcher.BeginInvoke(ReevaluateTitleCard, DispatcherPriority.Background); } catch { }
        try
        {
            if (hwnd == IntPtr.Zero || idObject != 0) return;
            IntPtr root = GetAncestor(hwnd, GA_ROOT);
            if (root == IntPtr.Zero) root = hwnd;
            NativeMethods.GetWindowThreadProcessId(root, out uint pid);
            if (pid == 0 || pid == (uint)Environment.ProcessId) return;
            // v1.0.11: window switch -> the next refresh may look at the tab strip again at once (was throttled to 5 s),
            // and refresh promptly even when no SMTC session matches (Opera without a session = audio fallback).
            _media.NoteForegroundChanged();
            if (!_media.IsPinned) ScheduleNowPlayingRefresh();
            var all = _media.AllSessions;
            if (all.Count == 0) return;
            string proc = ProcessNameOfHwnd(root);
            if (string.IsNullOrEmpty(proc)) return;
            string title = GetWindowTitle(root);
            bool popOut = IsPopOutWindow(root);

            // 1) a session whose title is this window's title (the active tab / the player window)
            SmtcSessionEntry? hit = null;
            foreach (var s in all)
            {
                string f = TitleFragment(s.Title);
                bool procOk = s.ProcName.Length == 0 || string.Equals(s.ProcName, proc, StringComparison.OrdinalIgnoreCase);
                if (procOk && f.Length >= 3 && title.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    if (hit == null || (!hit.IsPlaying && s.IsPlaying)) hit = s;
                }
            }
            // 2) Only windows that are VERIFIABLY the playing source may lock (9:02 PM fix: clicking any Opera
            //    window - X, a shop tab - used to lock it because Opera has one session per process):
            //    a browser pop-out (PiP), or a native media app (Spotify, ...) whose session is playing.
            //    A browser window whose title doesn't match the media title never locks or rebinds.
            if (hit == null)
            {
                var mine = all.Where(s => string.Equals(s.ProcName, proc, StringComparison.OrdinalIgnoreCase)).ToList();
                bool browserProc = BrowserProcs.Contains(proc);
                if (popOut && mine.Count > 0) hit = mine.FirstOrDefault(s => s.IsPlaying) ?? mine[0];
                else if (!browserProc && mine.Count == 1 && mine[0].IsPlaying) hit = mine[0];
            }
            if (hit == null) return; // not the playing source: keep the current lock and source

            bool changed = !ReferenceEquals(_media.SelectedEntry?.Session, hit.Session) || (!popOut && _mediaBoundHwnd != root);
            _media.LockSession(hit);
            if (!popOut)
            {
                _mediaPendingHwnd = root;
                _mediaPendingSession = hit.Session;
            }
            if (changed)
            {
                ScheduleNowPlayingRefresh();
                UpdateMediaSwitcher();
            }
        }
        catch { }
    }

    // ---------------------------------------------------------------- main update

    /// <summary>Called after every now-playing refresh (SyncMediaNowPlayingUi).</summary>
    private void UpdateMediaPreview()
    {
        try
        {
            if (MediaPreviewFrame == null) return;
            string status = _media.NowPlayingStatus ?? "";
            bool hasSession = !string.IsNullOrWhiteSpace(_media.NowPlayingLabel);
            bool active = hasSession && (status == "Playing" || status == "Paused" || status == "Changing");
            MediaListScroll.Visibility = _mediaPreviewCollapsed ? Visibility.Collapsed : Visibility.Visible;
            UpdateMediaSwitcher();
            SyncMediaPinUi();

            // v1.0.2: Opera drops its SMTC session while a video is popped out. A parked pop-out with no session
            // is still the thing playing: show it here instead of "Nothing playing" (and never unpark it).
            bool pipOnly = !active && !_mediaPreviewCollapsed && _settings.HideBrowserPopOut && _popOutParked
                           && _popOutHwnd != IntPtr.Zero && NativeMethods.IsWindow(_popOutHwnd)
                           && NativeMethods.IsWindowVisible(_popOutHwnd) && !NativeMethods.IsIconic(_popOutHwnd);
            if (_mediaPreviewCollapsed || (!active && !pipOnly))
            {
                HideLivePreview();
                HideNowCard();
                MediaPreviewFrame.Visibility = Visibility.Collapsed;
                MediaNothingStrip.Visibility = !_mediaPreviewCollapsed && !active ? Visibility.Visible : Visibility.Collapsed;
                ApplyMediaCardMaxHeight();
                EvaluateAutoTitleCard(IntPtr.Zero, false, false);
                return;
            }

            MediaNothingStrip.Visibility = Visibility.Collapsed;
            if (MediaPreviewFrame.Visibility != Visibility.Visible)
            {
                MediaPreviewFrame.Visibility = Visibility.Visible;
                UpdateMediaPreviewSize();
            }
            ApplyMediaCardMaxHeight();

            IntPtr src;
            MediaPreviewMode mode;
            if (pipOnly) { src = _popOutHwnd; mode = MediaPreviewMode.PopOut; }
            else src = ResolveMediaPreviewSource(out mode);
            if (mode != _mediaPreviewMode || _titleCardOn != _mediaLoggedTitleCard)
            {
                _mediaLoggedTitleCard = _titleCardOn;
                MediaLog($"preview mode={mode} titleCard={_titleCardOn} (live preview has no progress bar)");
            }
            _mediaPreviewMode = mode;
            EvaluateAutoTitleCard(src, mode == MediaPreviewMode.Window || mode == MediaPreviewMode.PopOut, true);
            // Browser AUDIO sites (Spotify web, YouTube Music, SoundCloud ...): always the now-playing card - the
            // live browser window would only mirror whichever tab is active.
            bool browserSrc = BrowserProcs.Contains(_media.NowPlayingApp ?? "");
            bool audioSite = browserSrc && MediaSessionService.IsAudioSite(_media.NowPlayingSite ?? "");
            string label = TxtMediaNow?.Text ?? "";
            string clickTip = mode switch
            {
                MediaPreviewMode.BackgroundTab => "Click: switch back to that tab.",
                MediaPreviewMode.PopOut => _popOutParked ? "Showing the browser's pop-out video (hidden). Click: switch back to the tab." : "Showing the browser's pop-out video. Click: bring the browser forward.",
                MediaPreviewMode.Minimized => "Click: restore the window.",
                _ => "Click: bring this window forward.",
            };
            MediaPreviewFrame.ToolTip = (status == "Paused" ? "Paused - " : "") + label + "\n" + clickTip + " Right-click: more.";

            if ((mode == MediaPreviewMode.Window || mode == MediaPreviewMode.PopOut) && !audioSite)
            {
                HideNowCard();
                MediaPreviewArt.Visibility = Visibility.Collapsed;
                MediaPreviewCard.Visibility = Visibility.Collapsed;
                MediaPreviewNote.Visibility = Visibility.Collapsed;
                if (_titleCardOn)
                {
                    // Preview redundant (or forced): title card instead of the live thumbnail (DWM thumb released).
                    if (_mediaPreviewShowingLive) HideLivePreview();
                    _mediaPreviewHwnd = src;
                    ShowNowCard(TitleCardNote(src), textOnly: true);
                    return;
                }
                if (!_mediaPreviewShowingLive || MediaPreviewThumb.SourceHwnd != src || !MediaPreviewThumb.HasThumbnail)
                {
                    _mediaPreviewHwnd = src;
                    _mediaPreviewShowingLive = true;
                    _mediaPreviewLastPos = new Point(double.NaN, double.NaN);
                    if (MediaPreviewThumb.IsVisible) AssignMediaThumb(src);
                }
                else ApplyMediaPreviewCrop();
                if (_mediaCropTimer != null && !_mediaCropTimer.IsEnabled) _mediaCropTimer.Start();
                if (mode == MediaPreviewMode.Window && WantsPlayerCrop(src) && _playerRelHwnd != src) ProbePlayerRect(src);
                return;
            }

            // Audio site / background tab / minimized / tray: never a live window -> themed now-playing card.
            HideLivePreview();
            _mediaPreviewHwnd = src;
            string note = audioSite
                ? (mode == MediaPreviewMode.BackgroundTab ? "Click: open the player tab" : "")
                : mode switch
                {
                    MediaPreviewMode.BackgroundTab => "Background tab - click to show",
                    MediaPreviewMode.Minimized => "Minimized - click to restore",
                    MediaPreviewMode.Missing => browserSrc ? "Tab not found" : "",
                    _ => "",
                };
            ShowNowCard(note);
        }
        catch { }
    }

    private void AssignMediaThumb(IntPtr src)
    {
        if (_titleCardOn) return;
        MediaPreviewThumb.Clear();          // also resets the previous source's crop
        MediaPreviewThumb.Assign(src);
        _cropLastWr = default;
        ApplyMediaPreviewCrop();
    }

    private void HideLivePreview()
    {
        _mediaPreviewShowingLive = false;
        try { _mediaCropTimer?.Stop(); } catch { }
        try { MediaPreviewThumb.Clear(); } catch { }
    }

    private static string TitleFragment(string? title)
    {
        string frag = TitleSanitizer.Clean(title).TrimEnd('\u2026', '.', ' ');
        return frag.Length > 24 ? frag[..24] : frag;
    }

    private HashSet<uint> PidsForProcess(string proc)
    {
        if (string.Equals(proc, _mediaPidsProc, StringComparison.OrdinalIgnoreCase) && (DateTime.UtcNow - _mediaPidsAt).TotalSeconds < 5)
            return _mediaPids;
        HashSet<uint> pids;
        try { pids = Process.GetProcessesByName(proc).Select(p => { using (p) return (uint)p.Id; }).ToHashSet(); }
        catch { pids = new HashSet<uint>(); }
        _mediaPids = pids;
        _mediaPidsProc = proc;
        _mediaPidsAt = DateTime.UtcNow;
        return pids;
    }

    /// <summary>
    /// Shown session -> source window. Bound once per session; never biased toward the foreground or a linked
    /// pane (linked only breaks a tie between equally matching windows).
    /// </summary>
    private IntPtr ResolveMediaPreviewSource(out MediaPreviewMode mode)
    {
        IntPtr resolved = _media.NowPlayingHwnd;
        string proc = (_media.NowPlayingApp ?? "").Trim();
        object? session = _media.SelectedEntry?.Session;

        if (!ReferenceEquals(session, _mediaBoundSession))
        {
            _mediaBoundHwnd = session != null && ReferenceEquals(session, _mediaPendingSession) ? _mediaPendingHwnd : IntPtr.Zero;
            _mediaBoundSession = session;
            _playerRelHwnd = IntPtr.Zero;
        }
        else if (session != null && ReferenceEquals(session, _mediaPendingSession) && _mediaPendingHwnd != IntPtr.Zero)
        {
            _mediaBoundHwnd = _mediaPendingHwnd;   // Tim clicked a (different) window of the same session
        }
        _mediaPendingHwnd = IntPtr.Zero;
        _mediaPendingSession = null;

        if (string.IsNullOrEmpty(proc) || proc == "Media")
        {
            mode = resolved == IntPtr.Zero || !NativeMethods.IsWindow(resolved) ? MediaPreviewMode.Missing
                : NativeMethods.IsIconic(resolved) ? MediaPreviewMode.Minimized : MediaPreviewMode.Window;
            return resolved;
        }

        bool browser = BrowserProcs.Contains(proc);
        string frag = TitleFragment(_media.NowPlayingTitle);
        _mediaBoundFrag = frag;
        bool Matches(IntPtr h) => frag.Length >= 3 && GetWindowTitle(h).IndexOf(frag, StringComparison.OrdinalIgnoreCase) >= 0;

        var pids = PidsForProcess(proc);
        var mains = new List<IntPtr>();
        IntPtr pip = IntPtr.Zero;
        if (pids.Count > 0)
        {
            NativeMethods.EnumWindows((h, _) =>
            {
                NativeMethods.GetWindowThreadProcessId(h, out uint pid);
                if (!pids.Contains(pid)) return true;
                bool vis = NativeMethods.IsWindowVisible(h);
                if (browser && IsPopOutWindow(h))
                {
                    if (vis && !NativeMethods.IsIconic(h) && pip == IntPtr.Zero) pip = h;
                    return true;
                }
                if (!vis && !NativeMethods.IsIconic(h)) return true;
                long ex = NativeMethods.GetWindowLongPtr(h, NativeMethods.GWL_EXSTYLE).ToInt64();
                if ((ex & NativeMethods.WS_EX_TOOLWINDOW) != 0) return true;
                if (NativeMethods.GetWindowTextLength(h) <= 0) return true;
                mains.Add(h);
                return true;
            }, IntPtr.Zero);
        }

        bool BelongsToSession(IntPtr h) => h != IntPtr.Zero && NativeMethods.IsWindow(h) && PidOf(h) is uint p && pids.Contains(p);
        IntPtr main = IntPtr.Zero;
        bool tabActive;
        // Browser: the bound window is "showing" the media only while its active tab matches the media title;
        // with no title (playing tab unknown) it's never shown live - that would mirror whatever tab Tim clicks.
        if (BelongsToSession(_mediaBoundHwnd) && (!browser || (frag.Length >= 3 && Matches(_mediaBoundHwnd))))
        {
            main = _mediaBoundHwnd;
            tabActive = true;
        }
        else
        {
            var matching = browser && frag.Length >= 3 ? mains.Where(Matches).ToList() : new List<IntPtr>();
            if (matching.Count > 0)
            {
                var linked = _tiler.WorkingSet.Where(w => !w.Parked).Select(w => w.Hwnd).ToHashSet();
                main = matching.FirstOrDefault(linked.Contains);
                if (main == IntPtr.Zero) main = matching[0];
            }
            else if (_media.NowPlayingFromAudio && BelongsToSession(resolved) && !IsPopOutWindow(resolved)) main = resolved;   // sticky audio pick
            else if (BelongsToSession(_mediaBoundHwnd)) main = _mediaBoundHwnd;   // same window, tab in the background
            else if (BelongsToSession(resolved) && !IsPopOutWindow(resolved)) main = resolved;
            else if (mains.Count > 0) main = mains[0];
            tabActive = main != IntPtr.Zero && (!browser || (frag.Length >= 3 && Matches(main)));
        }
        if (main != IntPtr.Zero) _mediaBoundHwnd = main;

        // The pop-out can't be tied to a session (generic title), so only use it while the SHOWN session's tab is
        // in the background. With the shown tab active, a pop-out belongs to another tab (or Tim popped it out
        // on purpose): leave it alone.
        if (pip != IntPtr.Zero && !tabActive)
        {
            // Auto pop-out while the tab is in the background: hide it (if enabled) and show it here.
            HandlePopOut(pip, park: _settings.HideBrowserPopOut && !tabActive);
            mode = MediaPreviewMode.PopOut;
            return pip;
        }
        // Not the shown source: the PiP watchdog still keeps it parked (setting on) - don't unpark here.

        if (main == IntPtr.Zero) { mode = MediaPreviewMode.Missing; return IntPtr.Zero; }
        mode = NativeMethods.IsIconic(main) ? MediaPreviewMode.Minimized
             : !tabActive ? MediaPreviewMode.BackgroundTab
             : MediaPreviewMode.Window;
        return main;
    }

    private static uint? PidOf(IntPtr h)
    {
        NativeMethods.GetWindowThreadProcessId(h, out uint pid);
        return pid == 0 ? null : pid;
    }

    private static string ClassOf(IntPtr h)
    {
        var sb = new StringBuilder(256);
        return GetClassName(h, sb, sb.Capacity) > 0 ? sb.ToString() : "";
    }

    /// <summary>Chromium / Opera video pop-out: Chrome_WidgetWin_* titled "Picture in Picture" (verified on Tim's PC).</summary>
    private static bool IsPopOutWindow(IntPtr h)
    {
        string t = GetWindowTitle(h);
        if (t.Length == 0 || !PopOutTitles.Any(p => t.StartsWith(p, StringComparison.OrdinalIgnoreCase))) return false;
        return ClassOf(h).StartsWith("Chrome_WidgetWin", StringComparison.Ordinal);
    }

    private static string ProcessNameOfHwnd(IntPtr h)
    {
        try
        {
            NativeMethods.GetWindowThreadProcessId(h, out uint pid);
            if (pid == 0) return "";
            using var p = Process.GetProcessById((int)pid);
            return p.ProcessName;
        }
        catch { return ""; }
    }

    // ---------------------------------------------------------------- pop-out parking

    private void HandlePopOut(IntPtr pip, bool park)
    {
        if (pip != _popOutHwnd)
        {
            ReleasePopOut();
            _popOutHwnd = pip;
            _popOutParked = false;
            _popOutParkTries = 0;
        }
        if (!park)
        {
            if (_popOutParked) UnparkPopOut();
            return;
        }
        // Parking (first park, re-park with backoff, hands-off while Tim is on it) is owned by PipWatchTick.
        if (!_popOutParked && !UserIsOnWindow(pip) && NativeMethods.GetWindowRect(pip, out var r))
        {
            _popOutOrigRect = r;
            ParkPopOut(pip, r);
            _pipStableSinceUtc = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Leave exactly a 1 px column on the right edge of the right-most monitor. Fully off-screen or fully covered
    /// Chromium windows are marked occluded and stop painting (native_window_occlusion_tracker_win.cc); the PiP is
    /// topmost, so that one column keeps it "visible" and the DWM thumbnail stays live.
    /// </summary>
    private void ParkPopOut(IntPtr pip, NativeMethods.Rect r)
    {
        int vx = NativeMethods.GetSystemMetrics(76), vy = NativeMethods.GetSystemMetrics(77);
        int vw = NativeMethods.GetSystemMetrics(78), vh = NativeMethods.GetSystemMetrics(79);
        int vRight = vx + vw;
        var hMon = MonitorFromPoint(new MpPoint { X = vRight - 1, Y = vy + vh / 2 }, 2 /*NEAREST*/);
        var info = new NativeMethods.MonitorInfoEx { cbSize = Marshal.SizeOf<NativeMethods.MonitorInfoEx>() };
        var work = new NativeMethods.Rect { Left = vx, Top = vy, Right = vRight, Bottom = vy + vh };
        if (hMon != IntPtr.Zero && NativeMethods.GetMonitorInfo(hMon, ref info)) work = info.rcWork;
        int h = Math.Max(1, _popOutOrigRect.Height);
        int y = Math.Clamp(_popOutOrigRect.Top, work.Top, Math.Max(work.Top, work.Bottom - h));
        int x = vRight - 1;
        _popOutParkTries++;
        NativeMethods.SetWindowPos(pip, IntPtr.Zero, x, y, 0, 0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE | SWP_NOOWNERZORDER_MP);
        NativeMethods.GetWindowRect(pip, out _popOutParkRect);
        _popOutParked = true;
        _cropLastWr = default;
    }

    private void UnparkPopOut()
    {
        if (_popOutParked && _popOutHwnd != IntPtr.Zero && NativeMethods.IsWindow(_popOutHwnd))
        {
            NativeMethods.SetWindowPos(_popOutHwnd, IntPtr.Zero, _popOutOrigRect.Left, _popOutOrigRect.Top, 0, 0,
                NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE | SWP_NOOWNERZORDER_MP);
        }
        _popOutParked = false;
    }

    /// <summary>PiP closed/hidden, feature off, or app exit: put it back so the browser remembers sane bounds.</summary>
    private void ReleasePopOut()
    {
        if (_popOutHwnd == IntPtr.Zero) return;
        UnparkPopOut();
        _popOutHwnd = IntPtr.Zero;
        _popOutParkTries = 0;
    }

    // ---------------------------------------------------------------- crop + cover fill

    private bool WantsPlayerCrop(IntPtr src)
    {
        if (src == IntPtr.Zero || _mediaPreviewMode != MediaPreviewMode.Window) return false;
        if (!BrowserProcs.Contains(_media.NowPlayingApp ?? "")) return false;
        return GetWindowTitle(src).IndexOf("YouTube", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>1.5 s while the live preview is shown: re-read the cached player rect (moves, theater, fullscreen).</summary>
    private void MediaCropTick()
    {
        try
        {
            if (!_mediaPreviewShowingLive || !MediaPreviewThumb.IsVisible || !MediaPreviewThumb.HasThumbnail)
            {
                _mediaCropTimer?.Stop();
                return;
            }
            var src = MediaPreviewThumb.SourceHwnd;
            if (!NativeMethods.GetWindowRect(src, out var wr)) return;
            string t = GetWindowTitle(src);
            bool changed = wr.Left != _cropLastWr.Left || wr.Top != _cropLastWr.Top || wr.Width != _cropLastWr.Width
                           || wr.Height != _cropLastWr.Height || t != _cropLastTitle;
            _cropLastWr = wr;
            _cropLastTitle = t;
            if (WantsPlayerCrop(src)) ProbePlayerRect(src);
            else
            {
                if (_playerRelHwnd == src) { _playerRel = null; _playerRelHwnd = IntPtr.Zero; changed = true; }
                if (changed) ApplyMediaPreviewCrop();
            }
        }
        catch { }
    }

    /// <summary>Off the UI thread: cached player element -> BoundingRectangle; full FindPlayer only if lost.</summary>
    private void ProbePlayerRect(IntPtr hwnd)
    {
        if (_playerProbeBusy) return;
        var cached = _playerElHwnd == hwnd ? _playerEl : null;
        if (cached == null && DateTime.UtcNow < _playerMissUntil && _playerElHwnd == hwnd) return;
        _playerProbeBusy = true;
        Task.Run(() =>
        {
            AutomationElement? el = cached;
            Rect r = Rect.Empty;
            try { if (el != null) r = el.Current.BoundingRectangle; } catch { el = null; }
            if (el == null || r.IsEmpty || r.Width < 2)
            {
                try { el = SkipAdService.FindPlayer(hwnd); r = el?.Current.BoundingRectangle ?? Rect.Empty; }
                catch { el = null; r = Rect.Empty; }
            }
            return (el, r);
        }).ContinueWith(task =>
        {
            Dispatcher.BeginInvoke(() =>
            {
                _playerProbeBusy = false;
                AutomationElement? el = null;
                Rect r = Rect.Empty;
                if (task.Status == TaskStatus.RanToCompletion) (el, r) = task.Result;
                _playerEl = el;
                _playerElHwnd = hwnd;
                if (el == null) _playerMissUntil = DateTime.UtcNow.AddSeconds(6);
                NativeMethods.Rect? rel = null;
                if (!r.IsEmpty && r.Width >= 80 && r.Height >= 45 && NativeMethods.GetWindowRect(hwnd, out var wr))
                {
                    var clip = new NativeMethods.Rect { Left = 0, Top = 0, Right = wr.Width, Bottom = wr.Height };
                    if (NativeMethods.TryGetVisibleFrame(hwnd, out var vf) && !vf.IsEmpty)
                        clip = new NativeMethods.Rect { Left = vf.Left - wr.Left, Top = vf.Top - wr.Top, Right = vf.Right - wr.Left, Bottom = vf.Bottom - wr.Top };
                    var pr = new NativeMethods.Rect
                    {
                        Left = Math.Max(clip.Left, (int)Math.Round(r.Left) - wr.Left),
                        Top = Math.Max(clip.Top, (int)Math.Round(r.Top) - wr.Top),
                        Right = Math.Min(clip.Right, (int)Math.Round(r.Right) - wr.Left),
                        Bottom = Math.Min(clip.Bottom, (int)Math.Round(r.Bottom) - wr.Top),
                    };
                    if (pr.Width >= 80 && pr.Height >= 45) rel = pr;
                }
                bool same = _playerRelHwnd == hwnd && Nullable.Equals(rel, _playerRel);
                _playerRel = rel;
                _playerRelHwnd = hwnd;
                if (!same) ApplyMediaPreviewCrop();
            }, DispatcherPriority.Background);
        }, TaskScheduler.Default);
    }

    /// <summary>
    /// rcSource = content rect (player, or visible frame) in DWM source coordinates, center-trimmed to the slot's
    /// aspect so the destination (the whole slot) is covered edge to edge with no bars and no stretching.
    /// </summary>
    private void ApplyMediaPreviewCrop()
    {
        try
        {
            if (!_mediaPreviewShowingLive || !MediaPreviewThumb.HasThumbnail) return;
            var src = MediaPreviewThumb.SourceHwnd;
            double hw = MediaPreviewThumb.ActualWidth, hh = MediaPreviewThumb.ActualHeight;
            if (hw < 2 || hh < 2) return;
            if (!NativeMethods.GetWindowRect(src, out var wr) || wr.Width < 2 || wr.Height < 2) return;
            if (!MediaPreviewThumb.TryGetSourceSize(out int sw, out int sh)) { sw = wr.Width; sh = wr.Height; }
            double sx = (double)sw / wr.Width, sy = (double)sh / wr.Height;

            NativeMethods.Rect b;
            if (_mediaPreviewMode == MediaPreviewMode.Window && _playerRelHwnd == src && _playerRel is { } pr) b = pr;
            else if (NativeMethods.TryGetVisibleFrame(src, out var vf) && !vf.IsEmpty)
                b = new NativeMethods.Rect
                {
                    Left = Math.Max(0, vf.Left - wr.Left), Top = Math.Max(0, vf.Top - wr.Top),
                    Right = Math.Min(wr.Width, vf.Right - wr.Left), Bottom = Math.Min(wr.Height, vf.Bottom - wr.Top),
                };
            else b = new NativeMethods.Rect { Left = 0, Top = 0, Right = wr.Width, Bottom = wr.Height };
            if (b.IsEmpty) b = new NativeMethods.Rect { Left = 0, Top = 0, Right = wr.Width, Bottom = wr.Height };

            double l = b.Left * sx, t = b.Top * sy, w = b.Width * sx, h = b.Height * sy;
            double target = hw / hh;
            if (w / h > target) { double nw = h * target; l += (w - nw) / 2; w = nw; }
            else { double nh = w / target; t += (h - nh) / 2; h = nh; }
            var crop = new NativeMethods.Rect
            {
                Left = Math.Clamp((int)Math.Round(l), 0, sw - 1),
                Top = Math.Clamp((int)Math.Round(t), 0, sh - 1),
            };
            crop.Right = Math.Clamp((int)Math.Round(l + w), crop.Left + 2, sw);
            crop.Bottom = Math.Clamp((int)Math.Round(t + h), crop.Top + 2, sh);
            if (MediaPreviewThumb.SourceCrop is { } cur && cur.Left == crop.Left && cur.Top == crop.Top
                && cur.Right == crop.Right && cur.Bottom == crop.Bottom)
                return;
            MediaPreviewThumb.SourceCrop = crop;
        }
        catch { }
    }

    // ---------------------------------------------------------------- click / menu

    /// <summary>
    /// v1.1.1: a context menu (Media preview / Media header) took activation from the desk; Dashboard_Deactivated
    /// then saw a foreground that wasn't a Linked tile and yielded the tiles' z-order (they dropped under other
    /// apps = "linked windows disappear"). Same 'chrome popup' quiet window as the color menu while the menu is
    /// open, and Linked tiles are re-asserted when it closes.
    /// </summary>
    private void ArmMediaMenuQuiet(ContextMenu? menu)
    {
        _chromePopupQuietUntilUtc = DateTime.UtcNow.AddSeconds(4);
        _suppressLiveRetileUntilUtc = DateTime.UtcNow.AddMilliseconds(500);
        if (menu == null || _mediaMenusHooked.Contains(menu)) return;
        _mediaMenusHooked.Add(menu);
        menu.Closed += (_, _) =>
        {
            _chromePopupQuietUntilUtc = DateTime.UtcNow.AddMilliseconds(600);
            Dispatcher.BeginInvoke(() =>
            {
                // v1.1.2 review: a menu item that brought ANOTHER app forward ("Bring to front" / "Switch to the
                // media tab") ran inside the quiet window, so Dashboard_Deactivated skipped its yield and the
                // TOPMOST tiles stayed over that app. End the quiet and run the normal deactivation now.
                var fg = NativeMethods.GetForegroundWindow();
                NativeMethods.GetWindowThreadProcessId(fg, out uint fgPid);
                if (!IsActive && fg != IntPtr.Zero && fgPid != (uint)Environment.ProcessId && !IsAssignedLiveHwnd(fg))
                {
                    _chromePopupQuietUntilUtc = DateTime.MinValue;
                    Dashboard_Deactivated(this, EventArgs.Empty);
                    return;
                }
                PreserveLiveTilesAfterMediaAction(restoreDeskFocus: false);
            }, DispatcherPriority.Background);
        };
    }
    private readonly HashSet<ContextMenu> _mediaMenusHooked = new();
    private void MediaHeaderMenu_Opened(object sender, RoutedEventArgs e) => ArmMediaMenuQuiet(sender as ContextMenu);

    private void MediaPreview_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        var mode = _mediaPreviewMode;
        IntPtr main = _mediaBoundHwnd != IntPtr.Zero && NativeMethods.IsWindow(_mediaBoundHwnd) ? _mediaBoundHwnd : _mediaPreviewHwnd;
        if (main == IntPtr.Zero || !NativeMethods.IsWindow(main)) { StatusText.Text = "Media window not found"; return; }
        try
        {
            if (mode == MediaPreviewMode.PopOut && _popOutParked && _mediaPreviewHwnd == _popOutHwnd
                && string.IsNullOrWhiteSpace(_media.NowPlayingLabel))
            {
                // No session to switch to: bring the pop-out back on screen (re-parked once Tim leaves it).
                var pipH = _popOutHwnd;
                UnparkPopOut();
                NativeMethods.ForceForegroundWindow(pipH);
                _pipNextTryUtc = DateTime.UtcNow.AddSeconds(5);
                StatusText.Text = "Pop-out video shown - it tucks away again when you leave it";
                return;
            }
            if (mode == MediaPreviewMode.BackgroundTab || (mode == MediaPreviewMode.PopOut && _popOutParked))
            {
                string frag = _mediaBoundFrag;
                StatusText.Text = "Switching to the media tab...";
                Task.Run(() => TrySelectBrowserTab(main, frag)).ContinueWith(t => Dispatcher.BeginInvoke(() =>
                {
                    bool ok = t.Status == TaskStatus.RanToCompletion && t.Result;
                    if (NativeMethods.IsIconic(main)) NativeMethods.ShowWindow(main, NativeMethods.SW_RESTORE);
                    NativeMethods.ForceForegroundWindow(main);
                    StatusText.Text = ok ? "Switched to the media tab: " + (TxtMediaNow?.Text ?? "")
                        : "Brought the browser forward (couldn't select the tab - tip: give YouTube its own window)";
                    ScheduleNowPlayingRefresh();
                }), TaskScheduler.Default);
                return;
            }
            var w = _tiler.WorkingSet.FirstOrDefault(x => x.Hwnd == main && !x.Parked);
            if (w != null && (w.Collapsed || w.Floating) && w.ZoneIndex >= 0)
                ExpandCollapsedPane(w.ZoneIndex);
            else
            {
                if (NativeMethods.IsIconic(main)) NativeMethods.ShowWindow(main, NativeMethods.SW_RESTORE);
                NativeMethods.ForceForegroundWindow(main);
            }
            StatusText.Text = "Brought media window forward: " + (TxtMediaNow?.Text ?? "");
            ScheduleNowPlayingRefresh();
        }
        catch { }
    }

    /// <summary>UIA: select the browser tab whose name carries the media title (no synthetic input).</summary>
    private static bool TrySelectBrowserTab(IntPtr hwnd, string frag)
    {
        if (frag.Length < 3) return false;
        try
        {
            var root = AutomationElement.FromHandle(hwnd);
            var tabs = root.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem));
            foreach (AutomationElement t in tabs)
            {
                string n;
                try { n = t.Current.Name ?? ""; } catch { continue; }
                if (n.IndexOf(frag, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (t.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var sp)) { ((SelectionItemPattern)sp).Select(); return true; }
                if (t.TryGetCurrentPattern(InvokePattern.Pattern, out var ip)) { ((InvokePattern)ip).Invoke(); return true; }
            }
        }
        catch { }
        return false;
    }

    private void MediaPreviewMenu_Opened(object sender, RoutedEventArgs e)
    {
        ArmMediaMenuQuiet(MediaPreviewMenu);
        var menu = MediaPreviewMenu;
        menu.Items.Clear();
        IntPtr src = _mediaBoundHwnd != IntPtr.Zero && NativeMethods.IsWindow(_mediaBoundHwnd) ? _mediaBoundHwnd : _mediaPreviewHwnd;
        var front = new MenuItem { Header = _mediaPreviewMode == MediaPreviewMode.BackgroundTab ? "Switch to the media tab" : "Bring to front" };
        front.Click += (_, _) => MediaPreview_Click(this, new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left));
        menu.Items.Add(front);

        var hidePip = new MenuItem
        {
            Header = "Hide browser pop-out video",
            IsCheckable = true,
            IsChecked = _settings.HideBrowserPopOut,
            ToolTip = "When the browser pops the video out (tab in the background), keep that window out of sight\n"
                    + "and show it here instead. Opera: Settings (Alt+P) > search 'video pop out' > turn off automatic pop-out.",
        };
        hidePip.Click += (_, _) =>
        {
            _settings.HideBrowserPopOut = hidePip.IsChecked;
            try { Persist(); } catch { }
            if (!_settings.HideBrowserPopOut) UnparkPopOut();
            StatusText.Text = _settings.HideBrowserPopOut ? "Browser pop-out video will be hidden and shown in the Media preview" : "Browser pop-out video left visible";
            ScheduleNowPlayingRefresh();
        };

        if (src == IntPtr.Zero || !NativeMethods.IsWindow(src))
        {
            front.IsEnabled = false;
            menu.Items.Add(new Separator());
            menu.Items.Add(hidePip);
            return;
        }
        menu.Items.Add(new Separator());
        var linked = _tiler.WorkingSet.FirstOrDefault(x => x.Hwnd == src && !x.Parked);
        if (linked != null)
            menu.Items.Add(new MenuItem { Header = $"Linked to pane {VisualPaneNumber(linked.ZoneIndex)}", IsEnabled = false });
        else
            for (int v = 1; v <= _paneCount; v++)
            {
                int vn = v;
                var mi = new MenuItem { Header = $"Link to pane {vn}" };
                mi.Click += (_, _) =>
                {
                    var entry = EntryFromHwnd(src);
                    if (entry == null) return;
                    AssignToPane(entry, ZoneIndexFromVisual(vn), swapIfOccupied: false);
                    SyncLinkedFlagsInList();
                    ScheduleNowPlayingRefresh();
                };
                menu.Items.Add(mi);
            }
        menu.Items.Add(new Separator());
        menu.Items.Add(hidePip);
    }
}
