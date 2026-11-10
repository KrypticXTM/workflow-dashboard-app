using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Automation;
using NAudio.CoreAudioApi;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Media.Control;

namespace WorkflowDashboard;

internal sealed class MediaSessionItem : INotifyPropertyChanged
{
    public uint ProcessId { get; init; }
    public string ProcessName { get; init; } = "";
    public string DisplayName { get; init; } = "";
    private string _paneLabel = "";
    public string PaneLabel
    {
        get => _paneLabel;
        set
        {
            if (_paneLabel == value) return;
            _paneLabel = value ?? "";
            OnPropertyChanged();
            OnPropertyChanged(nameof(Label));
        }
    }
    public string Label
    {
        get
        {
            string baseName = MediaSessionService.FriendlyAppName(ProcessName, ProcessId);
            return string.IsNullOrWhiteSpace(PaneLabel) ? baseName : (PaneLabel + " | " + baseName);
        }
    }

    private bool _isMuted;
    public bool IsMuted
    {
        get => _isMuted;
        set { if (_isMuted != value) { _isMuted = value; OnPropertyChanged(); OnPropertyChanged(nameof(MuteLabel)); } }
    }

    public string MuteLabel => IsMuted ? "Unmute" : "Mute";

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// Per-app mute list (Core Audio via NAudio) plus compact transport via GSMTC
/// and master endpoint volume. Skip Ad is YouTube/browser biased.
/// </summary>
/// <summary>v1.0.2: one SMTC session (Chromium exposes one per playing tab, so several can share an AUMID).</summary>
internal sealed class SmtcSessionEntry
{
    public GlobalSystemMediaTransportControlsSession Session { get; init; } = null!;
    public string Aumid { get; init; } = "";
    /// <summary>Best-guess process name from the AUMID ("opera", "spotify", ...); may be "" for hashed AUMIDs.</summary>
    public string ProcName { get; init; } = "";
    public string FriendlyApp { get; init; } = "";
    public string Title { get; set; } = "";
    public string Artist { get; init; } = "";
    /// <summary>Media site for browser sessions ("YouTube", "Twitch", ...); "" = unknown / native app.</summary>
    public string Site { get; set; } = "";
    /// <summary>Site came from the "only music tab open" guess, not a verified title/tab match (label only).</summary>
    public bool SiteGuessed { get; set; }
    public bool IsBrowserHosted { get; init; }
    public string DisplayApp => Site.Length > 0 ? Site : FriendlyApp;
    /// <summary>" in Opera" for browser-hosted sessions (tooltips only).</summary>
    public string HostSuffix => IsBrowserHosted ? " in " + FriendlyApp : "";
    public string Status { get; init; } = "";
    public int Ordinal { get; init; }
    public bool IsPlaying => Status == "Playing";
    public bool IsEnded => Status == "Stopped" || Status == "Closed";
    public string Label => string.IsNullOrWhiteSpace(Title) ? DisplayApp : DisplayApp + " - " + Title;
    /// <summary>SMTC timeline (0 = unknown).</summary>
    public double DurationSec { get; init; }
}

internal sealed partial class MediaSessionService : IDisposable
{
    private MMDeviceEnumerator? _enumerator;
    private MMDevice? _device;
    private readonly object _gate = new();
    private GlobalSystemMediaTransportControlsSessionManager? _gsmtc;
    private bool _gsmtcTried;

    public ObservableCollection<MediaSessionItem> Sessions { get; } = new();

    /// <summary>Last session we unmuted (or last active non-muted), used as hotkey primary.</summary>
    public uint PrimaryProcessId { get; private set; }

    public ImageSource? NowPlayingIcon { get; private set; }
    public uint NowPlayingProcessId { get; private set; }
    public string NowPlayingApp { get; private set; } = "";
    public string NowPlayingTitle { get; private set; } = "";
    /// <summary>v1.0.2: untruncated media title for the now-playing card (NowPlayingTitle is capped at 48).</summary>
    public string NowPlayingFullTitle { get; private set; } = "";
    /// <summary>Friendly, sanitized label for the media panel: "Opera - Home / X". Never raw AUMIDs or mojibake.</summary>
    public string NowPlayingLabel { get; private set; } = "";
    /// <summary>Multi-line tooltip: friendly app, window title, media title, process.</summary>
    public string NowPlayingTooltip { get; private set; } = "";
    /// <summary>v1.0.2 media preview: top-level window resolved for the current session (may be IntPtr.Zero).</summary>
    public IntPtr NowPlayingHwnd { get; private set; }
    /// <summary>Album art / video thumbnail from SMTC (fallback when the source window is minimized).</summary>
    public ImageSource? NowPlayingArt { get; private set; }
    /// <summary>"Playing", "Paused", "Stopped", ... from SMTC playback info ("" = unknown / no session).</summary>
    public string NowPlayingStatus { get; private set; } = "";
    public string NowPlayingArtist { get; private set; } = "";
    /// <summary>Raised (on a thread-pool thread) when SMTC reports a session / track / playback change.</summary>
    public event Action? NowPlayingChanged;
    private GlobalSystemMediaTransportControlsSession? _evSession;
    /// <summary>All SMTC sessions from the last refresh (switcher + click-to-lock).</summary>
    public IReadOnlyList<SmtcSessionEntry> AllSessions { get; private set; } = Array.Empty<SmtcSessionEntry>();
    /// <summary>The session shown in the Media section (locked / fallback); transport buttons act on it.</summary>
    public SmtcSessionEntry? SelectedEntry { get; private set; }

    // Last successful seek we issued (same-session only). Providers often keep returning a pre-seek
    // Position until LastUpdatedTime advances; rapid −10s clicks must not re-base on that stale snapshot.
    private GlobalSystemMediaTransportControlsSession? _pendingSeekSession;
    private DateTime _pendingSeekIssuedUtc = DateTime.MinValue;
    private double _pendingSeekTargetSec;

    public int SelectedIndex => SelectedEntry == null ? -1 : IndexOfEntry(AllSessions, SelectedEntry);

    private sealed class LockRef
    {
        public GlobalSystemMediaTransportControlsSession? Session;
        public string Aumid = "";
        public string Title = "";
        public int SameAumidCount;
    }
    /// <summary>Click-to-lock history, most recent first (in memory only; nothing persisted).</summary>
    private readonly List<LockRef> _locks = new();
    public string NowPlayingSubtitle =>
        string.IsNullOrWhiteSpace(NowPlayingTitle) ? NowPlayingApp
        : (string.IsNullOrWhiteSpace(NowPlayingApp) ? NowPlayingTitle : $"{NowPlayingApp} — {NowPlayingTitle}");

    public MediaSessionService()
    {
        try
        {
            _enumerator = new MMDeviceEnumerator();
            _device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        }
        catch
        {
            _device = null;
        }
        // Default output can change (Tim switched headphones -> volume kept driving the old endpoint
        // until restart). Watch for default-device changes and re-acquire lazily under _gate.
        try
        {
            if (_enumerator != null)
            {
                _deviceWatcher = new DefaultDeviceWatcher(this);
                _enumerator.RegisterEndpointNotificationCallback(_deviceWatcher);
            }
        }
        catch { _deviceWatcher = null; }
    }

    private volatile bool _deviceDirty;
    private DefaultDeviceWatcher? _deviceWatcher;
    private DateTime _lastDeviceCheckUtc = DateTime.MinValue;

    /// <summary>IMMNotificationClient: only flags the device stale (callbacks run on a COM worker thread).</summary>
    private sealed class DefaultDeviceWatcher : NAudio.CoreAudioApi.Interfaces.IMMNotificationClient
    {
        private readonly MediaSessionService _owner;
        public DefaultDeviceWatcher(MediaSessionService owner) => _owner = owner;
        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
        { if (flow == DataFlow.Render) _owner._deviceDirty = true; }
        public void OnDeviceStateChanged(string deviceId, DeviceState newState) => _owner._deviceDirty = true;
        public void OnDeviceAdded(string pwstrDeviceId) { }
        public void OnDeviceRemoved(string deviceId) => _owner._deviceDirty = true;
        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }
    }

