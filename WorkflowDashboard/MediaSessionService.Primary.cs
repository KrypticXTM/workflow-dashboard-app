using System.Diagnostics;
using System.Windows.Media;
using NAudio.CoreAudioApi;
using Windows.Media.Control;

namespace WorkflowDashboard;

/// <summary>
/// v1.0.2 primary-source selection (Tim 9:17-9:19 PM): the Media section locks onto the PLAYING source and
/// never mirrors what Tim clicks. Rules:
///  - The primary only changes when (a) another session plays continuously for TakeoverPlaySec while the
///    primary is still playing, (b) the primary has been paused/stopped for PrimaryIdleSec and another session
///    has played for MinTakeoverPlaySec, (c) Tim picks one in the switcher, or the primary closes (-> the most
///    recent long-playing session).
///  - Ignored for takeover (still listed in the switcher): muted / silent sessions (no audio peak on the app's
///    audio sessions within AudibleWindowSec) and feed-site clips (X, Instagram, TikTok, Facebook) with no or a
///    short duration (&lt;= FeedClipMaxSec).
///  - Pin (Media header toggle) = manual lock: no auto-takeover until unpinned or the pinned source closes.
///  - Browsers expose ONE session per process (Opera switches its single session between tabs), so the same
///    gate is applied to content changes inside the shown session: a new title only replaces the shown one
///    under the same rules (else the previous now-playing snapshot is held, for up to HeldContentMaxSec).
/// </summary>
internal sealed partial class MediaSessionService
{
    public const double TakeoverPlaySec = 8;
    // v1.0.2: 5 -> 2 s (Tim: pause Spotify -> YouTube should take ~2 s). Still longer than a Spotify gapless /
    // buffering gap (<1 s), and same-source track changes bypass the gate anyway (IsSameSourceTrackChange).
    public const double PrimaryIdleSec = 2;
    public const double MinTakeoverPlaySec = 1.5;
    // v1.0.2 (11:13 PM, Tim): UNLOCKED = follow whatever newly starts playing, after only this debounce
    // (no TakeoverPlaySec / PrimaryIdleSec hold). The lock is the way to stay on a source.
    public const double UnlockedSwitchSec = 0.5;
    // Same YouTube tab, title that isn't the tab's video (an ad the watcher hasn't confirmed yet): wait this long.
    public const double SameTabAdGraceSec = 2.0;
    public const double AudibleWindowSec = 3;
    public const double FeedClipMaxSec = 90;
    public const double HeldContentMaxSec = 600;
    private const float AudiblePeak = 0.0005f;

    private sealed class SesState
    {
        public string Status = "";
        public DateTime? PlayStart;          // start of the current continuous Playing run
        public DateTime NotPlayingSince = DateTime.UtcNow;
    }
    private readonly Dictionary<GlobalSystemMediaTransportControlsSession, SesState> _ses = new();
    private GlobalSystemMediaTransportControlsSession? _primary;
    private LockRef? _manualLock;
    private readonly Dictionary<string, DateTime> _audibleUtc = new(StringComparer.OrdinalIgnoreCase);
    private bool _peaksAvailable;

    /// <summary>When set in the future, the UI schedules one extra refresh then (idle hand-off deadline).</summary>
    public DateTime DecisionDueUtc { get; private set; }

    /// <summary>Pinned (manual lock) - switcher pick or the Media pin toggle.</summary>
    public bool IsPinned => _manualLock != null || _audioLock != null;

    /// <summary>
    /// Lock on an audio-fallback source (browser with no SMTC session, e.g. Opera): the browser process + the
    /// window the card shows. Held until unlocked or that window closes.
    /// </summary>
    private (string Proc, IntPtr Hwnd)? _audioLock;

    public double NowPlayingPositionSec { get; private set; }
    public double NowPlayingDurationSec { get; private set; }
    public DateTime NowPlayingTimelineAtUtc { get; private set; }
    public string NowPlayingSite { get; private set; } = "";

