using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;

namespace WorkflowDashboard;

internal enum SkipAdState { NoPlayer, Idle, AdPlaying, Ready }

internal sealed class SkipAdScan
{
    public SkipAdState State;
    public IntPtr Hwnd;
    public AutomationElement? SkipButton;
    public string Detail = "";
    public long ElapsedMs;
    /// <summary>Player element reports YouTube's "ad-showing" / "ad-interrupting" class.</summary>
    public bool AdShowing;
    /// <summary>True when the cheap player-subtree path was used (false = whole-page fallback).</summary>
    public bool PlayerPath;
}

/// <summary>
/// v1.0.2 fast scanner for the watcher thread. Caches the YouTube player element ("YouTube Video Player"
/// group, class html5-video-player) per window and only re-queries that subtree:
///   - ad state: one ClassName read on the player (YouTube adds "ad-showing" while an ad runs), ~1 ms
///   - Skip button: FindAll Buttons under the player only (~8-40 elements, 5-30 ms) instead of the whole
///     page (300+ buttons, 430-500 ms on a normal watch page, measured on Tim's PC in Opera).
/// The cache drops itself when the element goes stale or the window changes.
/// </summary>
internal sealed class SkipAdPlayerScanner
{
    private IntPtr _hwnd;
    private AutomationElement? _player;
    private long _lastFindMs = -100000;
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private long _foundMs = -100000;

    /// <summary>Drop the cached player (and the 3 s find throttle) so the next Scan finds it in the current page.</summary>
    public void Reset() { _player = null; _hwnd = IntPtr.Zero; _lastFindMs = -100000; }

    /// <summary>
    /// <paramref name="buttons"/> = false: only read the ad class (cheapest, used while idle).
    /// Returns Idle with PlayerPath=false when the player can't be found (caller falls back to the full scan).
    /// </summary>
    public SkipAdScan Scan(IntPtr hwnd, bool buttons)
    {
        var res = new SkipAdScan { Hwnd = hwnd, State = SkipAdState.Idle };
        var sw = Stopwatch.StartNew();
        try
        {
            if (hwnd != _hwnd) { _player = null; _hwnd = hwnd; }
            // A cached player element can outlive its page (tab switch / navigation in the same window) and keep
            // answering with its last ClassName, so "ad-showing" would never be seen again: re-find it every 15 s
            // while idle (the watcher also resets on every window-title change).
            if (_player != null && !buttons && _clock.ElapsedMilliseconds - _foundMs > 15000) { _player = null; _lastFindMs = -100000; }
            if (_player == null)
            {
                // Don't hammer FindFirst when the page has no player (e.g. YouTube home): retry every 3 s.
                if (_clock.ElapsedMilliseconds - _lastFindMs < 3000) { res.Detail = "no player element"; return res; }
                _lastFindMs = _clock.ElapsedMilliseconds;
                _player = SkipAdService.FindPlayer(hwnd);
                if (_player == null) { res.Detail = "no player element"; return res; }
                _foundMs = _clock.ElapsedMilliseconds;
            }
            string cls;
            try { cls = _player.Current.ClassName ?? ""; }
            catch { _player = null; _lastFindMs = -100000; res.Detail = "player went stale"; return res; }
            res.PlayerPath = true;
            res.AdShowing = SkipAdService.IsAdClass(cls);
            if (!buttons) { res.State = res.AdShowing ? SkipAdState.AdPlaying : SkipAdState.Idle; return res; }
            try { SkipAdService.ClassifyButtons(SkipAdService.PlayerButtons(_player), res); }
            catch { _player = null; _lastFindMs = -100000; res.PlayerPath = false; res.Detail = "player went stale"; return res; }
            if (res.State == SkipAdState.Idle && res.AdShowing) { res.State = SkipAdState.AdPlaying; res.Detail = "ad playing (not skippable yet)"; }
            return res;
        }
        catch (Exception ex) { _player = null; res.Detail = "UIA: " + ex.GetType().Name; return res; }
        finally { res.ElapsedMs = sw.ElapsedMilliseconds; }
    }
}

