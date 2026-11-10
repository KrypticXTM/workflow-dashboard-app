using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WorkflowDashboard;

/// <summary>
/// v1.0.2 now-playing card + pin. The preview never mirrors Tim's browsing:
///  - native app (Spotify.exe ...): live window while visible; minimized / tray -> this card;
///  - browser AUDIO site (Spotify web, YouTube Music, SoundCloud, ...): always this card;
///  - browser VIDEO: live only while the playing tab is the active tab (title match) or via the parked PiP;
///    otherwise this card (video thumbnail + title).
/// Card: album art at full height (square art) with title / artist / progress beside it, or a wide video
/// thumbnail with a caption band. Pin toggle (Media header) = manual lock in MediaSessionService.
/// </summary>
public partial class MainWindow
{
    private Grid? _npCard;
    private Image? _npArt;
    private DockPanel? _npText;          // v1.0.12: bar + time docked at the bottom (never clipped)
    private string _npLastLog = "";
    private TextBlock? _npTitle, _npArtist, _npNote;
    // v1.1.1: self-drawn bar (was a themed ProgressBar: #0A0A0A track on the #050505 slot = invisible track, and a
    // few-pixel yellow fill early in a long video). Visible track, minimum fill width, explicit colors.
    private Grid? _npBar;
    private Border? _npFill;
    private double _npFrac;

