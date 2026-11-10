using System.Text.RegularExpressions;

namespace WorkflowDashboard;

/// <summary>
/// v1.0.11 video progress from the yt-adskip extension ("progress" events: t, dur, rate, paused, title per tab).
/// Opera often publishes no SMTC session (audio fallback) or one without a timeline, so the Media card had no
/// progress bar / time for YouTube. The card uses this when SMTC has no duration and the title matches.
/// </summary>
internal sealed partial class MediaSessionService
{
    private sealed record ExtProg(string Title, double Pos, double Dur, double Rate, bool Paused, DateTime AtUtc);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, ExtProg> _extProg = new();
    private static readonly Regex ProgRx = new(@"(?:^|\s)(t|dur|rate|paused|tab)=(\S+)", RegexOptions.Compiled);
    private static readonly Regex TitleRx = new(@"(?:^|\s)title=(.*?)(?:\s+tabActive=|\s+tab=|$)", RegexOptions.Compiled);

    /// <summary>Bridge thread: store one "progress" event (detail = "k=v ..." from ExtensionBridge).</summary>
    public static void NoteExtProgress(string detail)
    {
        try
        {
            double t = 0, dur = 0, rate = 1; bool paused = false; string tab = "?";
            foreach (Match m in ProgRx.Matches(detail))
            {
                string k = m.Groups[1].Value, v = m.Groups[2].Value;
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                if (k == "t") double.TryParse(v, System.Globalization.NumberStyles.Float, inv, out t);
                else if (k == "dur") double.TryParse(v, System.Globalization.NumberStyles.Float, inv, out dur);
                else if (k == "rate") double.TryParse(v, System.Globalization.NumberStyles.Float, inv, out rate);
                else if (k == "paused") paused = v.Equals("True", StringComparison.OrdinalIgnoreCase);
                else if (k == "tab") tab = v;
            }
            var tm = TitleRx.Match(detail);
            string title = tm.Success ? CleanYtTitle(tm.Groups[1].Value) : "";
            if (dur <= 1 || !double.IsFinite(dur)) { _extProg.TryRemove(tab, out _); return; }
            if (_extProg.Count > 16) _extProg.Clear();
            _extProg[tab] = new ExtProg(title, t, dur, rate is > 0 and <= 2 ? rate : 1, paused, DateTime.UtcNow);
        }
        catch { }
    }

    private static string CleanYtTitle(string s)
    {
        s = Regex.Replace(s ?? "", @"^\(\d+\)\s*", "").Trim();
        foreach (var suf in new[] { " - YouTube", " \u2014 YouTube" })
            if (s.EndsWith(suf, StringComparison.OrdinalIgnoreCase)) s = s[..^suf.Length].Trim();
        return TitleSanitizer.Clean(s);
    }

    // v1.1.1: last SMTC timeline per title. Opera reports duration 0 for the same video once its tab is in the
    // background (media.log 03:12:32 dur=0 right after dur=1832), so the bar vanished in exactly the card modes.
    private readonly Dictionary<string, (double Pos, double Dur, DateTime AtUtc, bool Playing)> _tlCache = new(StringComparer.OrdinalIgnoreCase);

    private static string TlKey(string t)
    {
        string k = TitleSanitizer.Clean(t ?? "").TrimEnd('\u2026', '.').Trim();
        return k.Length > 40 ? k[..40] : k;
    }

    private void RememberTimeline(string title, double pos, double dur, DateTime atUtc, bool playing)
    {
        string k = TlKey(title);
        if (k.Length < 3 || dur <= 1) return;
        if (_tlCache.Count > 32) _tlCache.Clear();
        _tlCache[k] = (pos, dur, atUtc > DateTime.MinValue ? atUtc : DateTime.UtcNow, playing);
    }

    /// <summary>Last known timeline for this title (within 30 min), extrapolated if it was playing.</summary>
    public bool TryCachedTimeline(string title, out double pos, out double dur)
    {
        pos = dur = 0;
        if (!_tlCache.TryGetValue(TlKey(title), out var c) || (DateTime.UtcNow - c.AtUtc).TotalMinutes > 30) return false;
        dur = c.Dur;
        pos = Math.Clamp(c.Pos + (c.Playing && NowPlayingStatus != "Paused" ? (DateTime.UtcNow - c.AtUtc).TotalSeconds : 0), 0, dur);
        return true;
    }

    /// <summary>v1.1.2: the extension tab id (bridge key) reporting the shown title, or the single playing tab.</summary>
    public static string? FindExtTab(string shownTitle)
    {
        var now = DateTime.UtcNow;
        var recent = _extProg.Where(kv => (now - kv.Value.AtUtc).TotalSeconds <= 900).ToList();
        if (recent.Count == 0) return null;
        string want = TitleSanitizer.Clean(shownTitle ?? "").TrimEnd('\u2026', '.').Trim();
        if (want.Length > 24) want = want[..24];
        if (want.Length >= 3)
        {
            var hit = recent.Where(kv => kv.Value.Title.IndexOf(want, StringComparison.OrdinalIgnoreCase) >= 0)
                            .OrderByDescending(kv => kv.Value.AtUtc).Select(kv => kv.Key).FirstOrDefault();
            if (hit != null) return hit;
        }
        if (want.Length >= 3) return null;   // a named source that no YouTube tab reports: not ours
        var playing = recent.Where(kv => !kv.Value.Paused).ToList();
        if (playing.Count == 1) return playing[0].Key;
        return recent.Count == 1 ? recent[0].Key : null;
    }

    /// <summary>Extension progress for the shown YouTube title (fresh within 20 s), extrapolated to now.</summary>
    public bool TryExtProgress(string shownTitle, out double pos, out double dur, out bool paused)
    {
        pos = dur = 0; paused = false;
        var now = DateTime.UtcNow;
        // v1.1.1: a background tab's timers are throttled by the browser (progress may arrive only once a minute or
        // less), and the card lost its bar 20 s later. A title-matched report stays usable for 15 min, extrapolated.
        var recent = _extProg.Values.Where(p => (now - p.AtUtc).TotalSeconds <= 900).ToList();
        var fresh = recent.Where(p => (now - p.AtUtc).TotalSeconds <= 20).ToList();
        if (recent.Count == 0) return false;
        string want = TitleSanitizer.Clean(shownTitle ?? "").TrimEnd('\u2026', '.').Trim();
        if (want.Length > 24) want = want[..24];
        ExtProg? hit = want.Length >= 3
            ? recent.Where(p => p.Title.IndexOf(want, StringComparison.OrdinalIgnoreCase) >= 0).OrderByDescending(p => p.AtUtc).FirstOrDefault()
            : null;
        // No usable title (audio fallback "unknown tab"): only when exactly one tab reports a playing video.
        if (hit == null && want.Length < 3)
        {
            var playing = fresh.Where(p => !p.Paused).ToList();
            if (playing.Count == 1) hit = playing[0];
        }
        if (hit == null) return false;
        dur = hit.Dur;
        paused = hit.Paused;
        pos = hit.Pos + (hit.Paused ? 0 : (now - hit.AtUtc).TotalSeconds * hit.Rate);
        pos = Math.Clamp(pos, 0, dur);
        return true;
    }
}