    public static bool IsFeedSite(string site) => site is "X" or "Instagram" or "TikTok" or "Facebook";
    public static bool IsAudioSite(string site) =>
        site is "Spotify" or "YouTube Music" or "SoundCloud" or "Apple Music" or "Deezer" or "Tidal" or "Pandora";

    /// <summary>Pin / unpin the shown source.</summary>
    public void SetPinned(bool on)
    {
        if (!on) { _manualLock = null; _audioLock = null; return; }
        // The card shows an audio-fallback source (Opera without an SMTC session, or a stale paused session
        // superseded by browser audio): lock THAT, not SelectedEntry (which is absent or a different app).
        if (NowPlayingFromAudio && NowPlayingHwnd != IntPtr.Zero && NativeMethods.IsWindow(NowPlayingHwnd) && NowPlayingApp.Length > 0)
        {
            _manualLock = null;
            _audioLock = (NowPlayingApp, NowPlayingHwnd);
            return;
        }
        _audioLock = null;
        var e = SelectedEntry;
        if (e == null) return;
        _manualLock = MakeLock(e);
        _primary = e.Session;
    }

    private LockRef MakeLock(SmtcSessionEntry e) => new()
    {
        Session = e.Session, Aumid = e.Aumid, Title = e.Title,
        SameAumidCount = AllSessions.Count(x => x.Aumid == e.Aumid),
    };

    /// <summary>Audio peak per process name (1.2 s tick). Muted sessions count as silent.</summary>
    private void SampleAudioPeaks()
    {
        if (_device == null) return;
        lock (_gate)
        {
            try
            {
                _device.AudioSessionManager.RefreshSessions();
                var mgr = _device.AudioSessionManager.Sessions;
                for (int i = 0; i < mgr.Count; i++)
                {
                    try
                    {
                        var s = mgr[i];
                        uint pid = s.GetProcessID;
                        if (pid == 0) continue;
                        float peak = s.AudioMeterInformation.MasterPeakValue;
                        _peaksAvailable = true;
                        if (peak < AudiblePeak) continue;
                        try { if (s.SimpleAudioVolume.Mute) continue; } catch { }
                        using var p = Process.GetProcessById((int)pid);
                        _audibleUtc[p.ProcessName] = DateTime.UtcNow;
                    }
                    catch { }
                }
            }
            catch { }
        }
    }

    private bool IsAudible(string proc)
    {
        if (!_peaksAvailable || string.IsNullOrEmpty(proc)) return true;   // can't tell -> don't penalize
        return _audibleUtc.TryGetValue(proc, out var t) && (DateTime.UtcNow - t).TotalSeconds <= AudibleWindowSec;
    }

    private bool IgnoredForTakeover(string proc, string site, double durationSec)
        => (IsFeedSite(site) && (durationSec <= 0 || durationSec <= FeedClipMaxSec)) || !IsAudible(proc);

    private SesState Track(GlobalSystemMediaTransportControlsSession s, string status)
    {
        if (!_ses.TryGetValue(s, out var st)) { st = new SesState(); _ses[s] = st; }
        var now = DateTime.UtcNow;
        if (status == "Playing") { if (st.Status != "Playing" || st.PlayStart == null) st.PlayStart = now; }
        else
        {
            if (st.Status == "Playing" || st.PlayStart != null)
            {
                st.NotPlayingSince = now;
                // Re-evaluate exactly when the idle hand-off becomes possible (not on the next 1.2 s tick).
                DecisionDueUtc = now.AddSeconds(UnlockedSwitchSec + 0.15);
            }
            st.PlayStart = null;
        }
        st.Status = status;
        return st;
    }