/// <summary>
/// v1.0.1 Skip Ad engine. All UIA work happens on background threads (never the WPF UI thread).
///
/// Why the old F8 was intermittent:
///   1. Chromium (Opera / Chrome / Edge / Brave) builds the web-content accessibility tree lazily: the first
///      UIA query after accessibility is enabled sees only a stub (no Document / no Skip button), and the
///      old code did one shot (+ one retry 180 ms later), so the first press usually missed.
///   2. The UIA search ran synchronously on the WPF UI thread (plus Thread.Sleep in the click fallback), so
///      a slow cross-process Chromium tree walk could time out / stall and the result was lost.
///   3. The broad fallback was a 1600-node breadth-first walk from the window root that rarely reached the
///      player overlay on a full YouTube page, and exact-name conditions missed "Skip" variants.
///   4. When nothing was found it clicked four blind spots + Enter in the lower-right of the window.
///   5. Window choice: the first browser window (any tab) could win over the one showing YouTube.
/// Fix: pick the browser window whose title says YouTube (Live-linked pane first), nudge Chromium's
/// accessibility (WM_GETOBJECT to Chrome_RenderWidgetHostHWND), then poll for up to ~4.5 s: scan the web
/// Document's Buttons (cached Name/IsOffscreen/BoundingRectangle) for a Skip button, InvokePattern it, and
/// verify it disappeared; click the button's own rect only if Invoke is unavailable. No blind clicks.
/// </summary>
internal static class SkipAdService
{
    // v1.0.2: last ad state the watcher read from a YouTube player (shared with the Media card so an ad never
    // replaces the video's title, and the movie shows again the moment the ad ends).
    private static long _adHwnd, _adSeenTicks, _adClearTicks;

    /// <summary>Watcher: record the player's ad class for <paramref name="hwnd"/>.</summary>
    public static void NoteAdState(IntPtr hwnd, bool adShowing)
    {
        if (hwnd == IntPtr.Zero) return;
        if (adShowing)
        {
            Interlocked.Exchange(ref _adHwnd, hwnd.ToInt64());
            Interlocked.Exchange(ref _adSeenTicks, DateTime.UtcNow.Ticks);
        }
        else if (Interlocked.Read(ref _adHwnd) == hwnd.ToInt64())
            Interlocked.Exchange(ref _adClearTicks, DateTime.UtcNow.Ticks);
    }

    /// <summary>An ad is (very recently) showing in <paramref name="hwnd"/>'s YouTube player.</summary>
    public static bool IsAdShowing(IntPtr hwnd, double withinSec = 1.5)
    {
        if (hwnd == IntPtr.Zero || Interlocked.Read(ref _adHwnd) != hwnd.ToInt64()) return false;
        long seen = Interlocked.Read(ref _adSeenTicks), clear = Interlocked.Read(ref _adClearTicks);
        if (clear > seen) return false;
        return (DateTime.UtcNow - new DateTime(seen, DateTimeKind.Utc)).TotalSeconds <= withinSec;
    }

