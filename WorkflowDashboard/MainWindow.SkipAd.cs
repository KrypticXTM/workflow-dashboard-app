using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace WorkflowDashboard;

/// <summary>
/// v1.0.1 "Auto-skip ads" (F8 manual hotkey, F9 if F8 is taken) + ready indicator + auto-skip.
///
/// Watcher: one background thread. It picks the browser window titled *YouTube* (re-checked every 3 s,
/// Live-linked pane preferred) and caches its "YouTube Video Player" UIA element (SkipAdPlayerScanner).
///   no ad:   every 500 ms one ClassName read on the player (YouTube adds "ad-showing" during ads), plus a
///            player-button check every 2 s as a backstop
///   ad on:   every 100 ms re-query only the player subtree's Buttons (5-30 ms) for Skip
///   no player element: old whole-page scan every 1.5-2.5 s; no YouTube window: every 2 s
/// State goes to the UI only on change (and every 3 s). It never activates, restores or moves a window by itself.
///   Ready     -> button shows an inverted accent face and pulses
///   AdPlaying -> dim accent face + accent edge (an ad is on, Skip not offered yet / non-skippable)
///   Idle      -> normal keycap
/// Auto-skip (On by default, persisted): the first time Skip is seen it is invoked immediately through UIA
/// (no accessibility nudge, no delay, no activation). Per ad: retries 600 ms apart, at most 3 tries, then it
/// gives up until the ad is gone. If a UIA invoke didn't take, a retry may bring the YouTube window forward
/// for a moment and give focus back - only when the user has been idle >= 1.5 s (GetLastInputInfo).
/// The status bar logs press latency (ms from first sight of Skip to the press, and the detection window).
/// Manual (button click / F8): same engine, polls up to ~4.5 s, may activate briefly, restores focus.
/// </summary>
public partial class MainWindow
{
    // v1.0.2 latency fix. v1.0.1 scanned all ~300 page buttons (430-500 ms) every tick, which tripped the
    // "slow scan" backoff to 2.5 s, then nudged accessibility before pressing: ~1.5-3.5 s from Skip appearing
    // to the press. Now: cached player element, cheap ad-class check while idle, fast polling during ads.
    private const int SkipWatchAdIntervalMs = 100;       // ad showing / Skip visible: re-check the player subtree
    private const int SkipWatchIdleIntervalMs = 500;     // YouTube open, no ad: one ClassName read on the player
    private const int SkipWatchIdleButtonsEvery = 4;     // ...plus a player-button check every 4th idle tick (2 s)
    private const int SkipWatchFallbackIntervalMs = 1500; // no player element found: whole-page scan
    private const int SkipWatchSlowIntervalMs = 2500;
    private const int SkipWatchNoPlayerIntervalMs = 2000;
    private const int AutoSkipRetryMs = 600;
    private const int AutoSkipMaxTries = 3;
    private const uint AutoSkipIdleRequiredMs = 1500;

    private int _skipBusy;
    private CancellationTokenSource? _skipWatchCts;
    private SkipAdState _skipUiState = SkipAdState.NoPlayer;
    private bool _skipFlashActive;
    private volatile bool _autoSkipOn = true;
    private IntPtr _skipPreferredHwnd;
    private Storyboard? _skipPulse;
    private bool _skipExtShown;
    private volatile bool _extSkipsWork = true;
    private volatile SkipAdState _skipUiStateForExt = SkipAdState.NoPlayer;

    // ---------------------------------------------------------------- lifecycle

