using System.Text.RegularExpressions;

namespace WorkflowDashboard;

/// <summary>
/// v1.0.2: name the media SITE for browser-hosted sessions ("YouTube - title" instead of "Opera - title").
/// Detected from the tab/window title (browser suffix already stripped by CleanWindowTitle) or the SMTC title.
/// Each rule's regex both detects the site and strips the duplicated site suffix/prefix from a title.
/// </summary>
internal static class MediaSiteDetector
{
    private const RegexOptions Opt = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    private const string Sep = @"\s*[-\u2013\u2014|\u00B7:]\s*";

    private static readonly (string Site, Regex Rx)[] Rules =
    {
        ("YouTube Music", new Regex(Sep + @"YouTube Music\s*$", Opt)),
        ("YouTube", new Regex(Sep + @"YouTube\s*$", Opt)),
        ("Twitch", new Regex(Sep + @"Twitch\s*$", Opt)),
        ("Kick", new Regex(Sep + @"Kick\s*$", Opt)),
        ("SoundCloud", new Regex(Sep + @"[^-\u2013\u2014|]*SoundCloud\s*$", Opt)),
        ("Netflix", new Regex(@"(" + Sep + @")?\bNetflix\s*$", Opt)),
        ("Prime Video", new Regex(@"(" + Sep + @")?(Amazon\s+)?Prime Video\s*$|^\s*(Amazon\s+)?Prime Video" + Sep, Opt)),
        ("Disney+", new Regex(@"(" + Sep + @")?Disney\+\s*$|^\s*Disney\+" + Sep, Opt)),
        ("Apple Music", new Regex(@"(" + Sep + @")?Apple Music\s*$|^\s*Apple Music" + Sep, Opt)),
        ("Tidal", new Regex(@"(" + Sep + @")?\bTIDAL\s*$|^\s*TIDAL" + Sep, Opt)),
        ("Deezer", new Regex(Sep + @"Deezer\s*$", Opt)),
        ("Pandora", new Regex(Sep + @"Pandora( Radio)?[^-\u2013\u2014|]*$", Opt)),
        ("Vimeo", new Regex(@"\s+(on\s+|[-\u2013\u2014|]\s*)Vimeo\s*$", Opt)),
        ("Spotify", new Regex(Sep + @"Spotify\s*$|^\s*Spotify\s*[-\u2013\u2014|]\s*Web Player\s*$", Opt)),
        // v1.0.2 feed sites (short autoplay clips never take over the Media section).
        ("Instagram", new Regex(Sep + @"Instagram\s*$|^\s*(\(\d+\)\s*)?Instagram\s*$", Opt)),
        ("TikTok", new Regex(Sep + @"TikTok\b.*$|\s+on\s+TikTok\s*$|^\s*TikTok\s*$", Opt)),
        ("Facebook", new Regex(Sep + @"Facebook\s*$|^\s*(\(\d+\)\s*)?Facebook\s*$", Opt)),
        ("X", new Regex(@"\s+/\s+X\s*$|\s+on\s+X:\s*", RegexOptions.CultureInvariant)),
    };

    /// <summary>Site for a browser tab title, or "" if unknown.</summary>
    public static string Detect(string? title)
    {
        string t = (title ?? "").Trim();
        if (t.Length == 0) return "";
        foreach (var (site, rx) in Rules)
            if (rx.IsMatch(t)) return site;
        // Spotify Web Player while playing: "<song> \u2022 <artist>"
        if (t.Contains(" \u2022 ")) return "Spotify";
        return "";
    }

    /// <summary>Remove the site's own suffix/prefix from a title ("Song - YouTube" -> "Song").</summary>
    public static string Strip(string? title, string site)
    {
        string t = (title ?? "").Trim();
        if (t.Length == 0 || string.IsNullOrEmpty(site)) return t;
        foreach (var (s, rx) in Rules)
        {
            if (s != site) continue;
            string r = rx.Replace(t, "").Trim();
            return r.Length > 0 ? r : t;
        }
        return t;
    }
}
