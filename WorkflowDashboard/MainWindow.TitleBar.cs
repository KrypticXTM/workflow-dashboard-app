using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shell;
using WorkflowDashboard.Updates;

namespace WorkflowDashboard;

/// <summary>
/// Frameless custom title bar (WindowChrome), maximized-overhang padding, theme-derived polish
/// brushes/effects, and the (disabled-by-default) startup update-check hook.
///
/// Zone math note: every Live pane rect is measured with PaneGrid/host.PointToScreen (client-relative),
/// so moving the title bar into the client area and padding the root when maximized shifts PaneGrid
/// on screen but the measured zones follow it exactly. Nothing here touches pane hosts.
/// </summary>
public partial class MainWindow
{
    private const double TitleBarHeightDip = 32;
    private Thickness _maxOverhangPad = new(0);

    /// <summary>"v1.0.0" from the assembly version (csproj &lt;Version&gt;).</summary>
    public static string AppVersionText
    {
        get
        {
            try
            {
                var v = Assembly.GetExecutingAssembly().GetName().Version;
                return v == null ? "" : $"v{v.Major}.{v.Minor}.{Math.Max(0, v.Build)}";
            }
            catch { return ""; }
        }
    }

    /// <summary>Called once from the constructor right after InitializeComponent.</summary>
    private void InitCustomTitleBar()
    {
        try { if (TxtTitleVersion != null) TxtTitleVersion.Text = AppVersionText; } catch { }
        try { Title = "Workflow Dashboard"; } catch { }

        // Subscribed before the existing StateChanged/SizeChanged handlers so padding + caption height
        // are already correct when Live retile / restore logic runs (it also calls UpdateLayout first).
        StateChanged += (_, _) => UpdateMaximizedChromePadding();
        SizeChanged += (_, _) => UpdateMaximizedChromePadding();
        LocationChanged += (_, _) => { if (WindowState == WindowState.Maximized) UpdateMaximizedChromePadding(); };
        DpiChanged += (_, _) => UpdateMaximizedChromePadding();
        SourceInitialized += (_, _) => UpdateMaximizedChromePadding();
        Loaded += (_, _) =>
        {
            UpdateMaximizedChromePadding();
            StartBackgroundUpdateCheck();
        };
    }

    // ---- Caption buttons ----

    private void BtnTitleMin_Click(object sender, RoutedEventArgs e)
    {
        // SC_MINIMIZE -> WindowState.Minimized -> existing Dashboard_StateChanged (minimizes linked Live HWNDs).
        try { SystemCommands.MinimizeWindow(this); }
        catch { try { WindowState = WindowState.Minimized; } catch { } }
    }

    private void BtnTitleMax_Click(object sender, RoutedEventArgs e) => ToggleMaximizeRestore();

    private void BtnTitleClose_Click(object sender, RoutedEventArgs e)
    {
        try { SystemCommands.CloseWindow(this); }
        catch { try { Close(); } catch { } }
    }

    private void ToggleMaximizeRestore()
    {
        try
        {
            if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
            else SystemCommands.MaximizeWindow(this);
        }
        catch
        {
            try { WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized; } catch { }
        }
    }

    // ---- Maximized overhang ----

    /// <summary>
    /// With WindowChrome the client area is the whole window rect. When maximized, Windows places the
    /// window rect outside the monitor work area by the (invisible) resize frame, so pad the root by the
    /// exact overhang so the title bar / desk aren't clipped off-screen. Also keeps the caption drag
    /// region equal to the visible title bar.
    /// </summary>
    private void UpdateMaximizedChromePadding()
    {
        try
        {
            var pad = WindowState == WindowState.Maximized ? ComputeMaximizedOverhangDip() : new Thickness(0);
            _maxOverhangPad = pad;
            if (WindowRoot != null && !SameThickness(WindowRoot.Margin, pad))
                WindowRoot.Margin = pad;

            var chrome = WindowChrome.GetWindowChrome(this);
            if (chrome != null)
            {
                // WindowChrome caption band = [ResizeBorder.Top, ResizeBorder.Top + CaptionHeight) from window top.
                double titleH = TitleBarHost != null && TitleBarHost.Visibility == Visibility.Visible ? TitleBarHeightDip : 0;
                double cap = Math.Max(0, titleH + pad.Top - chrome.ResizeBorderThickness.Top);
                if (Math.Abs(chrome.CaptionHeight - cap) > 0.1)
                    chrome.CaptionHeight = cap;
            }

            if (BtnTitleMax != null)
            {
                bool max = WindowState == WindowState.Maximized;
                if (MaxGlyph != null) MaxGlyph.Visibility = max ? Visibility.Collapsed : Visibility.Visible;
                if (RestoreGlyph != null) RestoreGlyph.Visibility = max ? Visibility.Visible : Visibility.Collapsed;
                BtnTitleMax.ToolTip = max ? "Restore" : "Maximize";
            }
        }
        catch { }
    }