    private double PlayFor(SmtcSessionEntry e) =>
        _ses.TryGetValue(e.Session, out var st) && st.PlayStart is DateTime t ? (DateTime.UtcNow - t).TotalSeconds : 0;
    private double IdleFor(SmtcSessionEntry e) =>
        _ses.TryGetValue(e.Session, out var st) && st.PlayStart == null ? (DateTime.UtcNow - st.NotPlayingSince).TotalSeconds : 0;
    private DateTime StartOf(SmtcSessionEntry e) =>
        _ses.TryGetValue(e.Session, out var st) && st.PlayStart is DateTime t ? t : DateTime.MinValue;

    private SmtcSessionEntry? SelectPrimary(List<SmtcSessionEntry> list, GlobalSystemMediaTransportControlsSession? current)
    {
        // forget sessions that are gone
        foreach (var k in _ses.Keys.Where(k => !list.Any(e => ReferenceEquals(e.Session, k))).ToList()) _ses.Remove(k);

        if (_manualLock != null)
        {
            var locked = FindLock(list, _manualLock);
            if (locked != null && !locked.IsEnded)
            {
                _manualLock.Session = locked.Session;
                if (locked.Title != "") _manualLock.Title = locked.Title;
                _primary = locked.Session;
                return locked;
            }
            _manualLock = null;   // pinned source closed
        }

        bool Eligible(SmtcSessionEntry e) => e.IsPlaying && !IgnoredForTakeover(e.ProcName, e.Site, e.DurationSec);
        var prim = _primary == null ? null : list.FirstOrDefault(e => ReferenceEquals(e.Session, _primary));
        SmtcSessionEntry? sel;
        if (prim == null || prim.IsEnded)
        {
            sel = list.Where(e => Eligible(e) && PlayFor(e) >= UnlockedSwitchSec).OrderByDescending(StartOf).FirstOrDefault()
               ?? list.Where(Eligible).OrderByDescending(StartOf).ThenByDescending(e => ReferenceEquals(e.Session, current)).FirstOrDefault()
               ?? list.Where(e => e.IsPlaying).OrderByDescending(e => ReferenceEquals(e.Session, current)).FirstOrDefault()
               ?? (prim != null && !prim.IsEnded ? prim : null)
               ?? list.FirstOrDefault(e => ReferenceEquals(e.Session, current) && !e.IsEnded)
               ?? list.FirstOrDefault(e => !e.IsEnded)
               ?? list.FirstOrDefault();
        }
        else
        {
            sel = prim;
            var primStart = StartOf(prim);
            foreach (var c in list.Where(e => !ReferenceEquals(e.Session, prim.Session) && Eligible(e)).OrderByDescending(StartOf))
            {
                // Unlocked: the newest source that started playing wins after the short debounce (the primary
                // being paused, or having started earlier, is enough). Muted autoplay / feed clips are not Eligible.
                if (prim.IsPlaying && StartOf(c) <= primStart) continue;
                if (PlayFor(c) >= UnlockedSwitchSec) { sel = c; break; }
                if (StartOf(c) > DateTime.MinValue)
                {
                    var due = StartOf(c).AddSeconds(UnlockedSwitchSec + 0.15);   // re-check once it qualifies
                    if (due > DateTime.UtcNow) DecisionDueUtc = due;
                }
            }
        }
        _primary = sel?.Session;
        return sel;
    }

    // ---------------------------------------------------------------- same-session content gate

    private sealed class NpSnap
    {
        public GlobalSystemMediaTransportControlsSession? Session;
        public string Title = "", FullTitle = "", App = "", Label = "", Tooltip = "", Status = "", Artist = "", Site = "";
        public ImageSource? Icon, Art;
        public IntPtr Hwnd;
        public double Pos, Dur;
        public DateTime TlAt;
        public DateTime LastPlayingUtc, AcceptedUtc;
    }
    private NpSnap? _shown;
    private string _pendTitle = "";
    private DateTime _pendSinceUtc;
    private DateTime? _heldSinceUtc;