    private const uint WM_GETOBJECT = 0x003D;
    private const int OBJID_CLIENT = -4;
    private const int UiaRootObjectId = -25;
    private const uint SMTO_ABORTIFHUNG = 0x0002;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam,
        uint flags, uint timeout, out IntPtr result);

    private delegate bool EnumChildProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr parent, EnumChildProc cb, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder name, int max);

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    /// <summary>Milliseconds since the last keyboard/mouse input anywhere in the session.</summary>
    public static uint UserIdleMs()
    {
        try
        {
            var li = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
            if (!GetLastInputInfo(ref li)) return uint.MaxValue;
            return unchecked((uint)Environment.TickCount - li.dwTime);
        }
        catch { return uint.MaxValue; }
    }

    public static string GetTitle(IntPtr hwnd)
    {
        try
        {
            int len = NativeMethods.GetWindowTextLength(hwnd);
            if (len <= 0) return "";
            var sb = new StringBuilder(len + 1);
            NativeMethods.GetWindowText(hwnd, sb, sb.Capacity);
            return sb.ToString();
        }
        catch { return ""; }
    }

    public static bool IsYouTubeTitle(string t) => t.IndexOf("YouTube", StringComparison.OrdinalIgnoreCase) >= 0;

    private static string ProcessNameOf(IntPtr hwnd, Dictionary<uint, string>? cache = null)
    {
        NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0) return "";
        if (cache != null && cache.TryGetValue(pid, out var n)) return n;
        try
        {
            using var p = Process.GetProcessById((int)pid);
            n = p.ProcessName;
        }
        catch { n = ""; }
        if (cache != null) cache[pid] = n;
        return n;
    }

    /// <summary>Top-level browser windows whose title (active tab) contains YouTube, in z-order.</summary>
    public static List<IntPtr> FindYouTubeWindows()
    {
        var list = new List<IntPtr>();
        var cache = new Dictionary<uint, string>();
        var shell = NativeMethods.GetShellWindow();
        NativeMethods.EnumWindows((h, _) =>
        {
            if (h == IntPtr.Zero || h == shell) return true;
            if (!NativeMethods.IsWindowVisible(h) && !NativeMethods.IsIconic(h)) return true;
            string t = GetTitle(h);
            if (t.Length == 0 || !IsYouTubeTitle(t)) return true;
            if (!MediaSessionService.IsBrowser(ProcessNameOf(h, cache))) return true;
            list.Add(h);
            return true;
        }, IntPtr.Zero);
        return list;
    }

    /// <summary>v1.1.5: visible, non-minimized browser top-level windows (any title).</summary>
    public static List<IntPtr> FindBrowserWindows()
    {
        var list = new List<IntPtr>();
        var cache = new Dictionary<uint, string>();
        var shell = NativeMethods.GetShellWindow();
        NativeMethods.EnumWindows((h, _) =>
        {
            if (h == IntPtr.Zero || h == shell || !NativeMethods.IsWindowVisible(h) || NativeMethods.IsIconic(h)) return true;
            if (GetTitle(h).Length == 0 || !MediaSessionService.IsBrowser(ProcessNameOf(h, cache))) return true;
            list.Add(h);
            return true;
        }, IntPtr.Zero);
        return list;
    }

    /// <summary>Preferred (Live-linked) window if it shows YouTube, else foreground YouTube window, else topmost.</summary>
    public static IntPtr PickTarget(IntPtr preferred)
    {
        if (preferred != IntPtr.Zero && NativeMethods.IsWindow(preferred) && IsYouTubeTitle(GetTitle(preferred)))
            return preferred;
        var list = FindYouTubeWindows();
        if (list.Count == 0)
            return preferred != IntPtr.Zero && NativeMethods.IsWindow(preferred)
                && MediaSessionService.IsBrowser(ProcessNameOf(preferred)) ? preferred : IntPtr.Zero;
        var fg = NativeMethods.GetForegroundWindow();
        foreach (var h in list) if (h == fg) return h;
        foreach (var h in list) if (!NativeMethods.IsIconic(h)) return h;
        return list[0];
    }

    /// <summary>Ask Chromium's render widget for its accessibility root (turns renderer accessibility on).</summary>
    public static void NudgeAccessibility(IntPtr top)
    {
        try
        {
            var targets = new List<IntPtr>();
            EnumChildWindows(top, (c, _) =>
            {
                var sb = new StringBuilder(64);
                GetClassName(c, sb, sb.Capacity);
                if (sb.ToString().StartsWith("Chrome_RenderWidgetHostHWND", StringComparison.Ordinal))
                    targets.Add(c);
                return true;
            }, IntPtr.Zero);
            targets.Add(top);
            foreach (var h in targets)
            {
                SendMessageTimeout(h, WM_GETOBJECT, IntPtr.Zero, (IntPtr)OBJID_CLIENT, SMTO_ABORTIFHUNG, 150, out _);
                SendMessageTimeout(h, WM_GETOBJECT, IntPtr.Zero, (IntPtr)UiaRootObjectId, SMTO_ABORTIFHUNG, 150, out _);
            }
        }
        catch { }
    }

    private static bool IsSkipName(string n)
    {
        var s = n.Trim().ToLowerInvariant();
        if (s.Length == 0 || s.Length > 32) return false;
        if (s.Contains("navigation")) return false; // YouTube masthead "Skip navigation"
        // NOT "Skip ahead to live broadcast." (player control) - only Skip / Skip ad(s) / Skip advertisement.
        return s == "skip" || s == "skip ad" || s == "skip ads" || s.StartsWith("skip ad ") || s.StartsWith("skip ads ")
            || s == "skip advertisement" || s.StartsWith("skip >") || s.StartsWith("skip \u25b8") || s.StartsWith("skip \u25ba");
    }

    /// <summary>YouTube marks the player element with these classes while an ad runs.</summary>
    public static bool IsAdClass(string cls) =>
        cls.Contains("ad-showing", StringComparison.Ordinal) || cls.Contains("ad-interrupting", StringComparison.Ordinal);

    /// <summary>The YouTube player group under the web Document (name "YouTube Video Player"), or null.</summary>
    public static AutomationElement? FindPlayer(IntPtr hwnd, bool ytOnly = false)
    {
        foreach (var doc in WebDocuments(AutomationElement.FromHandle(hwnd), ytOnly))
        {
            var p = doc.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.NameProperty, "YouTube Video Player"));
            if (p != null) return p;
            // Fullscreen / localized label: fall back to the first Group whose class is html5-video-player.
            var groups = doc.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Group));
            foreach (AutomationElement g in groups)
            {
                try { if ((g.Current.ClassName ?? "").Contains("html5-video-player", StringComparison.Ordinal)) return g; } catch { }
            }
        }
        return null;
    }

    /// <summary>
    /// v1.1.4: every web Document in the browser window, YouTube pages first. Opera can expose more than one
    /// (Oct 18: a "Speed Dial" document came first in the same window), and FindFirst(Document) then scanned the
    /// wrong page: 0 buttons, "no Skip button in tree" in 28 ms while YouTube's Skip button was on screen.
    /// </summary>
    /// v1.1.5: ytOnly = only YouTube-named Documents (Opera split view: the window title is the focused pane's,
    /// e.g. Chess.com, so the window is found by its documents and only the YouTube pane may be pressed).
    public static List<AutomationElement> WebDocuments(AutomationElement root, bool ytOnly = false)
    {
        var all = new List<AutomationElement>();
        try
        {
            foreach (AutomationElement d in root.FindAll(TreeScope.Descendants,
                         new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document)))
                all.Add(d);
        }
        catch { }
        static bool Yt(AutomationElement d) { try { return (d.Current.Name ?? "").Contains("YouTube", StringComparison.OrdinalIgnoreCase); } catch { return false; } }
        var yt = all.Where(Yt).ToList();
        return yt.Count > 0 || ytOnly ? yt : all;
    }

    /// <summary>Buttons + Text under the player with Name/IsOffscreen/BoundingRectangle cached.</summary>
    public static AutomationElementCollection PlayerButtons(AutomationElement player)
    {
        var cr = new CacheRequest { AutomationElementMode = AutomationElementMode.Full, TreeScope = TreeScope.Element };
        cr.Add(AutomationElement.NameProperty);
        cr.Add(AutomationElement.IsOffscreenProperty);
        cr.Add(AutomationElement.BoundingRectangleProperty);
        using (cr.Activate())
        {
            return player.FindAll(TreeScope.Descendants, new OrCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text)));
        }
    }

    /// <summary>Sets SkipButton / State (Ready, AdPlaying, Idle) from a cached element collection.</summary>
    public static void ClassifyButtons(AutomationElementCollection els, SkipAdScan res)
    {
        bool ad = false;
        foreach (AutomationElement b in els)
        {
            string n;
            try { n = b.Cached.Name ?? ""; } catch { continue; }
            if (n.Length == 0) continue;
            if (IsSkipName(n))
            {
                bool off = false;
                try { off = b.Cached.IsOffscreen; } catch { }
                if (res.SkipButton == null || !off) res.SkipButton = b;
                ad = true;
            }
            else if (IsAdHint(n)) ad = true;
        }
        if (res.SkipButton != null) { res.State = SkipAdState.Ready; res.Detail = "Skip button present"; }
        else if (ad) { res.State = SkipAdState.AdPlaying; res.Detail = "ad playing (not skippable yet)"; }
        else res.State = SkipAdState.Idle;
    }

    private static bool IsAdHint(string n)
    {
        var s = n.Trim().ToLowerInvariant();
        return s.Contains("my ad center") || s.Contains("ad info") || s.Contains("why this ad") || s.Contains("about this ad")
            || s.Contains("ad options") || s.StartsWith("visit advertiser") || s == "sponsored" || s.StartsWith("ad \u00b7")
            || s == "ad" || (s.StartsWith("ad ") && s.Contains(" of ") && s.Length < 16);
    }

    /// <summary>One cheap pass: web Document -> Buttons (cached props). Safe to call from any non-UI thread.</summary>
    public static SkipAdScan Scan(IntPtr hwnd, bool ytOnly = false)
    {
        var res = new SkipAdScan { Hwnd = hwnd, State = SkipAdState.NoPlayer };
        if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd)) return res;
        var sw = Stopwatch.StartNew();
        try
        {
            // Fast path (v1.0.2): just the player subtree (~100 ms cold vs ~500 ms for the whole page).
            var player = FindPlayer(hwnd, ytOnly);
            if (player != null)
            {
                res.PlayerPath = true;
                try { res.AdShowing = IsAdClass(player.Current.ClassName ?? ""); } catch { }
                ClassifyButtons(PlayerButtons(player), res);
                if (res.State == SkipAdState.Idle && res.AdShowing) { res.State = SkipAdState.AdPlaying; res.Detail = "ad playing (not skippable yet)"; }
                if (res.State != SkipAdState.Idle) return res;
            }
            var root = AutomationElement.FromHandle(hwnd);
            var cr = new CacheRequest { AutomationElementMode = AutomationElementMode.Full, TreeScope = TreeScope.Element };
            cr.Add(AutomationElement.NameProperty);
            cr.Add(AutomationElement.IsOffscreenProperty);
            cr.Add(AutomationElement.BoundingRectangleProperty);
            AutomationElementCollection? buttons = null;
            var docs = WebDocuments(root, ytOnly);
            if (docs.Count == 0)
            {
                res.State = SkipAdState.Idle;
                res.Detail = "web content tree not ready";
                return res;
            }
            var all = new List<AutomationElement>();
            using (cr.Activate())
            {
                foreach (var doc in docs)
                {
                    buttons = doc.FindAll(TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
                    foreach (AutomationElement b in buttons) all.Add(b);
                }
            }
            res.State = SkipAdState.Idle;
            bool ad = false;
            foreach (AutomationElement b in all)
            {
                string n;
                try { n = b.Cached.Name ?? ""; } catch { continue; }
                if (n.Length == 0) continue;
                if (IsSkipName(n))
                {
                    bool off = false;
                    try { off = b.Cached.IsOffscreen; } catch { }
                    if (res.SkipButton == null || !off) res.SkipButton = b;
                    ad = true;
                }
                else if (IsAdHint(n)) ad = true;
            }
            if (res.SkipButton != null) { res.State = SkipAdState.Ready; res.Detail = "Skip button present"; }
            else if (ad) { res.State = SkipAdState.AdPlaying; res.Detail = "ad playing (not skippable yet)"; }
        }
        catch (Exception ex)
        {
            res.State = SkipAdState.Idle;
            res.Detail = "UIA: " + ex.GetType().Name;
        }
        finally { res.ElapsedMs = sw.ElapsedMilliseconds; }
        return res;
    }

    /// <summary>Chromium "Picture in Picture" windows owned by a browser (the video keeps playing there while its tab is in the background).</summary>
    public static List<IntPtr> FindPipWindows()
    {
        var list = new List<IntPtr>();
        var cache = new Dictionary<uint, string>();
        NativeMethods.EnumWindows((h, _) =>
        {
            if (h == IntPtr.Zero || !NativeMethods.IsWindowVisible(h)) return true;
            if (!string.Equals(GetTitle(h), "Picture in Picture", StringComparison.OrdinalIgnoreCase)) return true;
            if (!MediaSessionService.IsBrowser(ProcessNameOf(h, cache))) return true;
            list.Add(h);
            return true;
        }, IntPtr.Zero);
        return list;
    }

    /// <summary>
    /// Read-only scan of a PiP window for Chromium's own "Skip ad" button (shown when the page registers the
    /// media-session "skipad" action). Ready when found; otherwise Idle. Never activates the window.
    /// </summary>
    public static SkipAdScan ScanPip(IntPtr hwnd)
    {
        var res = new SkipAdScan { Hwnd = hwnd, State = SkipAdState.Idle };
        var sw = Stopwatch.StartNew();
        try
        {
            var root = AutomationElement.FromHandle(hwnd);
            var cr = new CacheRequest { AutomationElementMode = AutomationElementMode.Full, TreeScope = TreeScope.Element };
            cr.Add(AutomationElement.NameProperty);
            cr.Add(AutomationElement.IsOffscreenProperty);
            cr.Add(AutomationElement.BoundingRectangleProperty);
            AutomationElementCollection els;
            using (cr.Activate())
                els = root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
            foreach (AutomationElement b in els)
            {
                string n;
                try { n = b.Cached.Name ?? ""; } catch { continue; }
                if (IsSkipName(n)) { res.SkipButton = b; res.State = SkipAdState.Ready; res.Detail = "PiP Skip button present"; break; }
            }
        }
        catch (Exception ex) { res.Detail = "PiP UIA: " + ex.GetType().Name; }
        finally { res.ElapsedMs = sw.ElapsedMilliseconds; }
        return res;
    }

    /// <summary>Name of a background browser tab ending in "- YouTube" (tab strip TabItems), or null. Read-only.</summary>
    public static string? FindBackgroundYouTubeTab()
    {
        try
        {
            var cache = new Dictionary<uint, string>();
            var wins = new List<IntPtr>();
            NativeMethods.EnumWindows((h, _) =>
            {
                if (h != IntPtr.Zero && NativeMethods.IsWindowVisible(h) && GetTitle(h).Length > 0
                    && MediaSessionService.IsBrowser(ProcessNameOf(h, cache))) wins.Add(h);
                return true;
            }, IntPtr.Zero);
            var cond = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem);
            foreach (var h in wins)
            {
                foreach (AutomationElement t in AutomationElement.FromHandle(h).FindAll(TreeScope.Descendants, cond))
                {
                    string n;
                    try { n = t.Current.Name ?? ""; } catch { continue; }
                    if (n.TrimEnd().EndsWith("- YouTube", StringComparison.OrdinalIgnoreCase)) return n;
                }
            }
        }
        catch { }
        return null;
    }

    /// <summary>
    /// v1.0.10 (extension "skip-ready"): press YouTube's Skip button with a UIA Invoke only. No window activation,
    /// no mouse/keyboard fallback. Looks in browser windows whose active tab is YouTube (foreground first).
    /// </summary>
    public static (bool ok, string detail) InvokeSkipFromExtension()
    {
        var sw = Stopwatch.StartNew();
        var list = FindYouTubeWindows();
        // v1.1.5: Opera split view - YouTube pane visible, other pane focused, window title not YouTube.
        // Fall back to every visible browser window, scanning only YouTube-named Documents (pane) in it.
        bool split = list.Count == 0;
        if (split) list = FindBrowserWindows();
        if (list.Count == 0) return (false, "no browser window with YouTube");
        var fg = NativeMethods.GetForegroundWindow();
        list.Sort((a, b) => (b == fg ? 1 : 0) - (a == fg ? 1 : 0));
        string last = split ? "split: no YouTube pane" : "";
        foreach (var h in list)
        {
            try
            {
                NudgeAccessibility(h);           // WM_GETOBJECT only: turns Chromium's tree on, no focus change
                var scan = Scan(h, ytOnly: split);
                if (scan.State != SkipAdState.Ready || scan.SkipButton == null) { last = scan.Detail.Length > 0 ? scan.Detail : "no Skip button in tree"; continue; }
                if (scan.SkipButton.TryGetCurrentPattern(InvokePattern.Pattern, out object? pat) && pat is InvokePattern inv)
                {
                    inv.Invoke();
                    return (true, $"UIA invoke on 0x{h.ToInt64():X}{(split ? " split" : "")} ({sw.ElapsedMilliseconds} ms)");
                }
                last = "Skip button has no Invoke pattern";
            }
            catch (Exception ex) { last = "UIA: " + ex.GetType().Name; }
        }
        return (false, last + $" ({sw.ElapsedMilliseconds} ms)");
    }

    /// <summary>InvokePattern first; click the button's own rect only if Invoke is unavailable.</summary>
    public static bool TryInvoke(AutomationElement btn, out string how)
    {
        how = "";
        try
        {
            if (btn.TryGetCurrentPattern(InvokePattern.Pattern, out object? pat) && pat is InvokePattern inv)
            {
                inv.Invoke();
                how = "UIA invoke";
                return true;
            }
        }
        catch { }
        try
        {
            var r = btn.Current.BoundingRectangle;
            if (!r.IsEmpty && r.Width > 2 && r.Height > 2 && !double.IsInfinity(r.Left))
            {
                NativeMethods.ClickScreenPoint((int)(r.Left + r.Width / 2), (int)(r.Top + r.Height / 2));
                how = "click on Skip button";
                return true;
            }
        }
        catch { }
        return false;
    }
}