    private Thickness ComputeMaximizedOverhangDip()
    {
        double sx = 1, sy = 1;
        try
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            sx = dpi.DpiScaleX <= 0 ? 1 : dpi.DpiScaleX;
            sy = dpi.DpiScaleY <= 0 ? 1 : dpi.DpiScaleY;
        }
        catch { }

        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero
                && NativeMethods.GetWindowRect(hwnd, out var wr) && wr.Width > 0 && wr.Height > 0
                && NativeMethods.TryGetMonitorWorkArea(hwnd, out var work, out _) && work.Width > 0)
            {
                double Clamp(double v) => Math.Max(0, Math.Min(32, v));
                return new Thickness(
                    Clamp((work.Left - wr.Left) / sx),
                    Clamp((work.Top - wr.Top) / sy),
                    Clamp((wr.Right - work.Right) / sx),
                    Clamp((wr.Bottom - work.Bottom) / sy));
            }
        }
        catch { }

        // Fallback: resize border + padded border (SM_CXPADDEDBORDER = 92).
        var rb = SystemParameters.WindowResizeBorderThickness;
        double padded = 0;
        try { padded = NativeMethods.GetSystemMetrics(92) / sx; } catch { }
        return new Thickness(rb.Left + padded, rb.Top + padded, rb.Right + padded, rb.Bottom + padded);
    }

    private static bool SameThickness(Thickness a, Thickness b) =>
        Math.Abs(a.Left - b.Left) < 0.01 && Math.Abs(a.Top - b.Top) < 0.01 &&
        Math.Abs(a.Right - b.Right) < 0.01 && Math.Abs(a.Bottom - b.Bottom) < 0.01;

    /// <summary>DIP offset from the window's top edge to the bottom of the custom title bar (for overlays).</summary>
    private double TitleBarBottomOffsetDip()
    {
        double h = 0;
        try { if (TitleBarHost != null && TitleBarHost.IsVisible) h = TitleBarHost.ActualHeight; } catch { }
        return h + _maxOverhangPad.Top;
    }

    // ---- Theme-derived polish (re-run from ApplyBackgroundColor / ApplyAccentColor) ----

    /// <summary>
    /// Rebuild gradient / highlight / glow resources from the current background + accent so the
    /// polish follows every theme swap. Same dark/light rules as ApplyBackgroundColor/ApplyAccentColor.
    /// </summary>
    private void RebuildThemeDerivedVisuals()
    {
        try
        {
            Color Parse(string? hex, Color fallback)
            {
                try { return string.IsNullOrWhiteSpace(hex) ? fallback : (Color)ColorConverter.ConvertFromString(hex.Trim()); }
                catch { return fallback; }
            }
            static double Lum(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;
            static Color Mix(Color a, Color b, double t)
            {
                byte L(byte x, byte y) => (byte)Math.Clamp((int)Math.Round(x + (y - x) * t), 0, 255);
                return Color.FromRgb(L(a.R, b.R), L(a.G, b.G), L(a.B, b.B));
            }

            var bg = Parse(_backgroundHex, Colors.Black);
            var accent = Parse(_accentHex, Color.FromRgb(0xFF, 0xE5, 0x66));
            bool darkBg = Lum(bg) < 0.55;
            Color Shift(Color c, int amt)
            {
                int S(byte ch) => Math.Clamp(darkBg ? ch + amt : ch - amt, 0, 255);
                return Color.FromRgb((byte)S(c.R), (byte)S(c.G), (byte)S(c.B));
            }

            var panel = Shift(bg, 12);
            var alt = Shift(bg, 20);

            void Put(string key, Freezable value)
            {
                if (value.CanFreeze) value.Freeze();
                Application.Current.Resources[key] = value;
                Resources[key] = value;
            }

            // ---- Borders (v1.0.1 fix) ----
            // BorderBrushQuiet used to be derived from the ACCENT only (0x2A + 12% accent), so on dark-grey
            // backgrounds and with dark accents it matched the panel fill and edges vanished; the panel edge
            // brush was a top-lit gradient, so the TOP stroke had a different color (near-white on light
            // themes). Now one solid outline color is derived from Bg + Accent and pushed until it clearly
            // separates from both the panel fills and the window background.
            Color border = Mix(Shift(bg, darkBg ? 48 : 64), accent, 0.16);
            for (int amt = darkBg ? 56 : 72; amt <= 160; amt += 8)
            {
                double dPanel = Math.Abs(Lum(border) - Lum(alt));
                double dBg = Math.Abs(Lum(border) - Lum(bg));
                if (dPanel >= 0.09 && dBg >= 0.12) break;
                border = Mix(Shift(bg, amt), accent, 0.16);
            }
            Put("BorderBrushQuiet", new SolidColorBrush(border));
            Put("PanelEdgeBrush", new SolidColorBrush(border));
            // Kept for compatibility only: a translucent inner line, never used as a border color.
            Put("PanelHighlightBrush", new SolidColorBrush(darkBg ? Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x59, 0xFF, 0xFF, 0xFF)));

            // ---- Text color (v1.0.2 fix) ----
            // Text IS the accent color (that's the theme). v1.0.1 compared plain luma (|dL| >= 0.40) and lifted
            // the text toward white/black otherwise, which washed out perfectly readable pairs (Maroon / Dark grey
            // on Grey became near-white, Canary/Mint on Grey went pale) so only chips/borders showed the accent.
            // Now: WCAG contrast vs every surface text sits on (window bg, panel, key face); keep the accent when
            // it is >= 3:1, otherwise move it the SHORTEST way (toward white or black) until it is, so the hue
            // survives. Muted text is the same hue pulled toward the surface, but never below 2.6:1.
            static double RelLum(Color c)
            {
                static double Ch(byte v) { double x = v / 255.0; return x <= 0.04045 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4); }
                return 0.2126 * Ch(c.R) + 0.7152 * Ch(c.G) + 0.0722 * Ch(c.B);
            }
            static double Contrast(Color a, Color b)
            {
                double la = RelLum(a), lb = RelLum(b);
                return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
            }
            double MinContrast(Color c) => Math.Min(Contrast(c, bg), Math.Min(Contrast(c, panel), Contrast(c, alt)));
            const double minText = 3.0, minMuted = 2.6;
            Color text = accent;
            if (MinContrast(accent) < minText)
            {
                Color? best = null;
                double bestT = 2;
                foreach (var toward in new[] { Colors.White, Colors.Black })
                {
                    for (double t = 0.05; t <= 1.0001; t += 0.05)
                    {
                        var cand = Mix(accent, toward, t);
                        if (MinContrast(cand) >= minText) { if (t < bestT) { bestT = t; best = cand; } break; }
                    }
                }
                text = best ?? (Lum(alt) < 0.5 ? Colors.White : Colors.Black);
            }
            Color muted = text;
            for (double t = 0.30; t > 0.001; t -= 0.02)
            {
                var cand = Mix(text, alt, t);
                if (MinContrast(cand) >= minMuted) { muted = cand; break; }
            }
            Put("TextBrush", new SolidColorBrush(text));
            Put("MutedTextBrush", new SolidColorBrush(muted));
            // Text on filled accent (lit keys, chips): black or white, whichever reads better.
            Put("AccentTextBrush", new SolidColorBrush(Contrast(accent, Colors.Black) >= Contrast(accent, Colors.White) ? Colors.Black : Colors.White));
            // Text on AccentDim fills (selected rows, hovered/checked keys): accent when it reads, else white/black.
            var dimFill = Color.FromRgb((byte)(accent.R * 35 / 100), (byte)(accent.G * 35 / 100), (byte)(accent.B * 35 / 100));
            Color onDim = Contrast(accent, dimFill) >= 4.0 ? accent
                : (Contrast(Colors.White, dimFill) >= Contrast(Colors.Black, dimFill) ? Colors.White : Colors.Black);
            Put("AccentOnDimTextBrush", new SolidColorBrush(onDim));

            // ---- Keycap overlays (relative to each key's own Background, so inverted keys keep their color) ----
            // Wall: lighter top rim, darker tapering sides, dark 3px skirt. Dish: subtle concave face shading.
            // Light backgrounds get softer darks / stronger lights so keys stay crisp on grey/white.
            var wall = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
            var dish = new RadialGradientBrush
            {
                Center = new Point(0.5, 0.42), GradientOrigin = new Point(0.5, 0.36), RadiusX = 0.62, RadiusY = 0.85
            };
            var keyShadow = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
            if (darkBg)
            {
                wall.GradientStops.Add(new GradientStop(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF), 0));
                wall.GradientStops.Add(new GradientStop(Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF), 0.10));
                wall.GradientStops.Add(new GradientStop(Color.FromArgb(0x33, 0x00, 0x00, 0x00), 0.45));
                wall.GradientStops.Add(new GradientStop(Color.FromArgb(0x99, 0x00, 0x00, 0x00), 0.80));
                wall.GradientStops.Add(new GradientStop(Color.FromArgb(0xC0, 0x00, 0x00, 0x00), 1));
                dish.GradientStops.Add(new GradientStop(Color.FromArgb(0x22, 0x00, 0x00, 0x00), 0));
                dish.GradientStops.Add(new GradientStop(Color.FromArgb(0x08, 0x00, 0x00, 0x00), 0.6));
                dish.GradientStops.Add(new GradientStop(Color.FromArgb(0x16, 0xFF, 0xFF, 0xFF), 1));
                keyShadow.GradientStops.Add(new GradientStop(Color.FromArgb(0xB0, 0, 0, 0), 0));
                keyShadow.GradientStops.Add(new GradientStop(Color.FromArgb(0xB0, 0, 0, 0), 0.78));
                keyShadow.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0, 0, 0), 1));
            }
            else
            {
                wall.GradientStops.Add(new GradientStop(Color.FromArgb(0x8C, 0xFF, 0xFF, 0xFF), 0));
                wall.GradientStops.Add(new GradientStop(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF), 0.10));
                wall.GradientStops.Add(new GradientStop(Color.FromArgb(0x1A, 0x00, 0x00, 0x00), 0.45));
                wall.GradientStops.Add(new GradientStop(Color.FromArgb(0x52, 0x00, 0x00, 0x00), 0.80));
                wall.GradientStops.Add(new GradientStop(Color.FromArgb(0x70, 0x00, 0x00, 0x00), 1));
                dish.GradientStops.Add(new GradientStop(Color.FromArgb(0x14, 0x00, 0x00, 0x00), 0));
                dish.GradientStops.Add(new GradientStop(Color.FromArgb(0x05, 0x00, 0x00, 0x00), 0.6));
                dish.GradientStops.Add(new GradientStop(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF), 1));
                keyShadow.GradientStops.Add(new GradientStop(Color.FromArgb(0x5C, 0, 0, 0), 0));
                keyShadow.GradientStops.Add(new GradientStop(Color.FromArgb(0x5C, 0, 0, 0), 0.78));
                keyShadow.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0, 0, 0), 1));
            }
            Put("KeyWallBrush", wall);
            Put("KeyDishBrush", dish);
            Put("KeyShadowBrush", keyShadow);
            Put("KeyPressedSheenBrush", new SolidColorBrush(darkBg ? Color.FromArgb(0x38, 0, 0, 0) : Color.FromArgb(0x26, 0, 0, 0)));

            // ---- Tray depth (light touch): shadow/highlight lines beside pane outlines + side panel edge ----
            var trayShadow = darkBg ? Color.FromArgb(0x80, 0, 0, 0) : Color.FromArgb(0x30, 0, 0, 0);
            var trayLight = darkBg ? Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF);
            Put("TrayShadowBrush", new SolidColorBrush(trayShadow));
            Put("TrayHighlightBrush", new SolidColorBrush(trayLight));
            var trayEdge = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
            trayEdge.GradientStops.Add(new GradientStop(trayShadow, 0));
            trayEdge.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(trayLight.A / 3), 0xFF, 0xFF, 0xFF), 0.5));
            trayEdge.GradientStops.Add(new GradientStop(Color.FromArgb((byte)Math.Min(255, trayLight.A + (darkBg ? 0x05 : 0)), 0xFF, 0xFF, 0xFF), 1));
            Put("TrayEdgeBrush", trayEdge);
            try { TrayBevel?.InvalidateVisual(); } catch { }
            var strip = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
            strip.GradientStops.Add(new GradientStop(darkBg ? Color.FromArgb(0x16, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x73, 0xFF, 0xFF, 0xFF), 0));
            strip.GradientStops.Add(new GradientStop(darkBg ? Color.FromArgb(0x06, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF), 0.55));
            strip.GradientStops.Add(new GradientStop(darkBg ? Color.FromArgb(0x1A, 0, 0, 0) : Color.FromArgb(0x12, 0, 0, 0), 1));
            Put("HeaderStripBrush", strip);
            Put("HeaderTopHiBrush", new SolidColorBrush(darkBg ? Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)));

            var title = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
            title.GradientStops.Add(new GradientStop(Mix(Shift(bg, 24), accent, 0.04), 0));
            title.GradientStops.Add(new GradientStop(panel, 1));
            Put("TitleBarBrush", title);

            // Desk (behind the tiles): soft radial lift, dark-to-slightly-lighter, faint accent tint.
            var desk = new RadialGradientBrush
            {
                Center = new Point(0.5, 0.4),
                GradientOrigin = new Point(0.5, 0.35),
                RadiusX = 0.85,
                RadiusY = 0.9
            };
            desk.GradientStops.Add(new GradientStop(Mix(Shift(bg, 20), accent, 0.05), 0));
            desk.GradientStops.Add(new GradientStop(Shift(bg, 7), 0.6));
            desk.GradientStops.Add(new GradientStop(bg, 1));
            Put("DeskGradientBrush", desk);

            // Empty pane fill (was flat black): subtle vertical lift.
            var pane = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
            pane.GradientStops.Add(new GradientStop(Mix(Shift(bg, 14), accent, 0.03), 0));
            pane.GradientStops.Add(new GradientStop(bg, 1));
            Put("PaneGradientBrush", pane);

            // Accent glow: lift very dark accents toward white so the halo stays visible on dark desks.
            double aLum = Lum(accent);
            var glowColor = aLum < 0.35 ? Mix(accent, Colors.White, 0.45) : accent;
            Put("AccentGlowEffect", new DropShadowEffect
            {
                Color = glowColor,
                ShadowDepth = 0,
                BlurRadius = 10,
                Opacity = aLum < 0.35 ? 0.85 : 0.75,
                RenderingBias = RenderingBias.Performance
            });
        }
        catch { }
    }

    // ---- Auto-update hook (disabled by default; fail-silent; never blocks the UI thread) ----

    private void StartBackgroundUpdateCheck()
    {
        if (!UpdateSettings.UpdateCheckEnabled) return;
        _ = Task.Run(async () =>
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                var info = await new UpdateChecker().CheckForUpdateAsync(cts.Token).ConfigureAwait(false);
                if (info == null) return;
                _ = Dispatcher.BeginInvoke(new Action(() =>
                {
                    // TODO(updates): replace with a non-modal "Update available" chip that calls
                    // UpdateChecker.DownloadAsync + ApplyAsync after user confirmation.
                    try { StatusText.Text = $"Update available: v{info.Version} (current {AppVersionText})"; } catch { }
                }));
            }
            catch { /* fail-silent by design */ }
        });
    }
}