    private NpSnap Capture(GlobalSystemMediaTransportControlsSession s) => new()
    {
        Session = s, Title = NowPlayingTitle, FullTitle = NowPlayingFullTitle, App = NowPlayingApp, Label = NowPlayingLabel, Tooltip = NowPlayingTooltip,
        Status = NowPlayingStatus, Artist = NowPlayingArtist, Site = NowPlayingSite, Icon = NowPlayingIcon,
        Art = NowPlayingArt, Hwnd = NowPlayingHwnd, Pos = NowPlayingPositionSec, Dur = NowPlayingDurationSec,
        TlAt = NowPlayingTimelineAtUtc,
    };

    private void Restore(NpSnap s)
    {
        NowPlayingTitle = s.Title; NowPlayingFullTitle = s.FullTitle; NowPlayingApp = s.App; NowPlayingLabel = s.Label; NowPlayingTooltip = s.Tooltip;
        NowPlayingStatus = s.Status; NowPlayingArtist = s.Artist; NowPlayingSite = s.Site; NowPlayingIcon = s.Icon;
        NowPlayingArt = s.Art; NowPlayingHwnd = s.Hwnd; NowPlayingPositionSec = s.Pos; NowPlayingDurationSec = s.Dur;
        NowPlayingTimelineAtUtc = s.TlAt;
    }

    /// <summary>
    /// Called after the NowPlaying* fields were filled for <paramref name="session"/>. A new title inside the SAME
    /// session (Opera tab switch, X reel, ad) only replaces the shown content under the takeover rules; otherwise
    /// the previous snapshot is restored. A different session (chosen by SelectPrimary) is always accepted.
    /// </summary>
    private bool _npSiteGuessed;