    private void InitSkipAdFeature()
    {
        _autoSkipOn = _settings.AutoSkipAds;
        SyncAutoSkipToggle();
        ApplySkipAdIndicator(SkipAdState.NoPlayer);
        _skipWatchCts = new CancellationTokenSource();
        var ct = _skipWatchCts.Token;
        Task.Factory.StartNew(() => SkipWatchLoop(ct), ct, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        // v1.0.4: yt-adskip extension bridge (loopback only). "alive" is frequent: log it at most every 10 min.
        long lastAliveLog = 0;
        ExtensionBridge.EventReceived += (ev, detail) =>
        {
            long nowT = Environment.TickCount64;
            // v1.0.11: video progress for the Media card (frequent: never logged, no UI post).
            if (ev == "progress") { MediaSessionService.NoteExtProgress(detail); return; }
            if (ev == "alive" && nowT - lastAliveLog < 600000) { PostSkipState(_skipUiStateForExt); return; }
            if (ev == "alive") lastAliveLog = nowT;
            SkipLog($"ext: {ev}{(detail.Length > 0 ? " " + detail : "")}");
            // v1.0.5: trust the extension only while its skips actually work; after skip-ineffective or an ad that
            // played out, the UIA press is allowed again (tab active) until the extension reports a real skip.
            if (ev == "ad-start") PlayAdChime(); // v1.0.9: one chime per ad break (ad-next ignored)
            if (ev == "skip-ready") { HandleExtSkipReady(detail); return; }
            if (ev == "skipped") { _extSkipsWork = true; PostSkipResult(true, "Ad skipped by the browser extension"); }
            else if (ev == "skip-ineffective" || (ev == "ad-end" && detail.Contains("how=played-out"))) { _extSkipsWork = false; PostSkipState(_skipUiStateForExt); }
            else PostSkipState(_skipUiStateForExt);
        };
        ExtensionBridge.Start(ct, SkipLog);
    }

    private void StopSkipAdFeature()
    {
        try { _skipWatchCts?.Cancel(); } catch { }
    }

    private void TbAutoSkip_Click(object sender, RoutedEventArgs e)
    {
        _autoSkipOn = TbAutoSkip.IsChecked == true;
        _settings.AutoSkipAds = _autoSkipOn;
        SyncAutoSkipToggle();
        try { Persist(); } catch { }
        StatusText.Text = _autoSkipOn ? "Auto-skip ads: On (YouTube Skip button is pressed for you)" : "Auto-skip ads: Off (F8 / click to skip)";
    }

    private void SyncAutoSkipToggle()
    {
        if (TbAutoSkip == null) return;
        TbAutoSkip.IsChecked = _autoSkipOn;
        TbAutoSkip.Content = _autoSkipOn ? "On" : "Off";
    }

    // ---------------------------------------------------------------- watcher (background thread)

    private void SkipWatchLoop(CancellationToken ct)
    {
        IntPtr target = IntPtr.Zero;
        long lastPickMs = -100000;
        var clock = Stopwatch.StartNew();
        var player = new SkipAdPlayerScanner();
        long episodeLastSeenMs = -100000, lastTryMs = -100000;
        long lastNoSkipMs = 0, skipFirstSeenMs = -1, pressedAtMs = -1, pressLatencyMs = 0, appearWindowMs = 0;
        int episodeTries = 0, idleTick = 0;
        bool episodeGaveUp = false;
        string lastTargetTitle = "";
        int busyTicks = 0;
        SkipAdState lastState = SkipAdState.NoPlayer;
        long lastPostMs = -100000;
        long lastHeartbeatMs = -100000, lastBgCheckMs = -100000;
        string? lastBgTab = null;
        long lastBgLogMs = -1000000;
        while (!ct.IsCancellationRequested)
        {
            int sleep = SkipWatchIdleIntervalMs;
            try
            {
                if (Volatile.Read(ref _skipBusy) != 0)
                {
                    // Self-heal: a press can't legitimately hold the flag this long (manual skip polls <= 4.5 s).
                    if (++busyTicks > 60) { SkipLog("busy flag held > 30 s: released"); Volatile.Write(ref _skipBusy, 0); busyTicks = 0; }
                }
                else busyTicks = 0;
                if (Volatile.Read(ref _skipBusy) == 0)
                {
                    long now = clock.ElapsedMilliseconds;
                    if (target == IntPtr.Zero || now - lastPickMs > 3000 || !NativeMethods.IsWindow(target)
                        || !SkipAdService.IsYouTubeTitle(SkipAdService.GetTitle(target)))
                    {
                        // Several YouTube windows: while no ad is on, rotate through all of them (one per 3 s re-pick)
                        // so an ad in any of them is found; during an ad stay on the window showing it.
                        bool hotNow = lastState == SkipAdState.Ready || lastState == SkipAdState.AdPlaying;
                        var all = SkipAdService.FindYouTubeWindows();
                        IntPtr t;
                        if (hotNow && all.Contains(target)) t = target;
                        else if (!hotNow && all.Count > 1)
                        {
                            int i = all.IndexOf(target);
                            t = all[(i + 1) % all.Count];
                        }
                        else t = SkipAdService.PickTarget(_skipPreferredHwnd);
                        if (t != target) player.Reset();
                        target = t;
                        lastPickMs = now;
                    }
                    // Tab switch / navigation inside the same window changes its title: the cached player
                    // element belongs to the old page, so find it again in the current one.
                    string tNow = target == IntPtr.Zero ? "" : SkipAdService.GetTitle(target);
                    if (!string.Equals(tNow, lastTargetTitle, StringComparison.Ordinal)) { player.Reset(); lastTargetTitle = tNow; }
                    SkipAdScan scan;
                    bool scanIsPip = false;
                    if (ExtensionBridge.IsActive())
                    {
                        // v1.0.8: the browser extension handles ads in-page. Leave Opera completely alone: no UIA
                        // queries (they force Chromium accessibility work on the YouTube/PiP windows), no presses.
                        scan = new SkipAdScan { State = SkipAdState.Idle, Detail = "extension active" };
                        sleep = SkipWatchNoPlayerIntervalMs;
                    }
                    else if (target == IntPtr.Zero || !SkipAdService.IsYouTubeTitle(SkipAdService.GetTitle(target)))
                    {
                        scan = new SkipAdScan { State = SkipAdState.NoPlayer };
                        sleep = SkipWatchNoPlayerIntervalMs;
                        // v1.0.4: the Opera PiP scan was dropped (Opera's PiP window never exposes a Skip button);
                        // the yt-adskip extension handles background tabs and PiP from inside the page.
                        // Background YouTube tab: its page isn't in the UIA tree, so say so once instead of a silent NoPlayer.
                        if (scan.State == SkipAdState.NoPlayer && now - lastBgCheckMs > 10000)
                        {
                            lastBgCheckMs = now;
                            string? bg = SkipAdService.FindBackgroundYouTubeTab();
                            bool repeat = bg != null && now - lastBgLogMs >= 600000;
                            if (bg != null && (repeat || !string.Equals(bg, lastBgTab, StringComparison.Ordinal)))
                            {
                                lastBgLogMs = now;
                                SkipLog($"YouTube in background tab: '{bg}' (page not visible to UIA; {(ExtensionBridge.IsActive() ? "extension active: it skips there" : "auto-skip works only when the tab is active; load the yt-adskip extension for background tabs")})");
                            }
                            lastBgTab = bg;
                        }
                    }
                    else
                    {
                        bool hot = lastState == SkipAdState.Ready || lastState == SkipAdState.AdPlaying;
                        bool wantButtons = hot || (++idleTick % SkipWatchIdleButtonsEvery) == 0;
                        scan = player.Scan(target, wantButtons);
                        if (scan.PlayerPath && scan.AdShowing && !wantButtons)
                            scan = player.Scan(target, true);       // ad just started: look for Skip right away
                        if (!scan.PlayerPath)
                        {
                            // No player element (YouTube home, Shorts, odd layout): old whole-page scan, slowly.
                            scan = SkipAdService.Scan(target);
                            sleep = scan.ElapsedMs > 350 ? SkipWatchSlowIntervalMs : SkipWatchFallbackIntervalMs;
                        }
                        else
                        {
                            bool adNow = scan.AdShowing || scan.State == SkipAdState.Ready || scan.State == SkipAdState.AdPlaying;
                            sleep = adNow ? SkipWatchAdIntervalMs : SkipWatchIdleIntervalMs;
                        }
                    }
                    if (scan.PlayerPath || scan.State != SkipAdState.NoPlayer)
                        SkipAdService.NoteAdState(target, scan.AdShowing || scan.State == SkipAdState.Ready || scan.State == SkipAdState.AdPlaying);
                    if (scan.State != lastState)
                        SkipLog($"state {lastState} -> {scan.State} target=0x{target.ToInt64():X} '{lastTargetTitle}' ({scan.Detail}; {scan.ElapsedMs} ms)");
                    if (now - lastHeartbeatMs >= 60000)
                    {
                        lastHeartbeatMs = now;
                        SkipLog($"heartbeat state={scan.State} target=0x{target.ToInt64():X} '{lastTargetTitle}' auto={_autoSkipOn} tries={episodeTries} gaveUp={episodeGaveUp} ext={(ExtensionBridge.IsActive() ? "active (UIA idle)" : "none")}");
                    }
                    if (scan.State != lastState || now - lastPostMs > 3000) { PostSkipState(scan.State); lastPostMs = now; }
                    lastState = scan.State;

                    // Latency bookkeeping: Skip appeared somewhere between the last "no Skip" tick and now.
                    if (scan.State == SkipAdState.Ready) { if (skipFirstSeenMs < 0) { skipFirstSeenMs = now; appearWindowMs = now - lastNoSkipMs; } }
                    else
                    {
                        if (pressedAtMs >= 0 && skipFirstSeenMs >= 0)
                        {
                            long gone = now - pressedAtMs;
                            PostSkipResult(true, $"Auto-skipped ad: pressed {pressLatencyMs} ms after Skip was seen " +
                                $"(seen <= {appearWindowMs} ms after it appeared; gone {gone} ms later)");
                            episodeTries = 0; episodeGaveUp = false;
                        }
                        skipFirstSeenMs = -1; pressedAtMs = -1;
                        lastNoSkipMs = now;
                    }

                    // Ad episode bookkeeping (retry cap + give-up are per ad).
                    bool adOn = scan.State == SkipAdState.Ready || scan.State == SkipAdState.AdPlaying || scan.AdShowing;
                    if (adOn) episodeLastSeenMs = now;
                    else if (now - episodeLastSeenMs > 2500) { episodeTries = 0; episodeGaveUp = false; }

                    _skipUiStateForExt = scan.State;
                    // v1.0.4: the extension clicks Skip in-page; don't double-press. UIA stays as the fallback.
                    if (_autoSkipOn && scan.State == SkipAdState.Ready && scan.SkipButton != null && !episodeGaveUp
                        && !ExtensionBridge.IsActive()   // v1.0.8: never press while the extension is active
                        && (episodeTries == 0 || now - lastTryMs >= AutoSkipRetryMs)
                        && Interlocked.CompareExchange(ref _skipBusy, 1, 0) == 0)
                    {
                        try
                        {
                            episodeTries++;
                            lastTryMs = now;
                            bool ok;
                            string detail;
                            if (episodeTries == 1 || scanIsPip)
                            {
                                // First sight: press immediately (UIA Invoke, no nudge / no pre-delay / no activation).
                                ok = SkipAdService.TryInvoke(scan.SkipButton, out detail);
                                SkipLog($"press #{episodeTries} ok={ok} ({detail}{(scanIsPip ? ", PiP" : "")})");
                                if (ok) { pressedAtMs = clock.ElapsedMilliseconds; pressLatencyMs = pressedAtMs - skipFirstSeenMs; }
                                // Success is confirmed on the next 100 ms ticks when the Skip button is gone.
                            }
                            else
                            {
                                // Still there after a press: full engine; may raise the window briefly if user is idle.
                                bool allowActivate = SkipAdService.UserIdleMs() >= AutoSkipIdleRequiredMs;
                                (ok, detail) = SkipAdCore(target, null, allowActivate, maxWaitMs: 1200);
                                SkipLog($"press #{episodeTries} ok={ok} ({detail})");
                                if (ok) { pressedAtMs = -1; skipFirstSeenMs = -1; PostSkipResult(true, $"Auto-skipped ad on try {episodeTries} ({detail})"); episodeTries = 0; }
                            }
                            if (!ok && episodeTries >= AutoSkipMaxTries)
                            {
                                episodeGaveUp = true;
                                PostSkipResult(false, "Auto-skip gave up on this ad (press F8 or click Skip in the player)");
                            }
                        }
                        finally { Volatile.Write(ref _skipBusy, 0); }
                        sleep = SkipWatchAdIntervalMs;
                    }
                }
            }
            catch { sleep = SkipWatchSlowIntervalMs; player.Reset(); }
            try { Task.Delay(sleep, ct).Wait(ct); } catch { break; }
        }
    }

    // v1.0.10: the extension saw YouTube's Skip button (visible + enabled) but can't press it (synthetic DOM clicks
    // are ignored). Press it with a real UIA Invoke if that tab is active and visible. Once per second, max 5 per
    // ad (the extension also caps), never activating windows or sending mouse/keys.
    private static long _lastExtSkipTicks;
    private static int _lastExtSkipIndex = -1;
    private static int _extSkipBusy;
    private static void HandleExtSkipReady(string detail)
    {
        // v1.1.5: visible=True is enough (Opera split view: YouTube pane on screen, other pane focused ->
        // tabActive=False).
        // v1.1.6: also accept tabActive=True when Page Visibility says hidden (minimized / other desktop / race).
        // A true background tab (visible=False AND tabActive=False) is still ignored: UIA cannot see its Skip,
        // and we never steal focus. skipad.log: 57 ignored vs 31 presses; misses cluster on Twitch/other tabs.
        bool vis = detail.Contains("visible=True")
            || detail.Contains("tabActive=True")
            || (!detail.Contains("visible=") && !detail.Contains("tabActive="));
        if (!vis)
        { SkipLog($"ext: skip-ready ignored (tab not visible) {detail}"); return; }
        int idx = -1;
        int iPos = detail.IndexOf("index=", StringComparison.Ordinal);
        if (iPos >= 0)
        {
            int start = iPos + 6, end = start;
            while (end < detail.Length && char.IsDigit(detail[end])) end++;
            if (end > start && int.TryParse(detail.AsSpan(start, end - start), out int parsed)) idx = parsed;
        }
        long now = Environment.TickCount64;
        // Per-ad latch: a new index (2nd ad in a pod) may press immediately; same index still 1 s apart.
        bool sameAd = idx >= 0 && idx == Volatile.Read(ref _lastExtSkipIndex);
        if (sameAd && now - Interlocked.Read(ref _lastExtSkipTicks) < 1000)
        { SkipLog($"ext: skip-ready dropped (cooldown) {detail}"); return; }
        if (Interlocked.CompareExchange(ref _extSkipBusy, 1, 0) != 0)
        { SkipLog($"ext: skip-ready dropped (busy) {detail}"); return; }
        Interlocked.Exchange(ref _lastExtSkipTicks, now);
        if (idx >= 0) Volatile.Write(ref _lastExtSkipIndex, idx);
        Task.Run(() =>
        {
            try
            {
                var (ok, how) = SkipAdService.InvokeSkipFromExtension();
                SkipLog($"ext: skip-ready -> press ok={ok} ({how}) {detail}");
            }
            catch (Exception ex) { SkipLog("ext: skip-ready press failed: " + ex.GetType().Name); }
            finally { Volatile.Write(ref _extSkipBusy, 0); }
        });
    }

    // v1.0.7: chime once per ad (ad-start + each ad-next in a pod; 1.5 s de-dupe), played locally with NAudio at 40 %.
    private static long _lastChimeTicks;
    private static void PlayAdChime()
    {
        long now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _lastChimeTicks) < 10000) return; // one per break
        Interlocked.Exchange(ref _lastChimeTicks, now);
        Task.Run(() =>
        {
            try
            {
                var path = System.IO.Path.Combine(AppContext.BaseDirectory, "chime.mp3");
                if (!System.IO.File.Exists(path)) { SkipLog("chime: chime.mp3 missing"); return; }
                using var reader = new NAudio.Wave.AudioFileReader(path) { Volume = 0.4f };
                using var output = new NAudio.Wave.WaveOutEvent();
                output.Init(reader);
                output.Play();
                var sw = Stopwatch.StartNew();
                while (output.PlaybackState == NAudio.Wave.PlaybackState.Playing && sw.ElapsedMilliseconds < 15000) Thread.Sleep(100);
            }
            catch (Exception ex) { SkipLog("chime: " + ex.GetType().Name + " " + ex.Message); }
        });
    }

    /// <summary>Always-on, tiny ad-skip trace (%LOCALAPPDATA%\WorkflowDashboard\skipad.log, rolled at 256 KB).</summary>
    private static readonly object _skipLogLock = new();
    private static void SkipLog(string msg)
    {
        // v1.1.3: bridge events arrive on parallel threads; two in the same millisecond (rate-reset + ad-end at
        // 16:34:37) made the second File.AppendAllText fail on the open file and the line was silently lost.
        lock (_skipLogLock)
        try
        {
            var path = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WorkflowDashboard", "skipad.log");
            var fi = new System.IO.FileInfo(path);
            if (fi.Exists && fi.Length > 256 * 1024) System.IO.File.Move(path, path + ".1", overwrite: true);
            System.IO.File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {msg}{Environment.NewLine}");
        }
        catch { }
    }

    private void PostSkipState(SkipAdState s)
    {
        try
        {
            Dispatcher.BeginInvoke(() =>
            {
                _skipPreferredHwnd = PreferSkipAdHwnd();
                bool extOn = ExtensionBridge.IsActive();
                if (s != _skipUiState || extOn != _skipExtShown) { _skipExtShown = extOn; ApplySkipAdIndicator(s); }
            }, DispatcherPriority.Background);
        }
        catch { }
    }

    private void PostSkipResult(bool ok, string? message)
    {
        try
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (message != null) StatusText.Text = message;
                FlashSkipButton(ok);
                if (ok) PreserveLiveTilesAfterMediaAction(restoreDeskFocus: false);
            });
        }
        catch { }
    }

    // ---------------------------------------------------------------- engine (background thread)

    /// <summary>
    /// Shared skip path. Uses <paramref name="first"/> if it already holds a Skip button, otherwise polls.
    /// Activation (when allowed) is brief and focus goes back to the previous foreground window.
    /// </summary>
    private (bool ok, string detail) SkipAdCore(IntPtr target, SkipAdScan? first, bool allowActivate, int maxWaitMs)
    {
        if (target == IntPtr.Zero || !NativeMethods.IsWindow(target))
            return (false, "no YouTube window found (open YouTube in Opera / Chrome / Edge)");
        var sw = Stopwatch.StartNew();
        IntPtr prevFg = NativeMethods.GetForegroundWindow();
        bool wasIconic = NativeMethods.IsIconic(target);
        bool activated = false;
        string lastDetail = "";
        try
        {
            SkipAdService.NudgeAccessibility(target);
            var scan = first;
            while (true)
            {
                scan ??= SkipAdService.Scan(target);
                lastDetail = scan.Detail;
                if (scan.State == SkipAdState.Ready && scan.SkipButton != null)
                {
                    if (SkipAdService.TryInvoke(scan.SkipButton, out string how))
                    {
                        Thread.Sleep(450);
                        var after = SkipAdService.Scan(target);
                        if (after.State != SkipAdState.Ready)
                            return (true, how + (activated ? ", window raised briefly" : ""));
                        lastDetail = "Skip button still there after " + how;
                    }
                }
                if (sw.ElapsedMilliseconds >= maxWaitMs) break;
                // The tree may only be live while the browser is visible: raise it briefly if allowed.
                if (!activated && allowActivate && sw.ElapsedMilliseconds >= 700
                    && (wasIconic || NativeMethods.GetForegroundWindow() != target))
                {
                    activated = true;
                    try { Dispatcher.Invoke(() => NativeMethods.ForceForegroundWindow(target)); } catch { }
                    Thread.Sleep(250);
                    SkipAdService.NudgeAccessibility(target);
                }
                Thread.Sleep(200);
                scan = null;
            }
            return (false, string.IsNullOrEmpty(lastDetail) ? "no Skip button (ad not skippable yet?)" : lastDetail);
        }
        finally
        {
            if (activated)
            {
                try
                {
                    Dispatcher.Invoke(() =>
                    {
                        if (wasIconic && NativeMethods.IsWindow(target))
                            NativeMethods.ShowWindow(target, NativeMethods.SW_SHOWMINNOACTIVE);
                        if (prevFg != IntPtr.Zero && prevFg != target && NativeMethods.IsWindow(prevFg))
                            NativeMethods.ForceForegroundWindow(prevFg);
                    });
                }
                catch { }
            }
        }
    }

    /// <summary>Button click / F8: manual skip now (polls ~4.5 s, may raise the browser briefly).</summary>
    private async Task RunSkipAdAsync()
    {
        if (Interlocked.CompareExchange(ref _skipBusy, 1, 0) != 0) return;
        try
        {
            StatusText.Text = "Skip ad: looking for YouTube's Skip button...";
            var prefer = PreferSkipAdHwnd();
            _skipPreferredHwnd = prefer;
            var (ok, detail) = await Task.Run(() =>
            {
                var target = SkipAdService.PickTarget(prefer);
                return SkipAdCore(target, null, allowActivate: true, maxWaitMs: 4500);
            });
            StatusText.Text = ok ? $"Ad skipped ({detail})" : $"Couldn't skip: {detail}";
            FlashSkipButton(ok);
        }
        catch (Exception ex)
        {
            StatusText.Text = "Couldn't skip: " + ex.Message;
            FlashSkipButton(false);
        }
        finally
        {
            Volatile.Write(ref _skipBusy, 0);
            // After focusing YouTube for Skip: keep all Live panes (do not leave blank after Deactivated).
            PreserveLiveTilesAfterMediaAction(restoreDeskFocus: false);
        }
    }

    // ---------------------------------------------------------------- indicator (UI thread)

    private void ApplySkipAdIndicator(SkipAdState s)
    {
        _skipUiState = s;
        if (_skipFlashActive || BtnMediaSkipAd == null) return;
        var b = BtnMediaSkipAd;
        _skipPulse?.Stop(b);
        b.Opacity = 1.0;
        string ext = ExtensionBridge.IsActive() ? "\nBrowser extension active: it skips ads in background tabs / PiP." : "\nBrowser extension: not detected.";
        switch (s)
        {
            case SkipAdState.Ready:
                b.SetResourceReference(Control.BackgroundProperty, "AccentBrush");
                b.SetResourceReference(Control.ForegroundProperty, "AccentTextBrush");
                b.SetResourceReference(Control.BorderBrushProperty, "AccentBrush");
                KeyCap.SetLit(b, true);
                b.ToolTip = "Skip is available now - click or press F8" + (_autoSkipOn ? " (auto-skip is pressing it)" : "") + ext;
                _skipPulse ??= BuildSkipPulse();
                _skipPulse.Begin(b, true);
                break;
            case SkipAdState.AdPlaying:
                b.SetResourceReference(Control.BackgroundProperty, "AccentDimBrush");
                b.SetResourceReference(Control.ForegroundProperty, "AccentOnDimTextBrush");
                b.SetResourceReference(Control.BorderBrushProperty, "AccentBrush");
                KeyCap.SetLit(b, true);
                b.ToolTip = "Ad playing - waiting for YouTube's Skip button (non-skippable ads can't be skipped). F8 = skip now" + ext;
                break;
            default:
                b.ClearValue(Control.BackgroundProperty);
                b.ClearValue(Control.ForegroundProperty);
                b.ClearValue(Control.BorderBrushProperty);
                KeyCap.SetLit(b, false);
                b.ToolTip = (s == SkipAdState.NoPlayer
                    ? "Auto-skip ads: no YouTube window seen. Click or press F8 (F9 if F8 is taken) to skip manually."
                    : "Auto-skip ads: no ad right now. Click or press F8 (F9 if F8 is taken) to skip manually.") + ext;
                break;
        }
    }

    private static Storyboard BuildSkipPulse()
    {
        var a = new DoubleAnimation(1.0, 0.62, new Duration(TimeSpan.FromMilliseconds(650)))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        Storyboard.SetTargetProperty(a, new PropertyPath(UIElement.OpacityProperty));
        var sb = new Storyboard();
        sb.Children.Add(a);
        return sb;
    }

    private void FlashSkipButton(bool ok)
    {
        if (BtnMediaSkipAd == null) return;
        var b = BtnMediaSkipAd;
        _skipFlashActive = true;
        _skipPulse?.Stop(b);
        b.Opacity = 1.0;
        b.SetResourceReference(Control.BackgroundProperty, ok ? "SuccessBrush" : "CloseHoverBrush");
        b.SetResourceReference(Control.BorderBrushProperty, ok ? "SuccessBrush" : "CloseHoverBrush");
        b.Foreground = ok ? Brushes.Black : Brushes.White;
        KeyCap.SetLit(b, true);
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        t.Tick += (_, _) =>
        {
            t.Stop();
            _skipFlashActive = false;
            ApplySkipAdIndicator(_skipUiState);
        };
        t.Start();
    }
}