    /// <summary>
    /// Make <see cref="_device"/> the current default render endpoint. Cheap when nothing changed:
    /// re-queries only when flagged by the notification client, when forced (a volume call failed),
    /// or at most every 5 s as a safety net. Callers hold <see cref="_gate"/> (all _device users do).
    /// </summary>
    private void EnsureCurrentDeviceLocked(bool force = false)
    {
        if (_enumerator == null) return;
        var now = DateTime.UtcNow;
        if (!force && !_deviceDirty && _device != null && (now - _lastDeviceCheckUtc).TotalSeconds < 5) return;
        _lastDeviceCheckUtc = now;
        _deviceDirty = false;
        try
        {
            var cur = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            string? oldId = null;
            try { oldId = _device?.ID; } catch { }
            if (_device != null && oldId != null && string.Equals(oldId, cur.ID, StringComparison.OrdinalIgnoreCase))
            {
                cur.Dispose();
                return;
            }
            var old = _device;
            _device = cur;
            try { old?.Dispose(); } catch { }
        }
        catch { /* no render device right now: keep the old reference; next call retries */ }
    }

    private void EnsureCurrentDevice(bool force = false)
    {
        lock (_gate) EnsureCurrentDeviceLocked(force);
    }

    private async Task<GlobalSystemMediaTransportControlsSessionManager?> EnsureGsmtcAsync()
    {
        if (_gsmtc != null) return _gsmtc;
        if (_gsmtcTried) return null;
        _gsmtcTried = true;
        try
        {
            _gsmtc = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            try { _gsmtc.CurrentSessionChanged += OnGsmtcCurrentSessionChanged; } catch { }
            try { _gsmtc.SessionsChanged += OnGsmtcSessionsChanged; } catch { }
            return _gsmtc;
        }
        catch
        {
            _gsmtc = null;
            return null;
        }
    }

