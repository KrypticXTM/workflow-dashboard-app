using System.Diagnostics;
using System.Windows.Automation;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace WorkflowDashboard;

/// <summary>
/// v1.0.2 audio fallback. Opera often publishes NO SMTC session (none at all after a video was popped out -
/// its hidden "Picture in Picture" window lingers), so RefreshNowPlayingAsync cleared now-playing: label
/// "No media window", preview "Nothing playing", while the per-app volume row (WASAPI) still listed Opera.
/// Now, when SMTC has nothing usable, a browser with an ACTIVE (streaming) audio session is treated as the
/// thing playing: the source is its window whose title names a media site (YouTube first), else the tab found
/// via the tab strip (audio-playing / media-site tab, off-thread UIA), else its first window. The title is
/// re-read from the window every refresh, so a new video re-matches instead of dropping. Linked-pane windows
/// are valid sources (nothing here or in the resolver excludes them).
/// Without an active stream (paused), only a browser window titled with a media site is used, and only when
/// SMTC has no session at all.
/// </summary>
internal sealed partial class MediaSessionService
{
    public bool NowPlayingFromAudio { get; private set; }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (IntPtr Hwnd, string Title, bool Audio)> _audioTabs =
        new(StringComparer.OrdinalIgnoreCase);
    // Sticky binding (9:02 PM fix): the window/tab chosen as the playing one stays the source while it exists.
    // Clicking or switching tabs never re-picks it; only a closed window or a tab VERIFIABLY playing
    // (audio indicator on the tab) moves it. Title "" = playing tab unknown -> card, never a live window.
    private string _afProc = "";
    private IntPtr _afHwnd;
    private string _afTitle = "";
    private readonly Dictionary<string, DateTime> _audioTabTried = new(StringComparer.OrdinalIgnoreCase);
    private int _audioTabBusy;

