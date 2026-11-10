using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Text;

namespace WorkflowDashboard;

public sealed class WindowEntry : INotifyPropertyChanged
{
    public IntPtr Hwnd { get; init; }
    public string Title { get; init; } = "";
    public uint ProcessId { get; init; }
    public string ProcessName { get; init; } = "";
    public ImageSource? Icon { get; init; }

    private bool _isLinked;
    /// <summary>True when this HWND is assigned to a Live/Working pane (not parked).</summary>
    public bool IsLinked
    {
        get => _isLinked;
        set
        {
            if (_isLinked == value) return;
            _isLinked = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(LinkBrush));
            OnPropertyChanged(nameof(LinkTooltip));
            OnPropertyChanged(nameof(LinkLabel));
        }
    }

    private bool _isNowPlaying;
    /// <summary>True when this process is the active GSMTC now-playing session.</summary>
    public bool IsNowPlaying
    {
        get => _isNowPlaying;
        set
        {
            if (_isNowPlaying == value) return;
            _isNowPlaying = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(NowPlayingVisibility));
        }
    }

    public System.Windows.Visibility NowPlayingVisibility =>
        IsNowPlaying ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

    public Brush LinkBrush => IsLinked
        ? new SolidColorBrush(Color.FromRgb(0x3D, 0xC4, 0x6A))   // green = linked
        : new SolidColorBrush(Color.FromRgb(0xE0, 0x5A, 0x5A)); // red = unlinked

    /// <summary>Accessible label for the green/red link control.</summary>
    public string LinkLabel => IsLinked ? "Linked" : "Unlinked";

    public string LinkTooltip => IsLinked
        ? "Linked - click to Unlink and restore prior bounds"
        : "Unlinked - click to link to the active pane";

    public string ShortTitle
    {
        get
        {
            var t = string.IsNullOrWhiteSpace(Title) ? ProcessName : Title;
            t = TitleSanitizer.Clean(t);
            if (t.Length <= 26) return t;
            return t[..23] + "...";
        }
    }

    public string FullTooltip => string.IsNullOrWhiteSpace(ProcessName)
        ? Title
        : $"{Title}\n{ProcessName}";

    public string Display => string.IsNullOrWhiteSpace(ProcessName)
        ? Title
        : $"{Title}  [{ProcessName}]";

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public override string ToString() => ShortTitle;
}

/// <summary>Strip control / replacement / private-use junk from window titles for the list UI.</summary>
internal static class TitleSanitizer
{
    public static string Clean(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = RepairMojibake(s);
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
        {
            if (ch == '\uFFFD') continue; // replacement char
            if (ch >= '\u200B' && ch <= '\u200F') continue; // zero-width / direction marks (Edge titles)
            if (ch == '\uFEFF') continue;
            if (char.IsControl(ch)) continue;
            // Skip BMP private-use (often boxes on Segoe UI)
            if (ch >= '\uE000' && ch <= '\uF8FF') continue;
            sb.Append(ch);
        }
        var cleaned = sb.ToString().Trim();
        while (cleaned.Contains("  "))
            cleaned = cleaned.Replace("  ", " ");
        return cleaned;
    }

    private static readonly System.Text.RegularExpressions.Regex MojibakeRx =
        new(@"[\u00C2\u00C3\u00E2\u00C5\u00C6][\u0080-\u00BF\u0152\u0153\u0160\u0161\u0178\u017D\u017E\u0192\u02C6\u02DC\u2013\u2014\u2018-\u201E\u2020-\u2022\u2026\u2030\u2039\u203A\u20AC\u2122]",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static Encoding? _cp1252;

    /// <summary>
    /// Undo UTF-8-read-as-Windows-1252 damage ("Ã¢â‚¬â€" for an em dash), possibly applied several times.
    /// Leaves normal text alone; if it can't be repaired cleanly the garbage runs are dropped.
    /// </summary>
    public static string RepairMojibake(string s)
    {
        if (string.IsNullOrEmpty(s) || !MojibakeRx.IsMatch(s)) return s;
        try
        {
            if (_cp1252 == null)
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                _cp1252 = Encoding.GetEncoding(1252, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
            }
            var utf8 = new UTF8Encoding(false, true);
            string cur = s;
            for (int i = 0; i < 8 && MojibakeRx.IsMatch(cur); i++)
            {
                string next;
                try { next = utf8.GetString(_cp1252.GetBytes(cur)); }
                catch { break; }
                if (next == cur) break;
                cur = next;
            }
            if (!MojibakeRx.IsMatch(cur)) return cur;
        }
        catch { }
        // Couldn't repair: drop the junk runs instead of showing them.
        var stripped = System.Text.RegularExpressions.Regex.Replace(s,
            @"[\u00C2\u00C3\u00E2\u00C5\u00C6][^\sA-Za-z0-9]+", " - ");
        stripped = System.Text.RegularExpressions.Regex.Replace(stripped, @"(\s-\s)+", " - ");
        return stripped.Trim(' ', '-');
    }
}