    private void OnGsmtcCurrentSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args)
        => RaiseNowPlayingChanged();
    private void OnGsmtcSessionsChanged(GlobalSystemMediaTransportControlsSessionManager sender, SessionsChangedEventArgs args)
        => RaiseNowPlayingChanged();
    private void OnGsmtcMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args)
        => RaiseNowPlayingChanged();
    private void OnGsmtcPlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args)
        => RaiseNowPlayingChanged();
    private void RaiseNowPlayingChanged() { try { NowPlayingChanged?.Invoke(); } catch { } }

    /// <summary>Follow track / play-pause events of the current session only (one subscription at a time).</summary>
    private void HookSessionEvents(GlobalSystemMediaTransportControlsSession? session)
    {
        if (ReferenceEquals(session, _evSession)) return;
        try
        {
            if (_evSession != null)
            {
                _evSession.MediaPropertiesChanged -= OnGsmtcMediaPropertiesChanged;
                _evSession.PlaybackInfoChanged -= OnGsmtcPlaybackInfoChanged;
            }
        }
        catch { }
        _evSession = session;
        try
        {
            if (session != null)
            {
                session.MediaPropertiesChanged += OnGsmtcMediaPropertiesChanged;
                session.PlaybackInfoChanged += OnGsmtcPlaybackInfoChanged;
            }
        }
        catch { }
    }

    private async Task<GlobalSystemMediaTransportControlsSession?> GetActiveSessionAsync()
    {
        var mgr = await EnsureGsmtcAsync();
        if (mgr == null) return null;
        var sel = SelectedEntry?.Session;
        if (sel != null && AllSessions.Any(e => ReferenceEquals(e.Session, sel))) return sel;
        try { return mgr.GetCurrentSession(); }
        catch { return null; }
    }

    /// <summary>
    /// manual = switcher pick (pins it: holds until unpinned or it closes). Otherwise a click on a window that is
    /// verifiably the playing source makes it the primary (subject to the takeover rules afterwards); a pin wins.
    /// </summary>
    public void LockSession(SmtcSessionEntry e, bool manual = false)
    {
        if (manual) { _manualLock = MakeLock(e); _audioLock = null; }
        else if (IsPinned && (_manualLock == null || !ReferenceEquals(_manualLock.Session, e.Session))) return;
        else if (IgnoredForTakeover(e.ProcName, e.Site, e.DurationSec)) return;   // muted autoplay / feed clip
        _primary = e.Session;
        SelectedEntry = e;
    }

    public bool HasLock => IsPinned;

    private static int IndexOfEntry(IReadOnlyList<SmtcSessionEntry> list, SmtcSessionEntry e)
    {
        for (int i = 0; i < list.Count; i++) if (ReferenceEquals(list[i].Session, e.Session)) return i;
        for (int i = 0; i < list.Count; i++) if (list[i].Aumid == e.Aumid && list[i].Ordinal == e.Ordinal) return i;
        return -1;
    }

    private static SmtcSessionEntry? FindLock(List<SmtcSessionEntry> entries, LockRef r)
    {
        var e = entries.FirstOrDefault(x => ReferenceEquals(x.Session, r.Session));
        if (e != null) return e;
        if (r.Title != "") e = entries.FirstOrDefault(x => x.Aumid == r.Aumid && x.Title == r.Title);
        if (e != null) return e;
        // Track changed title (next video / song): accept only if this AUMID was and still is unique.
        var same = entries.Where(x => x.Aumid == r.Aumid).ToList();
        return same.Count == 1 && r.SameAumidCount <= 1 ? same[0] : null;
    }

    public static string ProcNameForAumid(string aumid)
    {
        if (string.IsNullOrWhiteSpace(aumid)) return "";
        string a = aumid.Trim();
        int exe = a.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exe > 0)
        {
            string left = a[..exe];
            int slash = Math.Max(left.LastIndexOf('\\'), left.LastIndexOf('/'));
            return slash >= 0 ? left[(slash + 1)..] : left;
        }
        if (a.Contains("spotify", StringComparison.OrdinalIgnoreCase)) return "Spotify";
        if (a.Contains("opera", StringComparison.OrdinalIgnoreCase)) return a.Contains("gx", StringComparison.OrdinalIgnoreCase) ? "opera_gx" : "opera";
        return ShortAppFromAumid(a);
    }

    private static readonly HashSet<string> BrowserProcNames = new(StringComparer.OrdinalIgnoreCase)
        { "opera", "opera_gx", "chrome", "msedge", "brave", "vivaldi", "firefox" };
    /// <summary>Last site seen per session (aumid|title): keeps "YouTube" while the tab is in the background.</summary>
    private readonly Dictionary<string, string> _siteCache = new();
    // Background-tab media: the window title belongs to the ACTIVE tab, so after a relaunch (empty cache) the
    // site was unknown and the label fell back to the browser name. Read the tab strip once via UIA, off-thread.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _tabSites = new();
    private readonly Dictionary<string, DateTime> _tabSiteTried = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _tabSiteGuesses = new();

    private void QueueTabSiteLookup(string key, string frag, List<IntPtr> hwnds)
    {
        if (frag.Length < 3 || hwnds.Count == 0) return;
        if (_tabSiteTried.TryGetValue(key, out var at) && (DateTime.UtcNow - at).TotalSeconds < 8) return;
        if (_tabSiteTried.Count > 64) _tabSiteTried.Clear();
        _tabSiteTried[key] = DateTime.UtcNow;
        var hs = hwnds.ToArray();
        Task.Run(() =>
        {
            foreach (var h in hs)
            {
                try
                {
                    var tabs = FindTabItems(h);
                    var audioSites = new HashSet<string>();
                    foreach (AutomationElement t in tabs)
                    {
                        string n;
                        try { n = t.Current.Name ?? ""; } catch { continue; }
                        var s = MediaSiteDetector.Detect(n);
                        if (IsAudioSite(s)) audioSites.Add(s);
                        if (n.IndexOf(frag, StringComparison.OrdinalIgnoreCase) < 0) continue;
                        if (s.Length > 0) { _tabSites[key] = s; RaiseNowPlayingChanged(); return; }
                    }
                    // v1.0.2: no tab carries the track title (paused Spotify web shows the playlist name), but exactly
                    // one music site is open -> it's that site (label was "Opera - ..." for ~20-30 s after relaunch).
                    // A guess only names the label; it never counts as "same source" for the content gate.
                    if (audioSites.Count == 1) { _tabSiteGuesses[key] = audioSites.First(); RaiseNowPlayingChanged(); return; }
                }
                catch { }
            }
        });
    }
    /// <summary>
    /// v1.0.11: browser tab-strip items WITHOUT descending into web content. FindAll(Descendants) walked the whole
    /// page tree; since v1.0.10's skip-ready path turns Chromium renderer accessibility on (WM_GETOBJECT nudge),
    /// that tree is huge and each lookup took seconds, so the Media card switched to the new tab/window late.
    /// Control-view BFS, skips Document subtrees, capped at 600 nodes.
    /// </summary>
    internal static List<AutomationElement> FindTabItems(IntPtr hwnd)
    {
        var found = new List<AutomationElement>();
        try
        {
            var walker = TreeWalker.ControlViewWalker;
            var q = new Queue<AutomationElement>();
            q.Enqueue(AutomationElement.FromHandle(hwnd));
            int seen = 0;
            while (q.Count > 0 && seen < 600)
            {
                var el = q.Dequeue();
                AutomationElement? c = null;
                try { c = walker.GetFirstChild(el); } catch { }
                while (c != null && seen < 600)
                {
                    seen++;
                    ControlType? ct = null;
                    try { ct = c.Current.ControlType; } catch { }
                    if (ct == ControlType.TabItem) found.Add(c);
                    else if (ct != ControlType.Document) q.Enqueue(c);
                    try { c = walker.GetNextSibling(c); } catch { c = null; }
                }
            }
        }
        catch { }
        return found;
    }

    /// <summary>v1.0.11: Tim switched windows - allow an immediate tab-strip lookup.</summary>
    public void NoteForegroundChanged()
    {
        try { lock (_audioTabTried) _audioTabTried.Clear(); } catch { }
    }

    private readonly Dictionary<string, (HashSet<uint> Pids, DateTime At)> _browserPids = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Browser sessions: site from the matching tab's window title (else the SMTC title, else the cache).</summary>
    private void AssignSites(List<SmtcSessionEntry> list)
    {
        var browsers = list.Where(e => e.IsBrowserHosted).Select(e => e.ProcName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (browsers.Count == 0) return;
        var titles = new List<(string Proc, string Title)>();
        var procHwnds = new Dictionary<string, List<IntPtr>>(StringComparer.OrdinalIgnoreCase);
        var pidToProc = new Dictionary<uint, string>();
        foreach (var b in browsers)
        {
            if (!_browserPids.TryGetValue(b, out var c) || (DateTime.UtcNow - c.At).TotalSeconds > 10)
            {
                HashSet<uint> pids;
                try { pids = Process.GetProcessesByName(b).Select(p => { using (p) return (uint)p.Id; }).ToHashSet(); }
                catch { pids = new HashSet<uint>(); }
                c = (pids, DateTime.UtcNow);
                _browserPids[b] = c;
            }
            foreach (var pid in c.Pids) pidToProc[pid] = b;
        }
        NativeMethods.EnumWindows((h, _) =>
        {
            if (!NativeMethods.IsWindowVisible(h) && !NativeMethods.IsIconic(h)) return true;
            NativeMethods.GetWindowThreadProcessId(h, out uint pid);
            if (!pidToProc.TryGetValue(pid, out var proc)) return true;
            int len = NativeMethods.GetWindowTextLength(h);
            if (len <= 0) return true;
            var sb = new System.Text.StringBuilder(len + 1);
            NativeMethods.GetWindowText(h, sb, sb.Capacity);
            titles.Add((proc, CleanWindowTitle(sb.ToString(), proc)));
            if (!procHwnds.TryGetValue(proc, out var hl)) procHwnds[proc] = hl = new List<IntPtr>();
            hl.Add(h);
            return true;
        }, IntPtr.Zero);

        foreach (var e in list.Where(x => x.IsBrowserHosted))
        {
            string frag = e.Title.TrimEnd('\u2026', '.', ' ');
            if (frag.Length > 24) frag = frag[..24];
            string site = "";
            if (frag.Length >= 3)
            {
                var wt = titles.FirstOrDefault(t => string.Equals(t.Proc, e.ProcName, StringComparison.OrdinalIgnoreCase)
                                                    && t.Title.IndexOf(frag, StringComparison.OrdinalIgnoreCase) >= 0).Title;
                site = MediaSiteDetector.Detect(wt);
            }
            if (site.Length == 0) site = MediaSiteDetector.Detect(e.Title);
            string key = e.Aumid + "|" + e.Title;
            if (site.Length > 0)
            {
                if (_siteCache.Count > 64) _siteCache.Clear();
                _siteCache[key] = site;
            }
            else if (_siteCache.TryGetValue(key, out var cachedSite)) site = cachedSite;
            else if (_tabSites.TryGetValue(key, out var tabSite)) { site = tabSite; _siteCache[key] = site; }
            else
            {
                if (procHwnds.TryGetValue(e.ProcName, out var bh)) QueueTabSiteLookup(key, frag, bh);
                if (_tabSiteGuesses.TryGetValue(key, out var guess)) { site = guess; e.SiteGuessed = true; }
            }
            e.Site = site;
            if (site.Length > 0) e.Title = MediaSiteDetector.Strip(e.Title, site);
        }
    }

    /// <summary>Enumerate every SMTC session (titles/status) and pick the one to show.</summary>
    private async Task<GlobalSystemMediaTransportControlsSession?> RefreshAllSessionsAsync()
    {
        var mgr = await EnsureGsmtcAsync();
        if (mgr == null) { AllSessions = Array.Empty<SmtcSessionEntry>(); SelectedEntry = null; return null; }
        GlobalSystemMediaTransportControlsSession? current = null;
        try { current = mgr.GetCurrentSession(); } catch { }
        var list = new List<SmtcSessionEntry>();
        try
        {
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var ses in mgr.GetSessions())
            {
                string aumid = "", title = "", status = "", artist = "";
                try { aumid = ses.SourceAppUserModelId ?? ""; } catch { }
                try { status = ses.GetPlaybackInfo()?.PlaybackStatus.ToString() ?? ""; } catch { }
                double dur = 0;
                try { var tl = ses.GetTimelineProperties(); dur = Math.Max(0, (tl.EndTime - tl.StartTime).TotalSeconds); } catch { }
                Track(ses, status);
                try
                {
                    var props = await ses.TryGetMediaPropertiesAsync();
                    title = props?.Title ?? "";
                    artist = props?.Artist ?? "";
                    if (string.IsNullOrWhiteSpace(title)) title = artist;
                }
                catch { }
                title = TitleSanitizer.Clean(title);
                if (LooksLikeId(title)) title = "";
                if (title.Length > 48) title = title[..45] + "...";
                counts.TryGetValue(aumid, out int ord);
                counts[aumid] = ord + 1;
                string proc = ProcNameForAumid(aumid);
                list.Add(new SmtcSessionEntry
                {
                    Session = ses, Aumid = aumid, ProcName = proc,
                    FriendlyApp = FriendlyAppName(proc), Title = title, Status = status, Ordinal = ord,
                    Artist = TitleSanitizer.Clean(artist), IsBrowserHosted = BrowserProcNames.Contains(proc),
                    DurationSec = dur,
                });
            }
        }
        catch { }
        try { AssignSites(list); } catch { }
        AllSessions = list;

        // v1.0.2 primary rules (most recent long-playing source, hysteresis, pin): MediaSessionService.Primary.cs
        var sel = SelectPrimary(list, current);
        SelectedEntry = sel;
        return sel?.Session ?? current;
    }


    /// <summary>Refresh compact now-playing (icon + app/title) from active GSMTC session.</summary>
    public async Task RefreshNowPlayingAsync()
    {
        try
        {
            SampleAudioPeaks();
            // Locked onto an audio-fallback source: keep showing that browser window; SMTC selection is skipped.
            if (_audioLock is { } al)
            {
                if (NativeMethods.IsWindow(al.Hwnd))
                {
                    if (_afHwnd != al.Hwnd) { _afProc = al.Proc; _afHwnd = al.Hwnd; _afTitle = ""; }
                    NowPlayingFromAudio = false;
                    if (TryAudioFallbackFor(al.Proc, requireSiteWindow: false, status: IsAudible(al.Proc) ? "Playing" : "Paused"))
                        return;
                }
                _audioLock = null;   // locked window closed: unlock, follow the playing source again
            }
            var session = await RefreshAllSessionsAsync();
            HookSessionEvents(session);
            NowPlayingFromAudio = false;
            if (session == null)
            {
                // v1.0.2: no SMTC session (Opera publishes none, e.g. after a pop-out) -> browser audio fallback.
                if (!TryAudioFallback()) ClearNowPlaying();
                return;
            }
            string status = "";
            try { status = session.GetPlaybackInfo()?.PlaybackStatus.ToString() ?? ""; } catch { }
            // Stale SMTC session (stopped/closed, or paused and not pinned by Tim) while a browser is actually
            // streaming audio: show the browser instead.
            // v1.0.2 rule (b): only after the shown session has been idle PrimaryIdleSec, never while pinned, and
            // not for the same browser's own lingering stream (Chromium moves its one session to a playing tab).
            string selProc = "";
            try { selProc = ProcNameForAumid(session.SourceAppUserModelId ?? ""); } catch { }
            var selE = SelectedEntry;
            if (status != "Playing" && status != "Changing" && !IsPinned && (selE == null || IdleFor(selE) >= UnlockedSwitchSec)
                && TryAudioFallback(allowPaused: false, excludeProc: selProc))
                return;

            string aumid = "";
            try { aumid = session.SourceAppUserModelId ?? ""; } catch { }

            string title = "";
            string artist = "";
            ImageSource? thumb = null;
            try
            {
                var props = await session.TryGetMediaPropertiesAsync();
                if (props != null)
                {
                    title = props.Title ?? "";
                    artist = props.Artist ?? "";
                    thumb = await TryLoadGsmtcThumbnailAsync(props.Thumbnail, 480);
                }
            }
            catch { }

            uint pid = ResolvePidFromAumid(aumid, out IntPtr npHwnd);
            string procName = "";
            if (pid != 0)
            {
                try
                {
                    using var p = Process.GetProcessById((int)pid);
                    procName = p.ProcessName;
                }
                catch { }
            }

            if (string.IsNullOrEmpty(procName))
                procName = ShortAppFromAumid(aumid);

            var icon = pid != 0 ? WindowIconLoader.ForProcess(pid) : null;
            icon ??= thumb;

            string displayTitle = !string.IsNullOrWhiteSpace(title) ? title
                : (!string.IsNullOrWhiteSpace(artist) ? artist : "");

            // Prefer YouTube window title fragment when browser + empty GSMTC title
            if (string.IsNullOrWhiteSpace(displayTitle) && IsBrowser(procName))
            {
                var hwnd = FindBrowserYouTubeWindow() ?? IntPtr.Zero;
                if (hwnd != IntPtr.Zero)
                {
                    int len = NativeMethods.GetWindowTextLength(hwnd);
                    if (len > 0)
                    {
                        var sb = new System.Text.StringBuilder(len + 1);
                        NativeMethods.GetWindowText(hwnd, sb, sb.Capacity);
                        displayTitle = sb.ToString();
                        if (displayTitle.EndsWith(" - YouTube", StringComparison.OrdinalIgnoreCase))
                            displayTitle = displayTitle[..^" - YouTube".Length].Trim();
                        else if (displayTitle.EndsWith(" — YouTube", StringComparison.OrdinalIgnoreCase))
                            displayTitle = displayTitle[..^" — YouTube".Length].Trim();
                    }
                }
            }

            NowPlayingFullTitle = displayTitle;
            if (displayTitle.Length > 48)
                displayTitle = displayTitle[..45] + "…";

            NowPlayingIcon = icon;
            NowPlayingApp = string.IsNullOrWhiteSpace(procName) ? "Media" : procName;
            NowPlayingTitle = displayTitle;

            // Friendly label: "<Site or App> - <media title or window title>", sanitized (no AUMIDs / mojibake).
            // Browser-hosted media names the site ("YouTube - ..."); the browser only appears in the tooltip.
            string browserName = FriendlyAppName(procName, pid);
            var selEntry = SelectedEntry;
            string site = selEntry != null && ReferenceEquals(selEntry.Session, session) ? selEntry.Site : "";
            _npSiteGuessed = site.Length > 0 && selEntry!.SiteGuessed;
            string friendly = site.Length > 0 ? site : browserName;
            string winTitle = "";
            if (npHwnd != IntPtr.Zero)
            {
                try
                {
                    int wl = NativeMethods.GetWindowTextLength(npHwnd);
                    if (wl > 0)
                    {
                        var wsb = new System.Text.StringBuilder(wl + 1);
                        NativeMethods.GetWindowText(npHwnd, wsb, wsb.Capacity);
                        winTitle = CleanWindowTitle(wsb.ToString(), procName);
                        if (site.Length > 0) winTitle = MediaSiteDetector.Strip(winTitle, site);
                    }
                }
                catch { }
            }
            string mediaTitle = TitleSanitizer.Clean(displayTitle);
            if (LooksLikeId(mediaTitle)) mediaTitle = "";
            if (site.Length > 0) mediaTitle = MediaSiteDetector.Strip(mediaTitle, site);
            string shown = !string.IsNullOrWhiteSpace(mediaTitle) ? mediaTitle : winTitle;
            if (shown.Length > 60) shown = shown[..57] + "...";
            NowPlayingLabel = string.IsNullOrWhiteSpace(shown) ? friendly : (friendly + " - " + shown);
            var tip = new System.Text.StringBuilder();
            tip.Append(friendly);
            if (site.Length > 0) tip.Append(" in ").Append(browserName);
            if (!string.IsNullOrWhiteSpace(winTitle)) tip.Append("\nWindow: ").Append(winTitle);
            if (!string.IsNullOrWhiteSpace(mediaTitle) && mediaTitle != winTitle) tip.Append("\nPlaying: ").Append(mediaTitle);
            string artistClean = TitleSanitizer.Clean(artist);
            if (!string.IsNullOrWhiteSpace(artistClean) && artistClean != mediaTitle)
                tip.Append(site is "YouTube" or "Twitch" or "Kick" or "Vimeo" ? "\nChannel: " : "\nArtist: ").Append(artistClean);
            if (!string.IsNullOrWhiteSpace(procName)) tip.Append("\nProcess: ").Append(procName).Append(".exe");
            NowPlayingTooltip = tip.ToString();
            NowPlayingHwnd = npHwnd;
            NowPlayingArt = thumb;
            NowPlayingStatus = status;
            NowPlayingArtist = TitleSanitizer.Clean(artist);
            string frag24 = mediaTitle.Length > 24 ? mediaTitle[..24] : mediaTitle;
            NowPlayingSite = site.Length > 0 ? site
                : BrowserProcNames.Contains(procName) && frag24.Length >= 3 && winTitle.IndexOf(frag24, StringComparison.OrdinalIgnoreCase) >= 0
                    ? MediaSiteDetector.Detect(winTitle) : "";
            NowPlayingPositionSec = 0; NowPlayingDurationSec = 0; NowPlayingTimelineAtUtc = DateTime.UtcNow;
            try
            {
                var tl = session.GetTimelineProperties();
                NowPlayingDurationSec = Math.Max(0, (tl.EndTime - tl.StartTime).TotalSeconds);
                NowPlayingPositionSec = Math.Max(0, (tl.Position - tl.StartTime).TotalSeconds);
                NowPlayingTimelineAtUtc = tl.LastUpdatedTime.UtcDateTime;
                RememberTimeline(NowPlayingFullTitle, NowPlayingPositionSec, NowPlayingDurationSec, NowPlayingTimelineAtUtc, status == "Playing");
            }
            catch { }
            // Same-session content changes (Opera tab switch, X reel, ad) obey the takeover rules.
            ApplyContentGate(session, procName);
            // Opera drops its SMTC session on tab switches / pop-outs: remember what it was playing so the
            // audio fallback keeps the same source + title instead of re-picking ("Opera", unknown tab).
            if (BrowserProcNames.Contains(NowPlayingApp))
                SeedAudioSticky(NowPlayingApp, NowPlayingHwnd,
                    MediaSiteDetector.Strip(TitleSanitizer.Clean(NowPlayingTitle), NowPlayingSite), NowPlayingSite);
        }
        catch
        {
            ClearNowPlaying();
        }
    }

    private void ClearNowPlaying()
    {
        NowPlayingFromAudio = false;
        NowPlayingSite = "";
        NowPlayingPositionSec = 0; NowPlayingDurationSec = 0;
        NowPlayingIcon = null;
        NowPlayingApp = "";
        NowPlayingTitle = "";
        NowPlayingFullTitle = "";
        NowPlayingLabel = "";
        NowPlayingTooltip = "";
        NowPlayingHwnd = IntPtr.Zero;
        NowPlayingArt = null;
        NowPlayingStatus = "";
        NowPlayingArtist = "";
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _friendlyCache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Human name for a process: known browsers/players get their product names, otherwise the exe's
    /// FileDescription (sanitized), otherwise the process name with a capital letter. Never an AUMID.
    /// </summary>
    public static string FriendlyAppName(string? processName, uint pid = 0)
    {
        string proc = (processName ?? "").Trim();
        if (proc.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) proc = proc[..^4];
        if (string.IsNullOrWhiteSpace(proc)) return "Media";
        switch (proc.ToLowerInvariant())
        {
            case "opera": return "Opera";
            case "opera_gx": return "Opera GX";
            case "chrome": return "Chrome";
            case "msedge": return "Edge";
            case "firefox": return "Firefox";
            case "brave": return "Brave";
            case "vivaldi": return "Vivaldi";
            case "vlc": return "VLC";
            case "wmplayer": return "Windows Media Player";
            case "spotify": return "Spotify";
            case "zunemusic": case "microsoft.media.player": return "Media Player";
        }
        if (_friendlyCache.TryGetValue(proc, out var cached)) return cached;
        string name = "";
        if (pid != 0)
        {
            try
            {
                using var p = Process.GetProcessById((int)pid);
                var desc = p.MainModule?.FileVersionInfo?.FileDescription;
                desc = TitleSanitizer.Clean(desc);
                if (!string.IsNullOrWhiteSpace(desc) && desc.Length <= 40 && !LooksLikeId(desc)) name = desc;
            }
            catch { }
        }
        if (string.IsNullOrWhiteSpace(name))
        {
            name = TitleSanitizer.Clean(proc);
            if (LooksLikeId(name)) name = "Media";
            else if (name.Length > 0 && char.IsLower(name[0])) name = char.ToUpperInvariant(name[0]) + name[1..];
        }
        if (pid != 0) _friendlyCache[proc] = name;
        return name;
    }

    /// <summary>Strip " - Opera" / " - Google Chrome" / " - Microsoft Edge" style suffixes and junk.</summary>
    public static string CleanWindowTitle(string? title, string? processName)
    {
        string t = TitleSanitizer.Clean(title);
        string[] suffixes =
        {
            " - Opera", " - Opera GX", " - Google Chrome", " - Microsoft Edge", " - Microsoft\u200B Edge",
            " - Mozilla Firefox", " \u2014 Mozilla Firefox", " - Brave", " - Vivaldi", " - Personal - Microsoft Edge",
        };
        foreach (var suf in suffixes)
        {
            string sc = TitleSanitizer.Clean(suf);
            if (t.EndsWith(sc, StringComparison.OrdinalIgnoreCase)) { t = t[..^sc.Length].TrimEnd(); break; }
        }
        // "... and 3 more pages" Edge/Chrome tab-group noise
        int more = t.IndexOf(" and ", StringComparison.Ordinal);
        if (more > 0 && t.EndsWith(" more pages", StringComparison.OrdinalIgnoreCase)) t = t[..more].TrimEnd();
        return LooksLikeId(t) ? "" : t;
    }

    /// <summary>True for AUMID / GUID / package-family style identifiers that should never be shown.</summary>
    public static bool LooksLikeId(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        s = s.Trim();
        if (s.Contains('!') && s.Contains('.')) return true;                 // Package_x!App
        if (System.Text.RegularExpressions.Regex.IsMatch(s, @"^\{?[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-")) return true; // GUID
        if (System.Text.RegularExpressions.Regex.IsMatch(s, @"^[A-Za-z][A-Za-z0-9]{2,}(\.[A-Za-z0-9]+){2,}$") && s.Length >= 16) return true; // Vendor.App.Id.123
        if (System.Text.RegularExpressions.Regex.IsMatch(s, @"_[a-z0-9]{13}(!|$)")) return true;   // package family hash
        if (s.Contains('\\') && s.Contains(':')) return true;                                  // full path
        return false;
    }

    private static string ShortAppFromAumid(string aumid)
    {
        if (string.IsNullOrWhiteSpace(aumid)) return "";
        // e.g. Chrome → chrome, Microsoft.ZuneMusic_… → ZuneMusic
        if (aumid.Contains("chrome", StringComparison.OrdinalIgnoreCase)) return "chrome";
        if (aumid.Contains("msedge", StringComparison.OrdinalIgnoreCase) || aumid.Contains("MicrosoftEdge", StringComparison.OrdinalIgnoreCase)) return "msedge";
        if (aumid.Contains("firefox", StringComparison.OrdinalIgnoreCase)) return "firefox";
        if (aumid.Contains("opera", StringComparison.OrdinalIgnoreCase)) return "opera";
        if (aumid.Contains("brave", StringComparison.OrdinalIgnoreCase)) return "brave";
        int excl = aumid.IndexOf('!');
        string left = excl > 0 ? aumid[..excl] : aumid;
        int und = left.IndexOf('_');
        if (und > 0) left = left[..und];
        int dot = left.LastIndexOf('.');
        if (dot >= 0 && dot < left.Length - 1) left = left[(dot + 1)..];
        return left.Length > 24 ? left[..24] : left;
    }

    private uint ResolvePidFromAumid(string aumid) => ResolvePidFromAumid(aumid, out _);

    private uint ResolvePidFromAumid(string aumid, out IntPtr resolvedHwnd)
    {
        var hwnd = FindWindowMatchingAumidOrTitle(aumid);
        if (hwnd == IntPtr.Zero)
            hwnd = FindBrowserYouTubeWindow() ?? IntPtr.Zero;
        if (hwnd == IntPtr.Zero)
            hwnd = FindPrimaryWindow();
        resolvedHwnd = hwnd;
        if (hwnd == IntPtr.Zero) return PrimaryProcessId;
        NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
        return pid;
    }

    /// <summary>decodeWidth: 480 for the Media preview art card (was 16 = icon size, which made the card blurry).</summary>
    private static async Task<ImageSource?> TryLoadGsmtcThumbnailAsync(Windows.Storage.Streams.IRandomAccessStreamReference? thumbRef, int decodeWidth = 16)
    {
        if (thumbRef == null) return null;
        try
        {
            using var ra = await thumbRef.OpenReadAsync();
            var reader = new Windows.Storage.Streams.DataReader(ra);
            var size = checked((uint)ra.Size);
            if (size == 0 || size > 2_000_000) return null;
            await reader.LoadAsync(size);
            var bytes = new byte[size];
            reader.ReadBytes(bytes);
            reader.Dispose();
            var ms = new MemoryStream(bytes);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.StreamSource = ms;
            bmp.DecodePixelWidth = decodeWidth;
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }
    public async Task<(bool ok, string detail)> TryPreviousAsync()
    {
        try
        {
            var session = await GetActiveSessionAsync();
            if (session == null) return (false, "No active media session");
            bool ok = await session.TrySkipPreviousAsync();
            return (ok, ok ? "Previous track" : "Previous not supported");
        }
        catch (Exception ex) { return (false, "Previous failed: " + ex.Message); }
    }

    public async Task<(bool ok, string detail)> TryPlayPauseAsync()
    {
        try
        {
            // v1.1.2: Opera often has no media session (card on the browser-audio fallback, src=audio); the
            // toggle then had no target, or hit some other app's current session. Route to the extension instead.
            string shown = NowPlayingFullTitle.Length > 0 ? NowPlayingFullTitle : NowPlayingTitle;
            var session = NowPlayingFromAudio ? null : await GetActiveSessionAsync();
            if (session != null)
            {
                bool ok = await session.TryTogglePlayPauseAsync();
                MainWindow.MediaLog($"play/pause via smtc ok={ok} shown='{Short(shown)}'");
                if (ok) return (true, "Play/Pause toggled");
            }
            // Only for a YouTube (or unnamed) browser source: never pause a YouTube tab while the card shows
            // something else (Spotify web, a podcast site...).
            bool ytLike = NowPlayingSite.Length == 0 || NowPlayingSite.Equals("YouTube", StringComparison.OrdinalIgnoreCase);
            string? tab = ytLike ? FindExtTab(shown) : null;
            if (tab != null && ExtensionBridge.IsActive())
            {
                bool live = ExtensionBridge.QueueCommand("toggle", tab);
                MainWindow.MediaLog($"play/pause via extension tab={tab} live={live} fromAudio={NowPlayingFromAudio} shown='{Short(shown)}'");
                return (true, "Play/Pause sent to the browser tab");
            }
            MainWindow.MediaLog($"play/pause no target fromAudio={NowPlayingFromAudio} session={(session != null)} extTab={tab ?? "none"} shown='{Short(shown)}'");
            return (false, session == null ? "No media session for this source (reload the yt-adskip extension)" : "Play/Pause not supported");
        }
        catch (Exception ex) { return (false, "Play/Pause failed: " + ex.Message); }
    }

    private static string Short(string s) => s.Length > 40 ? s[..40] : s;


    public async Task<(bool ok, string detail)> TryNextAsync()
    {
        try
        {
            var session = await GetActiveSessionAsync();
            if (session == null) return (false, "No active media session");
            bool ok = await session.TrySkipNextAsync();
            return (ok, ok ? "Next track" : "Next not supported");
        }
        catch (Exception ex) { return (false, "Next failed: " + ex.Message); }
    }

    /// <summary>
    /// Seek ±N seconds on the currently selected/pinned SMTC session only.
    /// No focus changes, no keys/clicks, no fallback to another session if the selected one is gone.
    /// Audio-fallback sources (no SMTC timeline) and live/unseekable timelines are a no-op with a clear detail.
    /// Position is corrected for SMTC snapshot age (docs: Position is current as of LastUpdatedTime) using
    /// elapsed time × PlaybackRate while Playing; fail closed when the snapshot is unsafe to correct.
    /// </summary>
    public async Task<(bool ok, string detail)> TrySeekRelativeAsync(double deltaSeconds)
    {
        try
        {
            if (NowPlayingFromAudio)
                return (false, "Seek not available for this source (no media session timeline)");

            var sel = SelectedEntry;
            if (sel?.Session == null)
                return (false, "No selected media source");
            if (!MediaSeek.TryResolveSelected(SelectedIndex, AllSessions.Count, out string whySel))
                return (false, whySel);
            // Re-check identity: SelectedIndex can match by AUMID ordinal; require the same session object still listed.
            if (!AllSessions.Any(e => ReferenceEquals(e.Session, sel.Session)))
                return (false, "Selected media source is gone");

            var session = sel.Session;
            var info = session.GetPlaybackInfo();
            if (info?.Controls == null || !info.Controls.IsPlaybackPositionEnabled)
                return (false, "Seek not supported for this source");

            var tl = session.GetTimelineProperties();
            double dur = Math.Max(0, (tl.EndTime - tl.StartTime).TotalSeconds);
            double snapPos = Math.Max(0, (tl.Position - tl.StartTime).TotalSeconds);
            DateTime lastUp = tl.LastUpdatedTime.UtcDateTime;
            DateTime now = DateTime.UtcNow;
            bool playing = string.Equals(info.PlaybackStatus.ToString(), "Playing", StringComparison.OrdinalIgnoreCase);
            double? rate = null;
            try { rate = info.PlaybackRate; } catch { }

            if (!MediaSeek.TryEstimateLivePosition(snapPos, dur, lastUp, now, playing, rate,
                    out double livePos, out string whyEst))
                return (false, whyEst);

            bool hasPending = _pendingSeekSession != null
                && ReferenceEquals(_pendingSeekSession, session)
                && _pendingSeekIssuedUtc > DateTime.MinValue;
            MediaSeek.TryResolvePositionWithPending(livePos, lastUp, now, playing, rate,
                hasPending, _pendingSeekIssuedUtc, _pendingSeekTargetSec, dur,
                out double pos, out string posSrc);

            if (!MediaSeek.TryComputeTarget(pos, dur, deltaSeconds, out double targetSec, out string whyTl))
                return (false, whyTl);

            var target = tl.StartTime + TimeSpan.FromSeconds(targetSec);
            try
            {
                if (tl.MaxSeekTime > tl.MinSeekTime)
                {
                    if (target > tl.MaxSeekTime) target = tl.MaxSeekTime;
                    if (target < tl.MinSeekTime) target = tl.MinSeekTime;
                }
            }
            catch { }

            bool ok = await session.TryChangePlaybackPositionAsync(target.Ticks);
            string dir = deltaSeconds < 0 ? "Rewind 10s" : "Forward 10s";
            if (ok)
            {
                _pendingSeekSession = session;
                _pendingSeekIssuedUtc = DateTime.UtcNow;
                _pendingSeekTargetSec = targetSec;
            }
            MainWindow.MediaLog(
                $"seek delta={deltaSeconds:0} ok={ok} snap={snapPos:0.#}@{lastUp:HH:mm:ss}Z live={livePos:0.#} pos={pos:0.#}({posSrc})->{targetSec:0.#}/{dur:0.#} rate={rate?.ToString("0.##") ?? "?"} playing={playing} selected='{Short(sel.Title)}'");
            return (ok, ok ? dir : dir + " not supported");
        }
        catch (Exception ex) { return (false, "Seek failed: " + ex.Message); }
    }

    /// <summary>
    /// Best-effort Skip Ad: focus YouTube/browser media window, UIA invoke button whose
    /// name contains Skip, then try common click/key patterns. Does NOT always work.
    /// </summary>
    public async Task<(bool ok, string detail)> TrySkipAdAsync(IntPtr preferredHwnd = default)
    {
        try
        {
            var hwnd = preferredHwnd;
            if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd))
                hwnd = await FindActiveMediaWindowAsync();
            if (hwnd == IntPtr.Zero)
                hwnd = FindBrowserYouTubeWindow() ?? IntPtr.Zero;
            if (hwnd == IntPtr.Zero)
                hwnd = FindPrimaryWindow();

            if (hwnd == IntPtr.Zero)
                return (false, "Couldn't find Skip — focus the player and click Skip");

            // One F8: focus, wait for overlay, UIA twice, then click guesses + final UIA.
            NativeMethods.ForceForegroundWindow(hwnd);
            await Task.Delay(280);

            if (TryInvokeSkipViaUia(hwnd))
                return (true, "Skip ad invoked (UIA)");

            await Task.Delay(180);
            NativeMethods.ForceForegroundWindow(hwnd);
            if (TryInvokeSkipViaUia(hwnd))
                return (true, "Skip ad invoked (UIA, retry)");

            TryYouTubeSkipFallback(hwnd);
            await Task.Delay(120);
            if (TryInvokeSkipViaUia(hwnd))
                return (true, "Skip ad invoked (UIA after clicks)");

            return (true, "Skip ad attempted — if it stayed, hit F8 once more");
        }
        catch (Exception ex)
        {
            return (false, "Skip failed: " + ex.Message);
        }
    }
private static bool TryInvokeSkipViaUia(IntPtr hwnd)
    {
        try
        {
            var root = AutomationElement.FromHandle(hwnd);
            if (root == null) return false;

            // Name contains "Skip" (Skip Ad / Skip Ads / Skip advertisement)
            var cond = new AndCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                new OrCondition(
                    new PropertyCondition(AutomationElement.NameProperty, "Skip", PropertyConditionFlags.IgnoreCase),
                    new PropertyCondition(AutomationElement.NameProperty, "Skip Ad", PropertyConditionFlags.IgnoreCase),
                    new PropertyCondition(AutomationElement.NameProperty, "Skip Ads", PropertyConditionFlags.IgnoreCase),
                    new PropertyCondition(AutomationElement.NameProperty, "Skip advertisement", PropertyConditionFlags.IgnoreCase)));

            var btn = root.FindFirst(TreeScope.Descendants, cond);
            if (btn == null)
            {
                // Broader: any element with Name containing Skip (deeper walk — Chrome trees are large)
                btn = FindElementNameContains(root, "skip", maxNodes: 1600);
            }

            if (btn == null) return false;

            if (btn.TryGetCurrentPattern(InvokePattern.Pattern, out object? pat) && pat is InvokePattern inv)
            {
                inv.Invoke();
                return true;
            }

            // Click center of bounding rect via SendInput-less mouse event if possible
            try
            {
                var r = btn.Current.BoundingRectangle;
                if (!r.IsEmpty)
                {
                    NativeMethods.ClickScreenPoint(
                        (int)(r.Left + r.Width / 2),
                        (int)(r.Top + r.Height / 2));
                    return true;
                }
            }
            catch { }

            return false;
        }
        catch
        {
            return false;
        }
    }

    private static AutomationElement? FindElementNameContains(AutomationElement root, string needle, int maxNodes)
    {
        try
        {
            var walker = TreeWalker.ControlViewWalker;
            var q = new Queue<AutomationElement>();
            q.Enqueue(root);
            int n = 0;
            while (q.Count > 0 && n < maxNodes)
            {
                var el = q.Dequeue();
                n++;
                string name = "";
                try { name = el.Current.Name ?? ""; } catch { continue; }
                if (name.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    // Prefer real Skip Ad controls over unrelated "skip" text.
                    bool likelyAd = name.IndexOf("ad", StringComparison.OrdinalIgnoreCase) >= 0
                        || name.Equals("Skip", StringComparison.OrdinalIgnoreCase)
                        || name.StartsWith("Skip ", StringComparison.OrdinalIgnoreCase);
                    if (!likelyAd) { /* keep walking */ }
                    else
                    {
                        try
                        {
                            var ct = el.Current.ControlType;
                            if (ct == ControlType.Button || ct == ControlType.Hyperlink || ct == ControlType.Text || ct == ControlType.Custom)
                                return el;
                        }
                        catch { return el; }
                    }
                }

                AutomationElement? child;
                try { child = walker.GetFirstChild(el); } catch { child = null; }
                while (child != null)
                {
                    q.Enqueue(child);
                    try { child = walker.GetNextSibling(child); } catch { break; }
                }
            }
        }
        catch { }
        return null;
    }

    /// <summary>
    /// Best-effort YouTube skip when UIA misses the overlay: click lower-right of the
    /// client area (typical Skip Ads placement) then try Enter.
    /// </summary>
    private static bool TryYouTubeSkipFallback(IntPtr hwnd)
    {
        try
        {
            if (!NativeMethods.GetClientRect(hwnd, out var cr)) return false;
            NativeMethods.POINT tl = new() { X = cr.Left, Y = cr.Top };
            NativeMethods.ClientToScreen(hwnd, ref tl);
            int w = cr.Right - cr.Left;
            int h = cr.Bottom - cr.Top;
            if (w < 80 || h < 80) return false;

            // Several Skip Ads placements in one press (player lower-right / mid-right).
            NativeMethods.ClickScreenPoint(tl.X + (int)(w * 0.92), tl.Y + (int)(h * 0.82));
            Thread.Sleep(40);
            NativeMethods.ClickScreenPoint(tl.X + (int)(w * 0.88), tl.Y + (int)(h * 0.78));
            Thread.Sleep(40);
            NativeMethods.ClickScreenPoint(tl.X + (int)(w * 0.90), tl.Y + (int)(h * 0.70));
            Thread.Sleep(40);
            NativeMethods.ClickScreenPoint(tl.X + (int)(w * 0.85), tl.Y + (int)(h * 0.85));
            Thread.Sleep(40);
            NativeMethods.SendVirtualKey(NativeMethods.VK_RETURN);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task<IntPtr> FindActiveMediaWindowAsync()
    {
        try
        {
            var session = await GetActiveSessionAsync();
            if (session != null)
            {
                string aumid = "";
                try { aumid = session.SourceAppUserModelId ?? ""; } catch { }
                var hwnd = FindWindowMatchingAumidOrTitle(aumid);
                if (hwnd != IntPtr.Zero) return hwnd;
            }
        }
        catch { }

        return FindBrowserYouTubeWindow() ?? FindPrimaryWindow();
    }

    private static IntPtr? FindBrowserYouTubeWindow()
    {
        IntPtr found = IntPtr.Zero;
        IntPtr youtubePreferred = IntPtr.Zero;
        var shell = NativeMethods.GetShellWindow();
        NativeMethods.EnumWindows((hWnd, _) =>
        {
            if (hWnd == IntPtr.Zero || hWnd == shell) return true;
            if (!NativeMethods.IsWindowVisible(hWnd) && !NativeMethods.IsIconic(hWnd)) return true;
            NativeMethods.GetWindowThreadProcessId(hWnd, out uint pid);
            string proc = "";
            try
            {
                using var p = Process.GetProcessById((int)pid);
                proc = p.ProcessName;
            }
            catch { return true; }

            if (!IsBrowser(proc)) return true;
            int len = NativeMethods.GetWindowTextLength(hWnd);
            if (len <= 0) return true;
            var sb = new System.Text.StringBuilder(len + 1);
            NativeMethods.GetWindowText(hWnd, sb, sb.Capacity);
            string title = sb.ToString();
            if (title.IndexOf("YouTube", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                youtubePreferred = hWnd;
                return false;
            }
            if (found == IntPtr.Zero)
                found = hWnd;
            return true;
        }, IntPtr.Zero);

        if (youtubePreferred != IntPtr.Zero) return youtubePreferred;
        if (found != IntPtr.Zero) return found;
        return null;
    }

    private static IntPtr FindWindowMatchingAumidOrTitle(string aumid)
    {
        if (string.IsNullOrWhiteSpace(aumid)) return IntPtr.Zero;
        string needle = aumid;
        // Chrome AUMIDs often contain Chrome; Edge contains MSEdge / MicrosoftEdge
        IntPtr found = IntPtr.Zero;
        var shell = NativeMethods.GetShellWindow();
        NativeMethods.EnumWindows((hWnd, _) =>
        {
            if (hWnd == IntPtr.Zero || hWnd == shell) return true;
            if (!NativeMethods.IsWindowVisible(hWnd) && !NativeMethods.IsIconic(hWnd)) return true;
            NativeMethods.GetWindowThreadProcessId(hWnd, out uint pid);
            string proc = "";
            try
            {
                using var p = Process.GetProcessById((int)pid);
                proc = p.ProcessName;
            }
            catch { return true; }

            bool matchProc =
                needle.IndexOf(proc, StringComparison.OrdinalIgnoreCase) >= 0
                || proc.IndexOf("chrome", StringComparison.OrdinalIgnoreCase) >= 0 && needle.IndexOf("chrome", StringComparison.OrdinalIgnoreCase) >= 0
                || proc.IndexOf("msedge", StringComparison.OrdinalIgnoreCase) >= 0 && needle.IndexOf("edge", StringComparison.OrdinalIgnoreCase) >= 0
                || proc.IndexOf("firefox", StringComparison.OrdinalIgnoreCase) >= 0 && needle.IndexOf("firefox", StringComparison.OrdinalIgnoreCase) >= 0
                || proc.IndexOf("opera", StringComparison.OrdinalIgnoreCase) >= 0 && needle.IndexOf("opera", StringComparison.OrdinalIgnoreCase) >= 0;

            // v1.0.2: only windows of the session's own app (a Spotify session must never resolve to a
            // browser YouTube window).
            if (!matchProc) return true;

            int len = NativeMethods.GetWindowTextLength(hWnd);
            if (len <= 0) return true;
            var sb = new System.Text.StringBuilder(len + 1);
            NativeMethods.GetWindowText(hWnd, sb, sb.Capacity);
            string title = sb.ToString();

            if (title.IndexOf("YouTube", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                found = hWnd;
                return false;
            }
            if (found == IntPtr.Zero && matchProc)
                found = hWnd;
            return true;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>Adjust master multimedia endpoint volume by delta (-1..+1). Returns new level 0..1.</summary>
    public bool AdjustMasterVolume(float delta, out float level)
    {
        level = 0f;
        EnsureCurrentDevice();
        if (_device == null) return false;
        lock (_gate)
        {
            try
            {
                var ep = _device.AudioEndpointVolume;
                float next = Math.Clamp(ep.MasterVolumeLevelScalar + delta, 0f, 1f);
                ep.MasterVolumeLevelScalar = next;
                if (next > 0.001f && ep.Mute)
                    ep.Mute = false;
                level = next;
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    public float GetMasterVolume()
    {
        return TryGetMasterVolume(out float v, out _) ? v : 0f;
    }

    /// <summary>Current default-endpoint level 0..1 and mute (re-acquires the endpoint if it changed).</summary>
    public bool TryGetMasterVolume(out float level, out bool muted)
    {
        level = 0f; muted = false;
        lock (_gate)
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                EnsureCurrentDeviceLocked(force: attempt > 0);
                if (_device == null) return false;
                try
                {
                    var ep = _device.AudioEndpointVolume;
                    level = ep.MasterVolumeLevelScalar;
                    muted = ep.Mute;
                    return true;
                }
                catch { }
            }
            return false;
        }
    }

    /// <summary>Set default-endpoint level 0..1 (unmutes when raised above 0). Retries once on a fresh endpoint.</summary>
    public bool SetMasterVolume(float value, out float level)
    {
        level = 0f;
        value = Math.Clamp(value, 0f, 1f);
        lock (_gate)
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                EnsureCurrentDeviceLocked(force: attempt > 0);
                if (_device == null) return false;
                try
                {
                    var ep = _device.AudioEndpointVolume;
                    ep.MasterVolumeLevelScalar = value;
                    if (value > 0.001f && ep.Mute) ep.Mute = false;
                    level = value;
                    return true;
                }
                catch { }
            }
            return false;
        }
    }

    public void Refresh()
    {
        EnsureCurrentDevice();
        if (_device == null) return;

        List<MediaSessionItem> next;
        lock (_gate)
        {
            try
            {
                _device.AudioSessionManager.RefreshSessions();
                var mgr = _device.AudioSessionManager.Sessions;
                next = new List<MediaSessionItem>();
                var seen = new HashSet<uint>();

                for (int i = 0; i < mgr.Count; i++)
                {
                    var session = mgr[i];
                    uint pid = 0;
                    try { pid = session.GetProcessID; } catch { continue; }
                    if (pid == 0) continue;
                    if (!seen.Add(pid)) continue;

                    string procName = "";
                    try
                    {
                        using var p = Process.GetProcessById((int)pid);
                        procName = p.ProcessName;
                    }
                    catch { continue; }

                    if (string.Equals(procName, "WorkflowDashboard", StringComparison.OrdinalIgnoreCase))
                        continue;

                    string display = "";
                    try { display = session.DisplayName ?? ""; } catch { }

                    bool muted = false;
                    try { muted = session.SimpleAudioVolume.Mute; } catch { }

                    next.Add(new MediaSessionItem
                    {
                        ProcessId = pid,
                        ProcessName = procName,
                        DisplayName = display,
                        IsMuted = muted
                    });
                }

                next = next
                    .OrderByDescending(s => IsBrowser(s.ProcessName))
                    .ThenBy(s => s.ProcessName, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch
            {
                return;
            }
        }

        var existing = Sessions.ToDictionary(s => s.ProcessId);
        var nextIds = next.Select(s => s.ProcessId).ToHashSet();

        for (int i = Sessions.Count - 1; i >= 0; i--)
        {
            if (!nextIds.Contains(Sessions[i].ProcessId))
                Sessions.RemoveAt(i);
        }

        foreach (var item in next)
        {
            if (existing.TryGetValue(item.ProcessId, out var cur))
            {
                cur.IsMuted = item.IsMuted;
            }
            else
            {
                Sessions.Add(item);
            }
        }

        UpdatePrimary();
    }

    private void UpdatePrimary()
    {
        if (PrimaryProcessId != 0)
        {
            var still = Sessions.FirstOrDefault(s => s.ProcessId == PrimaryProcessId);
            if (still != null && !still.IsMuted)
                return;
        }

        var unmuted = Sessions.FirstOrDefault(s => !s.IsMuted && IsBrowser(s.ProcessName))
                   ?? Sessions.FirstOrDefault(s => !s.IsMuted)
                   ?? Sessions.FirstOrDefault(s => IsBrowser(s.ProcessName))
                   ?? Sessions.FirstOrDefault();

        PrimaryProcessId = unmuted?.ProcessId ?? 0;
    }

    public static bool IsBrowser(string processName)
    {
        return processName.Equals("chrome", StringComparison.OrdinalIgnoreCase)
            || processName.Equals("msedge", StringComparison.OrdinalIgnoreCase)
            || processName.Equals("firefox", StringComparison.OrdinalIgnoreCase)
            || processName.Equals("brave", StringComparison.OrdinalIgnoreCase)
            || processName.Equals("opera", StringComparison.OrdinalIgnoreCase)
            || processName.Equals("vivaldi", StringComparison.OrdinalIgnoreCase);
    }

    public bool ToggleMute(uint processId)
    {
        if (_device == null) return false;
        lock (_gate)
        {
            try
            {
                _device.AudioSessionManager.RefreshSessions();
                var mgr = _device.AudioSessionManager.Sessions;
                bool found = false;
                bool newMute = false;
                for (int i = 0; i < mgr.Count; i++)
                {
                    var session = mgr[i];
                    uint pid = 0;
                    try { pid = session.GetProcessID; } catch { continue; }
                    if (pid != processId) continue;
                    bool muted = session.SimpleAudioVolume.Mute;
                    session.SimpleAudioVolume.Mute = !muted;
                    newMute = !muted;
                    found = true;
                }
                if (!found) return false;

                var item = Sessions.FirstOrDefault(s => s.ProcessId == processId);
                if (item != null) item.IsMuted = newMute;

                if (!newMute)
                    PrimaryProcessId = processId;
                else
                    UpdatePrimary();

                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    public bool TogglePrimaryMute()
    {
        UpdatePrimary();
        if (PrimaryProcessId == 0)
        {
            Refresh();
            UpdatePrimary();
        }
        if (PrimaryProcessId == 0) return false;
        return ToggleMute(PrimaryProcessId);
    }

    public IntPtr FindPrimaryWindow()
    {
        UpdatePrimary();
        if (PrimaryProcessId == 0)
        {
            Refresh();
            UpdatePrimary();
        }
        if (PrimaryProcessId == 0) return IntPtr.Zero;

        IntPtr found = IntPtr.Zero;
        IntPtr youtubePreferred = IntPtr.Zero;
        var shell = NativeMethods.GetShellWindow();
        NativeMethods.EnumWindows((hWnd, _) =>
        {
            if (hWnd == IntPtr.Zero || hWnd == shell) return true;
            if (!NativeMethods.IsWindowVisible(hWnd)) return true;
            NativeMethods.GetWindowThreadProcessId(hWnd, out uint pid);
            if (pid != PrimaryProcessId) return true;
            int len = NativeMethods.GetWindowTextLength(hWnd);
            if (len <= 0) return true;
            var sb = new System.Text.StringBuilder(len + 1);
            NativeMethods.GetWindowText(hWnd, sb, sb.Capacity);
            if (sb.ToString().IndexOf("YouTube", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                youtubePreferred = hWnd;
                return false;
            }
            if (found == IntPtr.Zero)
                found = hWnd;
            return true;
        }, IntPtr.Zero);
        return youtubePreferred != IntPtr.Zero ? youtubePreferred : found;
    }

    public void Dispose()
    {
        try { if (_deviceWatcher != null) _enumerator?.UnregisterEndpointNotificationCallback(_deviceWatcher); } catch { }
        _deviceWatcher = null;
        lock (_gate)
        {
            _device?.Dispose();
            _device = null;
            _enumerator?.Dispose();
            _enumerator = null;
        }
        try { HookSessionEvents(null); } catch { }
        try { if (_gsmtc != null) _gsmtc.CurrentSessionChanged -= OnGsmtcCurrentSessionChanged; } catch { }
        try { if (_gsmtc != null) _gsmtc.SessionsChanged -= OnGsmtcSessionsChanged; } catch { }
        _gsmtc = null;
    }
}