    /// <summary>Browser with a streaming WASAPI session (or ""), plus every browser that has any session.</summary>
    private string ActiveAudioBrowser(out List<string> anyBrowsers)
    {
        anyBrowsers = new List<string>();
        string active = "";
        if (_device == null) return "";
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
                        string name;
                        using (var p = Process.GetProcessById((int)pid)) name = p.ProcessName;
                        if (!BrowserProcNames.Contains(name)) continue;
                        if (!anyBrowsers.Contains(name, StringComparer.OrdinalIgnoreCase)) anyBrowsers.Add(name);
                        if (active.Length == 0 && s.State == AudioSessionState.AudioSessionStateActive) active = name;
                    }
                    catch { }
                }
            }
            catch { }
        }
        return active;
    }

    /// <summary>SMTC verified this browser window/title as playing: the fallback starts from it.</summary>
    private void SeedAudioSticky(string proc, IntPtr hwnd, string mediaTitle, string site)
    {
        if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd) || mediaTitle.Length < 3) return;
        _afProc = proc;
        _afHwnd = hwnd;
        _afTitle = site.Length > 0 ? mediaTitle + " - " + site : mediaTitle;
    }

    /// <summary>Fill now-playing from browser audio. False = nothing usable (caller clears).</summary>
    private bool TryAudioFallback(bool allowPaused = true, string excludeProc = "")
    {
        string active = ActiveAudioBrowser(out var any);
        if (excludeProc.Length > 0 && string.Equals(active, excludeProc, StringComparison.OrdinalIgnoreCase)) active = "";
        if (active.Length > 0 && TryAudioFallbackFor(active, requireSiteWindow: false, status: "Playing")) return true;
        if (!allowPaused) return false;
        foreach (var b in any)
            if (TryAudioFallbackFor(b, requireSiteWindow: true, status: "Paused")) return true;
        return false;
    }

    private HashSet<uint> BrowserPidsFor(string proc)
    {
        if (!_browserPids.TryGetValue(proc, out var c) || (DateTime.UtcNow - c.At).TotalSeconds > 10)
        {
            HashSet<uint> pids;
            try { pids = Process.GetProcessesByName(proc).Select(p => { using (p) return (uint)p.Id; }).ToHashSet(); }
            catch { pids = new HashSet<uint>(); }
            c = (pids, DateTime.UtcNow);
            _browserPids[proc] = c;
        }
        return c.Pids;
    }

    private bool TryAudioFallbackFor(string proc, bool requireSiteWindow, string status)
    {
        var pids = BrowserPidsFor(proc);
        if (pids.Count == 0) return false;
        var wins = new List<(IntPtr H, string Title, uint Pid)>();
        NativeMethods.EnumWindows((h, _) =>
        {
            if (!NativeMethods.IsWindowVisible(h) && !NativeMethods.IsIconic(h)) return true;
            NativeMethods.GetWindowThreadProcessId(h, out uint pid);
            if (!pids.Contains(pid)) return true;
            long ex = NativeMethods.GetWindowLongPtr(h, NativeMethods.GWL_EXSTYLE).ToInt64();
            if ((ex & NativeMethods.WS_EX_TOOLWINDOW) != 0) return true;
            int len = NativeMethods.GetWindowTextLength(h);
            if (len <= 0) return true;
            var sb = new System.Text.StringBuilder(len + 1);
            NativeMethods.GetWindowText(h, sb, sb.Capacity);
            string raw = sb.ToString();
            if (raw.StartsWith("Picture in Picture", StringComparison.OrdinalIgnoreCase)) return true;
            wins.Add((h, CleanWindowTitle(raw, proc), pid));
            return true;
        }, IntPtr.Zero);
        if (wins.Count == 0) return false;

        QueueAudioTabLookup(proc, wins.Select(w => w.H).ToList());
        _audioTabs.TryGetValue(proc, out var tab);
        bool tabOk = tab.Hwnd != IntPtr.Zero && wins.Any(w => w.H == tab.Hwnd);
        (IntPtr H, string Title, uint Pid) pick = default;
        string title = "";
        bool sticky = string.Equals(_afProc, proc, StringComparison.OrdinalIgnoreCase) && wins.Any(w => w.H == _afHwnd);
        if (sticky)
        {
            pick = wins.First(w => w.H == _afHwnd);
            title = _afTitle;
            string boundSite = MediaSiteDetector.Detect(_afTitle);
            string curSite = MediaSiteDetector.Detect(pick.Title);
            if (tabOk && tab.Audio && (_audioLock == null || tab.Hwnd == _audioLock.Value.Hwnd) && (tab.Hwnd != _afHwnd || !string.Equals(tab.Title, _afTitle, StringComparison.OrdinalIgnoreCase)))
            {
                pick = wins.First(w => w.H == tab.Hwnd);   // another tab verifiably playing
                title = tab.Title;
            }
            else if (_afTitle.Length == 0 && IsVideoSite(curSite))
                title = pick.Title;                        // upgrade "unknown" once a video-site tab shows
            else if (boundSite.Length > 0 && curSite == boundSite && !string.Equals(pick.Title, _afTitle, StringComparison.Ordinal)
                     && !wins.Any(w => w.H != _afHwnd && string.Equals(w.Title, _afTitle, StringComparison.Ordinal)))
                title = pick.Title;                        // same site in the bound window: next video / autoplay
        }
        else
        {
            if (tabOk && tab.Audio) { pick = wins.First(w => w.H == tab.Hwnd); title = tab.Title; }
            if (pick.H == IntPtr.Zero)
            {
                pick = wins.FirstOrDefault(w => MediaSiteDetector.Detect(w.Title) == "YouTube");
                if (pick.H == IntPtr.Zero) pick = wins.FirstOrDefault(w => IsVideoSite(MediaSiteDetector.Detect(w.Title)));
                if (pick.H != IntPtr.Zero) title = pick.Title;
            }
            if (pick.H == IntPtr.Zero && tabOk && IsVideoSite(MediaSiteDetector.Detect(tab.Title)))
            {
                pick = wins.First(w => w.H == tab.Hwnd);   // video-site tab in the background
                title = tab.Title;
            }
            if (pick.H == IntPtr.Zero)
            {
                if (requireSiteWindow) return false;
                pick = wins[0];                            // playing tab unknown: card only (empty title)
                title = "";
            }
        }
        _afProc = proc;
        _afHwnd = pick.H;
        _afTitle = title;

        string site = MediaSiteDetector.Detect(title);
        string mediaTitle = site.Length > 0 ? MediaSiteDetector.Strip(title, site) : title;
        mediaTitle = TitleSanitizer.Clean(mediaTitle);
        if (LooksLikeId(mediaTitle)) mediaTitle = "";
        string browserName = FriendlyAppName(proc, pick.Pid);
        string friendly = site.Length > 0 ? site : browserName;
        string shown = mediaTitle.Length > 60 ? mediaTitle[..57] + "..." : mediaTitle;

        NowPlayingIcon = WindowIconLoader.ForProcess(pick.Pid);
        NowPlayingApp = proc;
        // Spotify Web Player window title is "<song> \u2022 <artist>": split it so the card gets both, uncut.
        string fbArtist = "";
        int dot = site == "Spotify" ? mediaTitle.IndexOf(" \u2022 ", StringComparison.Ordinal) : -1;
        if (dot > 0) { fbArtist = mediaTitle[(dot + 3)..].Trim(); mediaTitle = mediaTitle[..dot].Trim(); }
        NowPlayingFullTitle = mediaTitle;
        NowPlayingTitle = mediaTitle.Length > 48 ? mediaTitle[..45] + "\u2026" : mediaTitle;
        NowPlayingLabel = shown.Length == 0 ? friendly : friendly + " - " + shown;
        var tip = new System.Text.StringBuilder(friendly);
        if (site.Length > 0) tip.Append(" in ").Append(browserName);
        if (mediaTitle.Length > 0) tip.Append("\nPlaying: ").Append(mediaTitle);
        tip.Append("\n(").Append(browserName).Append(" reports no media-controls session; found by its audio)");
        tip.Append("\nProcess: ").Append(proc).Append(".exe");
        NowPlayingTooltip = tip.ToString();
        NowPlayingHwnd = pick.H;
        NowPlayingArt = null;
        NowPlayingStatus = status;
        NowPlayingArtist = fbArtist;
        NowPlayingSite = site;
        NowPlayingPositionSec = 0; NowPlayingDurationSec = 0;
        NowPlayingFromAudio = true;
        return true;
    }

    /// <summary>Tab strip lookup: a tab flagged as playing audio, else one naming a media site. Every 5 s max.</summary>
    private void QueueAudioTabLookup(string proc, List<IntPtr> hwnds)
    {
        if (hwnds.Count == 0) return;
        lock (_audioTabTried)
        {
            if (_audioTabTried.TryGetValue(proc, out var at) && (DateTime.UtcNow - at).TotalSeconds < 5) return;
            if (Interlocked.CompareExchange(ref _audioTabBusy, 1, 0) != 0) return;   // one lookup at a time
            _audioTabTried[proc] = DateTime.UtcNow;
        }
        var hs = hwnds.ToArray();
        Task.Run(() =>
        {
            try
            {
                _audioTabs.TryGetValue(proc, out var before);
                (IntPtr, string, bool)? result = null;
                (IntPtr, string)? site = null;
                foreach (var h in hs)
                {
                    try
                    {
                        foreach (var t in FindTabItems(h))   // v1.0.11: tab strip only, never the page tree
                        {
                            string n;
                            try { n = t.Current.Name ?? ""; } catch { continue; }
                            string clean = StripAudioTabSuffix(n, out bool audio);
                            if (audio) { result = (h, clean, true); break; }
                            if (site == null && IsVideoSite(MediaSiteDetector.Detect(clean))) site = (h, clean);
                        }
                    }
                    catch { }
                    if (result != null) break;
                }
                if (result == null && site is { } s) result = (s.Item1, s.Item2, false);
                if (result is { } r) _audioTabs[proc] = r; else _audioTabs.TryRemove(proc, out _);
                // v1.0.11: the result used to wait for the next 1.2 s tick; apply it now if it changed.
                _audioTabs.TryGetValue(proc, out var after);
                if (after != before) RaiseNowPlayingChanged();
            }
            finally { Volatile.Write(ref _audioTabBusy, 0); }
        });
    }

    /// <summary>Sites whose tab is a media player (X / Spotify-web titles are not proof of playback).</summary>
    private static bool IsVideoSite(string site) =>
        site is "YouTube" or "YouTube Music" or "Twitch" or "Kick" or "Vimeo" or "Netflix" or "Prime Video"
            or "Disney+" or "SoundCloud";

    private static readonly string[] AudioTabMarkers =
        { " - Audio playing", " - Playing audio", " - Audio is playing", ", playing audio", " (playing audio)" };

    private static string StripAudioTabSuffix(string name, out bool audio)
    {
        audio = false;
        string n = (name ?? "").Trim();
        foreach (var m in AudioTabMarkers)
        {
            int i = n.IndexOf(m, StringComparison.OrdinalIgnoreCase);
            if (i > 0) { audio = true; n = n[..i].Trim(); break; }
        }
        return n;
    }
}