    private void EnsureNowCard()
    {
        if (_npCard != null || MediaPreviewGrid == null) return;
        _npCard = new Grid { Visibility = Visibility.Collapsed, ClipToBounds = true };
        _npCard.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _npCard.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _npArt = new Image { Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
        RenderOptions.SetBitmapScalingMode(_npArt, BitmapScalingMode.HighQuality);
        // v1.0.2: title and artist wrap (up to 2 lines each, font steps down if needed; see FitNowCardText).
        _npTitle = new TextBlock { FontSize = NpTitleFont, FontWeight = FontWeights.Bold, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis };
        _npArtist = new TextBlock { FontSize = NpArtistFont, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0) };
        _npBar = new Grid { Height = 4, Margin = new Thickness(0, 6, 0, 0), SnapsToDevicePixels = true };
        _npBar.Children.Add(new Border { CornerRadius = new CornerRadius(2), Background = new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)) });
        _npFill = new Border { CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 3 };
        _npFill.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
        _npBar.Children.Add(_npFill);
        _npBar.SizeChanged += (_, _) => ApplyNpFill();
        _npNote = new TextBlock { FontSize = 10, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 4, 0, 0) };
        // v1.0.12: the text column was a StackPanel; with a 2-line title + 2-line channel it grew taller than the
        // slot and the card (ClipToBounds) cut off its LAST rows - exactly the progress bar and the time. Now the
        // bar and time are docked at the bottom and get their space first; title/artist take what is left.
        _npText = new DockPanel { LastChildFill = true, ClipToBounds = true };
        DockPanel.SetDock(_npNote, Dock.Bottom);
        DockPanel.SetDock(_npBar, Dock.Bottom);
        _npText.Children.Add(_npNote);
        _npText.Children.Add(_npBar);
        var head = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom, ClipToBounds = true };
        head.Children.Add(_npTitle);
        head.Children.Add(_npArtist);
        _npText.Children.Add(head);
        _npCard.Children.Add(_npArt);
        _npCard.Children.Add(_npText);
        _npText.SizeChanged += (_, e) => { if (Math.Abs(e.PreviousSize.Width - e.NewSize.Width) > 0.5) FitNowCardText(); };
        // Under the DWM thumbnail host (hidden while the card shows), above the legacy art/card/note.
        int at = MediaPreviewGrid.Children.IndexOf(MediaPreviewThumb);
        MediaPreviewGrid.Children.Insert(at < 0 ? MediaPreviewGrid.Children.Count : at, _npCard);
    }

    private void HideNowCard()
    {
        if (_npCard != null) _npCard.Visibility = Visibility.Collapsed;
    }

    /// <summary>Show the themed now-playing card for the shown source (art, title, artist, progress, note).</summary>
    /// <param name="textOnly">Title card (the preview is redundant): drop a wide video thumbnail so it reads like the
    /// background-tab card; square album art (music) is kept.</param>
    private void ShowNowCard(string note, bool textOnly = false)
    {
        EnsureNowCard();
        if (_npCard == null || _npArt == null || _npText == null || _npTitle == null || _npArtist == null || _npBar == null || _npNote == null) return;
        MediaPreviewArt.Visibility = Visibility.Collapsed;
        MediaPreviewCard.Visibility = Visibility.Collapsed;
        MediaPreviewNote.Visibility = Visibility.Collapsed;

        var art = _media.NowPlayingArt;
        string title = TitleSanitizer.Clean(string.IsNullOrWhiteSpace(_media.NowPlayingFullTitle) ? _media.NowPlayingTitle : _media.NowPlayingFullTitle);
        if (string.IsNullOrWhiteSpace(title)) title = TxtMediaNow?.Text ?? "";
        string artist = _media.NowPlayingArtist ?? "";
        string site = _media.NowPlayingSite ?? "";
        if (string.IsNullOrWhiteSpace(artist)) artist = site.Length > 0 ? site : MediaSessionService.FriendlyAppName(_media.NowPlayingApp);
        _npTitle.Text = title;
        _npArtist.Text = artist;

        // Progress sources, best first:
        //  1) yt-adskip extension position for this YouTube video (the page's own currentTime; Chromium's SMTC
        //     position is often stale and Opera drops the timeline for background tabs),
        //  2) SMTC timeline (extrapolated while playing),
        //  3) the last SMTC timeline seen for this same title (MediaSessionService timeline cache).
        double dur = _media.NowPlayingDurationSec, pos = _media.NowPlayingPositionSec;
        string time = "";
        bool fromExt = false;
        if ((site == "YouTube" || site.Length == 0) && BrowserProcs.Contains(_media.NowPlayingApp ?? "")
            && _media.TryExtProgress(title, out double ePos, out double eDur, out _))
        { pos = ePos; dur = eDur; fromExt = true; }
        else if (dur > 1 && _media.NowPlayingStatus == "Playing" && _media.NowPlayingTimelineAtUtc > DateTime.MinValue)
            pos += Math.Max(0, (DateTime.UtcNow - _media.NowPlayingTimelineAtUtc).TotalSeconds);
        else if (dur <= 1 && _media.TryCachedTimeline(title, out double cPos, out double cDur))
        { pos = cPos; dur = cDur; }
        if (dur > 1)
        {
            pos = Math.Clamp(pos, 0, dur);
            _npFrac = pos / dur;
            ApplyNpFill();
            _npBar.Visibility = Visibility.Visible;
            time = Fmt(pos) + " / " + Fmt(dur);
        }
        else _npBar.Visibility = Visibility.Collapsed;
        string status = _media.NowPlayingStatus == "Paused" ? "Paused" : "";
        string n = string.Join("  \u00B7  ", new[] { status, time, note }.Where(s => !string.IsNullOrWhiteSpace(s)));
        _npNote.Text = n;
        _npNote.Visibility = n.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        bool square = art is BitmapSource bs && bs.PixelHeight > 0 && (double)bs.PixelWidth / bs.PixelHeight < 1.25;
        if (textOnly && !square) art = null;
        _npArt.Source = art;
        _npArt.Visibility = art != null ? Visibility.Visible : Visibility.Collapsed;
        if (art != null && square)
        {
            // Album: art at full slot height on the left, text beside it (theme colors).
            Grid.SetColumn(_npArt, 0); Grid.SetColumnSpan(_npArt, 1);
            _npArt.Stretch = Stretch.Uniform;
            _npArt.Height = Math.Max(20, MediaPreviewGrid.Height);
            Grid.SetColumn(_npText, 1); Grid.SetColumnSpan(_npText, 1);
            _npText.VerticalAlignment = VerticalAlignment.Center;
            _npText.Margin = new Thickness(10, 0, 8, 0);
            _npText.Background = Brushes.Transparent;
            ThemeText(onOverlay: false);
        }
        else if (art != null)
        {
            // Video thumbnail: fill the slot, caption band at the bottom.
            Grid.SetColumn(_npArt, 0); Grid.SetColumnSpan(_npArt, 2);
            _npArt.Stretch = Stretch.UniformToFill;
            _npArt.Height = double.NaN;
            _npArt.HorizontalAlignment = HorizontalAlignment.Center;
            Grid.SetColumn(_npText, 0); Grid.SetColumnSpan(_npText, 2);
            _npText.VerticalAlignment = VerticalAlignment.Bottom;
            _npText.Margin = new Thickness(0);
            _npText.Background = new SolidColorBrush(Color.FromArgb(0xB8, 0, 0, 0));
            ThemeText(onOverlay: true);
        }
        else
        {
            Grid.SetColumn(_npText, 0); Grid.SetColumnSpan(_npText, 2);
            _npText.VerticalAlignment = VerticalAlignment.Center;
            _npText.Margin = new Thickness(12, 0, 12, 0);
            _npText.Background = Brushes.Transparent;
            ThemeText(onOverlay: false);
        }
        if (art != null && square) _npArt.HorizontalAlignment = HorizontalAlignment.Left;
        _npTitle.SetValue(FrameworkElement.MarginProperty, _npText.Background is SolidColorBrush { Color.A: > 0 } ? new Thickness(8, 4, 8, 0) : new Thickness(0));
        _npArtist.Margin = _npText.Background is SolidColorBrush { Color.A: > 0 } ? new Thickness(8, 2, 8, 0) : new Thickness(0, 2, 0, 0);
        _npBar.Margin = _npText.Background is SolidColorBrush { Color.A: > 0 } ? new Thickness(8, 4, 8, 0) : new Thickness(0, 6, 0, 0);
        _npNote.Margin = _npText.Background is SolidColorBrush { Color.A: > 0 } ? new Thickness(8, 2, 8, 4) : new Thickness(0, 4, 0, 0);
        _npText.MaxHeight = Math.Max(20, MediaPreviewGrid.Height);   // measure within the slot so the docked rows fit
        _npCard.Visibility = Visibility.Visible;
        FitNowCardText();
        string vis = "";
        try
        {
            double gridBottom = MediaPreviewGrid.TranslatePoint(new Point(0, MediaPreviewGrid.ActualHeight), MediaCard).Y;
            vis = $" grid={MediaPreviewGrid.ActualWidth:0}x{MediaPreviewGrid.ActualHeight:0} cardH={MediaCard.ActualHeight:0} gridBottomInCard={gridBottom:0} barW={_npBar.ActualWidth:0}";
        }
        catch { }
        string st = $"card note='{note}' textOnly={textOnly} art={(art == null ? "none" : square ? "square" : "wide")} dur={dur:0} ext={fromExt} bar={_npBar.Visibility} slot={MediaPreviewGrid.Height:0}{vis}";
        if (st != _npLastLog) { _npLastLog = st; MediaLog(st); }
    }

    private void ApplyNpFill()
    {
        if (_npBar == null || _npFill == null) return;
        double w = _npBar.ActualWidth;
        _npFill.Width = w > 0 ? Math.Max(3, Math.Round(w * Math.Clamp(_npFrac, 0, 1))) : double.NaN;
    }

    private const double NpTitleFont = 13, NpArtistFont = 11, NpMinTitleFont = 11, NpMinArtistFont = 9.5;

    /// <summary>
    /// Fit title and artist into the text column: wrap to 2 lines, step the font down (13->11 / 11->9.5) until
    /// the whole string fits, and only past that cap at 2 lines with an ellipsis + full-text tooltip.
    /// Pure card-internal layout (the slot size never changes).
    /// </summary>
    private void FitNowCardText()
    {
        if (_npText == null || _npTitle == null || _npArtist == null) return;
        double w = _npText.ActualWidth - _npArtist.Margin.Left - _npArtist.Margin.Right;
        if (w < 20) return;
        Fit(_npTitle, NpTitleFont, NpMinTitleFont, w);
        Fit(_npArtist, NpArtistFont, NpMinArtistFont, w);

        static void Fit(TextBlock tb, double max, double min, double width)
        {
            string text = tb.Text ?? "";
            tb.MaxHeight = double.PositiveInfinity;
            tb.ToolTip = null;
            if (text.Length == 0) { tb.FontSize = max; return; }
            var probe = new TextBlock { Text = text, FontFamily = tb.FontFamily, FontWeight = tb.FontWeight, TextWrapping = TextWrapping.Wrap };
            for (double fs = max; ; fs = Math.Max(min, fs - 0.5))
            {
                probe.FontSize = fs;
                probe.Measure(new Size(width, double.PositiveInfinity));
                double twoLines = Math.Ceiling(fs * 1.34 * 2) + 1;   // Segoe UI line height ~1.33 em
                if (probe.DesiredSize.Height <= twoLines)
                {
                    tb.FontSize = fs;
                    tb.MaxHeight = twoLines;
                    return;
                }
                if (fs <= min)
                {
                    tb.FontSize = min;
                    tb.MaxHeight = twoLines;    // extreme length: 2 lines + ellipsis, full text on hover
                    tb.ToolTip = text;
                    return;
                }
            }
        }
    }

    private void ThemeText(bool onOverlay)
    {
        if (_npTitle == null || _npArtist == null || _npNote == null) return;
        if (onOverlay)
        {
            _npTitle.Foreground = new SolidColorBrush(Color.FromRgb(0xF2, 0xF2, 0xF2));
            _npArtist.Foreground = new SolidColorBrush(Color.FromRgb(0xC8, 0xC8, 0xC8));
            _npNote.Foreground = new SolidColorBrush(Color.FromRgb(0xB0, 0xB0, 0xB0));
        }
        else
        {
            // The slot itself is always near-black (#050505), so light text; accent for the title.
            _npTitle.SetResourceReference(TextBlock.ForegroundProperty, "AccentOnDimTextBrush");
            _npArtist.Foreground = new SolidColorBrush(Color.FromRgb(0xD8, 0xD8, 0xD8));
            _npNote.Foreground = new SolidColorBrush(Color.FromRgb(0xA8, 0xA8, 0xA8));
        }
    }

    private static string Fmt(double s)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, s));
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
    }

    // ---------------------------------------------------------------- pin

    private void TbMediaPin_Click(object sender, RoutedEventArgs e)
    {
        // Toggle from the service state (source of truth), not IsChecked: a refresh can re-sync IsChecked
        // between press and click, and the old handler then locked/unlocked the wrong way.
        bool on = !_media.IsPinned;
        _media.SetPinned(on);
        if (on && !_media.IsPinned)
        {
            TbMediaPin.IsChecked = false;
            StatusText.Text = "Nothing to lock - no media source";
        }
        else StatusText.Text = on ? "Media locked: " + (TxtMediaNow?.Text ?? "") : "Media unlocked - follows the playing source";
        SyncMediaPinUi();
        ScheduleNowPlayingRefresh();
    }

    private void SyncMediaPinUi()
    {
        if (TbMediaPin == null) return;
        bool p = _media.IsPinned;
        if (TbMediaPin.IsChecked != p) TbMediaPin.IsChecked = p;
        TbMediaPin.ToolTip = p
            ? "Locked onto: " + (TxtMediaNow?.Text ?? "this source") + "\nNothing else takes over until you unlock it or it closes. Click to unlock."
            : "Lock onto this media source: nothing else takes over until you unlock it or it closes.\n(Unlocked: follows the most recent long-playing source.)";
        System.Windows.Automation.AutomationProperties.SetName(TbMediaPin, p ? "Unlock media source" : "Lock media source");
    }
    // Lock button text ("Unlocked"/"Locked") is dropped below this header width so the header never overflows.
    private const double MediaLockTextMinHeaderWidth = 212;   // + live/title-card toggle

    private void MediaHeaderDock_SizeChanged(object sender, SizeChangedEventArgs e) => SyncMediaLockText();

    private void SyncMediaLockText()
    {
        try
        {
            if (TbMediaPin == null || MediaHeaderDock == null) return;
            TbMediaPin.ApplyTemplate();
            if (TbMediaPin.Template?.FindName("lbl", TbMediaPin) is TextBlock lbl)
                lbl.Visibility = MediaHeaderDock.ActualWidth >= MediaLockTextMinHeaderWidth ? Visibility.Visible : Visibility.Collapsed;
        }
        catch { }
    }
}