    private static string WindowTitleOf(IntPtr hwnd)
    {
        int len = NativeMethods.GetWindowTextLength(hwnd);
        if (len <= 0) return "";
        var sb = new System.Text.StringBuilder(len + 1);
        NativeMethods.GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    /// <summary>The (cleaned) media title appears in the window title (first 24 chars, case-insensitive).</summary>
    private static bool TitleInWindow(string title, string win)
    {
        string t = TitleSanitizer.Clean(title ?? "").TrimEnd('\u2026', '.').Trim();
        if (t.Length > 24) t = t[..24];
        return t.Length >= 3 && win.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private bool IsSameSourceTrackChange(string procName)
    {
        if (_shown == null) return false;
        if (NowPlayingSite.Length > 0)
            return !_npSiteGuessed && NowPlayingSite == _shown.Site && IsAudioSite(NowPlayingSite);
        return _shown.Site.Length == 0 && procName.Length > 0 && !BrowserProcNames.Contains(procName);
    }

    private void ApplyContentGate(GlobalSystemMediaTransportControlsSession session, string procName)
    {
        var now = DateTime.UtcNow;
        bool playing = NowPlayingStatus == "Playing";
        void Accept()
        {
            var snap = Capture(session);
            snap.AcceptedUtc = now;
            snap.LastPlayingUtc = playing ? now : (_shown != null && ReferenceEquals(_shown.Session, session) ? _shown.LastPlayingUtc : DateTime.MinValue);
            _shown = snap;
            _pendTitle = "";
            _heldSinceUtc = null;
        }
        // YouTube ads: the tab (window) title keeps naming the video while SMTC reports the ad. An ad never
        // replaces the video on the card, and when the ad ends the video shows again at once (it used to be held
        // TakeoverPlaySec ~8 s as "new content" in the same session). Same tab/window only.
        if (BrowserProcNames.Contains(procName) && NowPlayingHwnd != IntPtr.Zero && NativeMethods.IsWindow(NowPlayingHwnd))
        {
            string win = WindowTitleOf(NowPlayingHwnd);
            if (SkipAdService.IsYouTubeTitle(win))
            {
                bool newInWin = TitleInWindow(NowPlayingFullTitle, win);
                if (!newInWin && SkipAdService.IsAdShowing(NowPlayingHwnd))
                {
                    string st = NowPlayingStatus;
                    if (_shown != null && _shown.Hwnd == NowPlayingHwnd && TitleInWindow(_shown.FullTitle, win))
                        Restore(_shown);                       // keep the movie (title, art, timeline)
                    else
                    {
                        string vid = TitleSanitizer.Clean(MediaSiteDetector.Strip(CleanWindowTitle(win, procName), "YouTube"));
                        if (vid.Length > 0)
                        {
                            NowPlayingFullTitle = vid;
                            NowPlayingTitle = vid.Length > 48 ? vid[..45] + "\u2026" : vid;
                            NowPlayingLabel = "YouTube - " + (vid.Length > 60 ? vid[..57] + "..." : vid);
                            NowPlayingSite = "YouTube";
                            NowPlayingArtist = "";
                            NowPlayingArt = null;
                            NowPlayingPositionSec = 0; NowPlayingDurationSec = 0;
                        }
                    }
                    NowPlayingStatus = st;
                    newInWin = true;
                }
                if (newInWin && _shown != null && ReferenceEquals(_shown.Session, session) && _shown.Hwnd == NowPlayingHwnd
                    && NowPlayingTitle != _shown.Title)
                {
                    Accept();                                  // the tab shows this video now (ad over / next video)
                    return;
                }
            }
        }
        if (_shown == null || !ReferenceEquals(_shown.Session, session) || NowPlayingTitle == _shown.Title || _shown.Title.Length == 0)
        {
            Accept();
            return;
        }
        // Content changed inside the shown session.
        if (_pendTitle != NowPlayingTitle || !playing) { _pendTitle = playing ? NowPlayingTitle : ""; _pendSinceUtc = now; }
        double pendFor = playing && _pendTitle == NowPlayingTitle ? (now - _pendSinceUtc).TotalSeconds : 0;
        bool ignored = IgnoredForTakeover(procName, NowPlayingSite, NowPlayingDurationSec);
        // Unlocked debounce: 0.5 s; a same-YouTube-tab title that isn't the tab's video (likely an ad the watcher
        // hasn't confirmed yet) gets SameTabAdGraceSec so it never flashes onto the card.
        double need = UnlockedSwitchSec;
        if (NowPlayingHwnd != IntPtr.Zero && NowPlayingHwnd == _shown.Hwnd && BrowserProcNames.Contains(procName))
        {
            string w = WindowTitleOf(NowPlayingHwnd);
            if (SkipAdService.IsYouTubeTitle(w) && TitleInWindow(_shown.FullTitle, w) && !TitleInWindow(NowPlayingFullTitle, w))
                need = SameTabAdGraceSec;
        }
        _heldSinceUtc ??= now;
        bool stale = (now - _heldSinceUtc.Value).TotalSeconds >= HeldContentMaxSec;
        bool take;
        if (IsPinned)
            take = NowPlayingSite == _shown.Site;                 // pinned: next track / video of the same source only
        else
            take = stale || (!ignored && playing && pendFor >= need);
        if (IsPinned && !take && NowPlayingSite.Length == 0 && _shown.Site.Length == 0) take = true;   // native app track change
        // Next track on the SAME source shows at once (Spotify song change was held TakeoverPlaySec ~10 s): a
        // music site verified for the new title (not the single-tab guess) matching the shown one, or a native
        // app's own session. X reels / feed clips / YouTube ads keep the full takeover rules.
        if (!take && IsSameSourceTrackChange(procName)) take = true;
        if (take) { Accept(); return; }
        // Hold what was playing. It can't be observed any more (the session now reports the other tab), so a
        // held "Playing" counts as still playing: the newcomer needs the full TakeoverPlaySec, not PrimaryIdleSec.
        if (_shown.Status == "Playing") _shown.LastPlayingUtc = now;
        if (!ignored && playing && !IsPinned)
        {
            var due = _pendSinceUtc.AddSeconds(need + 0.15);           // switch as soon as the debounce passes
            if (due > now) DecisionDueUtc = due;
        }
        Restore(_shown);
    }
}
