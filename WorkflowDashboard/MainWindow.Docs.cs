using System.Text.RegularExpressions;
using System.Net.Http;
using System.Windows.Interop;
using System.Runtime.InteropServices;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace WorkflowDashboard;

/// <summary>Docs lily-pad: paste/drop into DeskPad, drag out to share. Not a second path to panes.</summary>
public partial class MainWindow
{
    private static string DocsStagingDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "DeskPad");

    private readonly ObservableCollection<DocsFileItem> _docsItems = new();
    private string _docsQuery = "";
    private bool _docsWatchAttached;
    private DispatcherTimer? _docsRefreshDebounce;
    private string _docsBrowseDir = DocsStagingDir;
    private bool _filesOverlayOpen;
    /// <summary>files = DeskPad home; downloads/desktop = ephemeral browse (list only, no DeskPad copy).</summary>
    private string _docsBrowseSource = "files";

    private void EnsureDocsStaging()
    {
        try { Directory.CreateDirectory(DocsStagingDir); }
        catch { }
        if (!_docsWatchAttached)
        {
            try
            {
                var w = new FileSystemWatcher(DocsStagingDir)
                {
                    IncludeSubdirectories = false,
                    EnableRaisingEvents = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite
                };
                w.Created += (_, _) => QueueDocsRefresh();
                w.Deleted += (_, _) => QueueDocsRefresh();
                w.Renamed += (_, _) => QueueDocsRefresh();
                w.Changed += (_, e) =>
                {
                    if (string.Equals(Path.GetExtension(e.Name), ".tmp", StringComparison.OrdinalIgnoreCase)) return;
                    QueueDocsRefresh();
                };
                _docsWatchAttached = true;
            }
            catch { }
        }
    }

    private void QueueDocsRefresh()
    {
        try
        {
            _docsRefreshDebounce ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            _docsRefreshDebounce.Tick -= DocsRefreshDebounce_Tick;
            _docsRefreshDebounce.Tick += DocsRefreshDebounce_Tick;
            _docsRefreshDebounce.Stop();
            _docsRefreshDebounce.Start();
        }
        catch
        {
            try { Dispatcher.BeginInvoke(RefreshDocsList); } catch { }
        }
    }

    private void DocsRefreshDebounce_Tick(object? sender, EventArgs e)
    {
        try { _docsRefreshDebounce?.Stop(); } catch { }
        try
        {
            // Watcher is DeskPad-only; do not yank list away from Downloads/Desktop browse.
            if (string.Equals(_docsBrowseDir, DocsStagingDir, StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(_docsBrowseDir))
                RefreshDocsList();
        }
        catch { }
    }

    /// <summary>Docs sidebar / folder chrome: never blank or unlink sibling Live panes.</summary>
    private void MarkDocsChromeActive()
    {
        // Renew quiet on every Docs interaction. SoftRefresh/Yield also protect while overlay
        // open or Docs chrome focused â€” quiet is a grace buffer, not the only guard.
        _docsChromeQuietUntilUtc = DateTime.UtcNow.AddSeconds(45);
        _yieldedForOutsideFocus = false;
    }

    /// <summary>
    /// Preview overlay open, Docs quiet window, Docs sidebar focus, or same-process FG:
    /// never Yield Live panes; SoftRefresh must re-pin indefinitely (not 4s-only).
    /// </summary>
    private bool IsDocsChromeProtecting()
    {
        if (_filesOverlayOpen) return true;
        try { if (DocsOverlayWindow.HasOpenInstance) return true; } catch { }
        if (DateTime.UtcNow < _docsChromeQuietUntilUtc) return true;
        try
        {
            if (DocsPanel != null && (DocsPanel.IsKeyboardFocusWithin || DocsPanel.IsMouseOver))
                return true;
        }
        catch { }
        try
        {
            var fg = NativeMethods.GetForegroundWindow();
            if (fg != IntPtr.Zero)
            {
                NativeMethods.GetWindowThreadProcessId(fg, out uint fgPid);
                if (fgPid == (uint)Environment.ProcessId)
                    return true;
            }
        }
        catch { }
        return false;
    }

    private void KeepLivePanesAfterDocs()
    {
        try
        {
            MarkDocsChromeActive();
            if (!_liveDesk) return;

            // Snapshot siblings first so SoftRefresh/Assert races cannot drop assignments.
            var before = SnapshotAssignments(except: -1);
            // Clear stuck click-through WITHOUT calling EndDocsFileDragHitThrough (that calls KeepLive â†’ recurse).
            if (_docsFileDragClickThru)
            {
                _docsFileDragClickThru = false;
                try
                {
                    foreach (var w in _tiler.WorkingSet)
                    {
                        if (NativeMethods.IsWindow(w.Hwnd))
                            NativeMethods.SetWindowClickThrough(w.Hwnd, through: false);
                    }
                }
                catch { }
            }

            // Prefer cheap TOPMOST re-pin; full Assert if any tile lost topmost or we just healed yield.
            EnsureLiveTilesTopmostIfNeeded();
            bool needAssert = false;
            for (int i = 0; i < _paneCount; i++)
            {
                var w = _tiler.GetZone(i);
                if (w == null || w.Parked || w.PreviewProxy) continue;
                if (!NativeMethods.IsWindow(w.Hwnd) || NativeMethods.IsIconic(w.Hwnd)) continue;
                if (!NativeMethods.IsWindowTopmost(w.Hwnd)) { needAssert = true; break; }
            }
            if (needAssert || !IsActive)
                AssertAllAssignedLiveTiles(IntPtr.Zero, focus: false, allowWhenLiveFocused: true, forceWhenInactive: true);

            EnsureAssignmentsIntact(before);
        }
        catch { }
        try { if (_filesOverlayOpen) DocsOverlayWindow.RaiseOpenInstanceIfAny(activate: false, forceAboveTiles: true); } catch { }
    }

    private void UpdateDocsBrowseSourceChrome()
    {
        try
        {
            void Style(System.Windows.Controls.Button? btn, bool on)
            {
                if (btn == null) return;
                if (on)
                {
                    btn.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty, "AccentBrush");
                    btn.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, "AccentTextBrush");
                    btn.SetResourceReference(System.Windows.Controls.Control.BorderBrushProperty, "AccentBrush");
                }
                else
                {
                    btn.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty, "PanelBrush");
                    btn.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, "MutedTextBrush");
                    btn.SetResourceReference(System.Windows.Controls.Control.BorderBrushProperty, "BorderBrushQuiet");
                }
                btn.BorderThickness = new Thickness(1);
                KeyCap.SetLit(btn, on); // inverted accent key: hover keeps its face/text readable
            }
            Style(BtnDocsFiles, _docsBrowseSource == "files");
            Style(BtnDocsDownloads, _docsBrowseSource == "downloads");
            Style(BtnDocsDesktop, _docsBrowseSource == "desktop");
        }
        catch { }
    }

    private void SetDocsBrowseSource(string source, string dir, string status)
    {
        MarkDocsChromeActive();
        _docsBrowseSource = source;
        _docsBrowseDir = dir;
        _docsQuery = "";
        RefreshDocsList();
        UpdateDocsBrowseSourceChrome();
        KeepLivePanesAfterDocs();
        try { StatusText.Text = status; } catch { }
    }

    private void SetDocsMode(bool docs)
    {
        // Tabs removed: Windows + Docs always visible (dual scroll).
        // Keep _docsMode / callers for paste focus + status; never hide either panel.
        try
        {
            _docsMode = docs;
            if (DocsPanel != null) DocsPanel.Visibility = Visibility.Visible;
            if (WindowsPanel != null) WindowsPanel.Visibility = Visibility.Visible;
            EnsureDocsStaging();
            try { RefreshDocsList(); }
            catch (Exception ex) { try { StatusText.Text = "Docs list failed: " + ex.Message; } catch { } }
            if (docs)
            {
                try { StatusText.Text = "Docs (DeskPad): paste / Files / drop in, drag out to share"; } catch { }
            }
            Dispatcher.BeginInvoke(KeepLivePanesAfterDocs);
        }
        catch (Exception ex)
        {
            try { StatusText.Text = "Docs panel error: " + ex.Message; } catch { }
        }
    }

    private void BtnDocsClear_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            EnsureDocsStaging();
            foreach (var f in Directory.GetFiles(DocsStagingDir))
            {
                try { File.SetAttributes(f, FileAttributes.Normal); File.Delete(f); } catch { }
            }
            _docsBrowseDir = DocsStagingDir;
            _docsBrowseSource = "files";
            RefreshDocsList();
            UpdateDocsBrowseSourceChrome();
            KeepLivePanesAfterDocs();
            StatusText.Text = "Docs staging cleared";
        }
        catch (Exception ex) { StatusText.Text = "Clear failed: " + ex.Message; }
    }

    private string DownloadsDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    /// <summary>Live panes are HWND_TOPMOST; owned dialogs hide behind them unless we raise the desk.</summary>
    private void RunWithDashboardTopmost(Action action)
    {
        bool was = false;
        try
        {
            was = Topmost;
            _deskAboveTilesAllowed++;   // owned dialogs must sit above the panes: let the desk go to the top
            Topmost = true;
            Activate();
            action();
        }
        finally
        {
            _deskAboveTilesAllowed = Math.Max(0, _deskAboveTilesAllowed - 1);
            try { Topmost = was; } catch { }
            try { KeepLivePanesAfterDocs(); } catch { }
        }
    }


    private void BtnDocsLatestDownload_Click(object sender, RoutedEventArgs e)
    {
        var before = SnapshotAssignments(except: -1);
        try
        {
            MarkDocsChromeActive();
            EnsureDocsStaging();
            var dir = DownloadsDir;
            if (!Directory.Exists(dir))
            {
                try { StatusText.Text = "Downloads folder not found"; } catch { }
                // Unstick browse â€” always land on DeskPad Files home.
                try
                {
                    _docsBrowseDir = DocsStagingDir;
                    _docsBrowseSource = "files";
                    _docsQuery = "";
                    RefreshDocsList();
                    UpdateDocsBrowseSourceChrome();
                }
                catch { }
                KeepLivePanesAfterDocs();
                EnsureAssignmentsIntact(before);
                return;
            }

            var exts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ".gif",".jpg",".jpeg",".png",".webp",".bmp",".mp4",".webm",".mov",".m4v",
                ".mp3",".wav",".ogg",".m4a",".flac",".pdf"
            };

            List<FileInfo> recent;
            try
            {
                recent = Directory.GetFiles(dir)
                    .Where(f => exts.Contains(Path.GetExtension(f)))
                    .Select(f => new FileInfo(f))
                    .Where(fi => fi.Length > 0
                        && !fi.Name.StartsWith(".~", StringComparison.Ordinal)
                        && !fi.Extension.Equals(".tmp", StringComparison.OrdinalIgnoreCase)
                        && !fi.Extension.Equals(".crdownload", StringComparison.OrdinalIgnoreCase)
                        && !fi.Extension.Equals(".partial", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(fi => fi.LastWriteTime)
                    .ToList();
            }
            catch (Exception exList)
            {
                try { StatusText.Text = "Latest download failed: " + exList.Message; } catch { }
                try
                {
                    _docsBrowseDir = DocsStagingDir;
                    _docsBrowseSource = "files";
                    RefreshDocsList();
                    UpdateDocsBrowseSourceChrome();
                }
                catch { }
                KeepLivePanesAfterDocs();
                EnsureAssignmentsIntact(before);
                return;
            }

            var cutoff = DateTime.Now.AddMinutes(-60);
            var batch = recent.Where(fi => fi.LastWriteTime >= cutoff).Take(5).ToList();
            if (batch.Count == 0 && recent.Count > 0)
                batch = recent.Take(1).ToList();

            if (batch.Count == 0)
            {
                try { StatusText.Text = "No recent downloads to pull in"; } catch { }
                try
                {
                    _docsBrowseDir = DocsStagingDir;
                    _docsBrowseSource = "files";
                    _docsQuery = "";
                    RefreshDocsList();
                    UpdateDocsBrowseSourceChrome();
                }
                catch { }
                KeepLivePanesAfterDocs();
                EnsureAssignmentsIntact(before);
                return;
            }

            int added = 0, skipped = 0;
            string? lastName = null;
            foreach (var pick in batch)
            {
                lastName = pick.Name;
                var dest = Path.Combine(DocsStagingDir, pick.Name);
                if (File.Exists(dest))
                {
                    skipped++;
                    continue;
                }
                try
                {
                    // Brief retry â€” Downloads often still locked while browser finishes write.
                    Exception? lastEx = null;
                    for (int attempt = 0; attempt < 4; attempt++)
                    {
                        try
                        {
                            File.Copy(pick.FullName, dest, overwrite: false);
                            lastEx = null;
                            break;
                        }
                        catch (IOException ioEx)
                        {
                            lastEx = ioEx;
                            System.Threading.Thread.Sleep(80 * (attempt + 1));
                        }
                    }
                    if (lastEx != null) throw lastEx;
                    added++;
                }
                catch
                {
                    // Skip locked/unreadable; continue other candidates.
                }
            }

            // Always return to DeskPad Files home and refresh list (never leave Downloads browse stuck).
            _docsBrowseDir = DocsStagingDir;
            _docsBrowseSource = "files";
            _docsQuery = "";
            RefreshDocsList();
            UpdateDocsBrowseSourceChrome();
            KeepLivePanesAfterDocs();
            EnsureAssignmentsIntact(before);

            if (added > 0)
            {
                try
                {
                    StatusText.Text = added == 1
                        ? "Pulled into Docs: " + lastName
                        : $"Pulled {added} file(s) into Docs" + (skipped > 0 ? $" ({skipped} already there)" : "");
                }
                catch { }
            }
            else if (skipped > 0)
            {
                try { StatusText.Text = "Already in Docs: " + lastName; } catch { }
            }
            else
            {
                try { StatusText.Text = "Latest download failed (file locked or unreadable)"; } catch { }
            }
        }
        catch (Exception ex)
        {
            try
            {
                _docsBrowseDir = DocsStagingDir;
                _docsBrowseSource = "files";
                _docsQuery = "";
                RefreshDocsList();
                UpdateDocsBrowseSourceChrome();
            }
            catch { }
            try { KeepLivePanesAfterDocs(); } catch { }
            try { EnsureAssignmentsIntact(before); } catch { }
            try { StatusText.Text = "Latest download failed: " + ex.Message; } catch { }
        }
    }


    private static string UniqueDocsPath(string dest)
    {
        if (!File.Exists(dest)) return dest;
        var dir = Path.GetDirectoryName(dest) ?? DocsStagingDir;
        var stem = Path.GetFileNameWithoutExtension(dest);
        var ext = Path.GetExtension(dest);
        for (int i = 2; i < 9999; i++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
        return Path.Combine(dir, $"{stem}_{Guid.NewGuid():N}{ext}");
    }

    private void BtnDocsDownloads_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dir = DownloadsDir;
            if (!Directory.Exists(dir))
            {
                try { StatusText.Text = "Downloads folder not found"; } catch { }
                return;
            }
            // Ephemeral browse: list in-place from Downloads; do not copy into DeskPad.
            SetDocsBrowseSource("downloads", dir, "Browsing Downloads (temporary - Files returns to DeskPad)");
            try { StatusText.Text = "Browsing Downloads (" + _docsItems.Count + " files) - temporary"; } catch { }
        }
        catch (Exception ex)
        {
            try { StatusText.Text = "Downloads browse failed: " + ex.Message; } catch { }
        }
    }

    private void BtnDocsDesktop_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
                dir = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
            {
                try { StatusText.Text = "Desktop folder not found"; } catch { }
                return;
            }
            // Ephemeral browse: list in-place from Desktop; do not copy into DeskPad.
            SetDocsBrowseSource("desktop", dir, "Browsing Desktop (temporary - Files returns to DeskPad)");
            try { StatusText.Text = "Browsing Desktop (" + _docsItems.Count + " files) - temporary"; } catch { }
        }
        catch (Exception ex)
        {
            try { StatusText.Text = "Desktop browse failed: " + ex.Message; } catch { }
        }
    }

    /// <summary>Files = DeskPad staging home (not the overlay picker).</summary>
    private void BtnDocsHome_Click(object sender, RoutedEventArgs e)
    {
        EnsureDocsStaging();
        SetDocsBrowseSource("files", DocsStagingDir, "Docs home: DeskPad. Paste / drop add files here.");
    }


    private void BtnDocsRemoveItem_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string? path = null;
            if (sender is Button b && b.Tag is string t) path = t;
            else if (DocsList?.SelectedItem is DocsFileItem sel) path = sel.FullPath;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
            File.SetAttributes(path, FileAttributes.Normal);
            File.Delete(path);
            RefreshDocsList();
            try { StatusText.Text = "Removed from Docs: " + Path.GetFileName(path); } catch { }
        }
        catch (Exception ex)
        {
            try { StatusText.Text = "Remove failed: " + ex.Message; } catch { }
        }
    }
    private void BtnDocsPaste_Click(object sender, RoutedEventArgs e) => PasteIntoDocs();

        private void DocsPanel_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        try { MarkDocsChromeActive(); KeepLivePanesAfterDocs(); } catch { }
    }
private void DocsPanel_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.V && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            if (PasteIntoDocs())
            {
                e.Handled = true;
            }
        }
    }

    /// <summary>Ctrl+V / Paste: clipboard image or file list ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã¢â‚¬Â ÃƒÂ¢Ã¢â€šÂ¬Ã¢â€žÂ¢ÃƒÆ’Ã†â€™ÃƒÂ¢Ã¢â€šÂ¬Ã‚Â ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÂ¢Ã¢â‚¬Å¾Ã‚Â¢ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬Ãƒâ€¦Ã‚Â¡ÃƒÆ’Ã†â€™ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã¢â‚¬Â ÃƒÂ¢Ã¢â€šÂ¬Ã¢â€žÂ¢ÃƒÆ’Ã†â€™ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã¢â‚¬Â¦Ãƒâ€šÃ‚Â¡ÃƒÆ’Ã†â€™ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬Ãƒâ€¦Ã‚Â¡ÃƒÆ’Ã†â€™ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã¢â‚¬Â ÃƒÂ¢Ã¢â€šÂ¬Ã¢â€žÂ¢ÃƒÆ’Ã†â€™ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã¢â‚¬Â¦Ãƒâ€šÃ‚Â¡ÃƒÆ’Ã†â€™ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã¢â‚¬Â¦Ãƒâ€šÃ‚Â¾ÃƒÆ’Ã†â€™ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¢ DeskPad.</summary>
    private bool PasteIntoDocs()
    {
        try
        {
            EnsureDocsStaging();
            int added = 0;

            if (Clipboard.ContainsFileDropList())
            {
                var list = Clipboard.GetFileDropList();
                foreach (string? path in list)
                {
                    if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) continue;
                    CopyIntoStaging(path);
                    added++;
                }
            }
            else if (Clipboard.ContainsImage())
            {
                var img = Clipboard.GetImage();
                if (img != null)
                {
                    var name = $"paste_{DateTime.Now:yyyyMMdd_HHmmss}.png";
                    var dest = UniqueDest(Path.Combine(DocsStagingDir, name));
                    using var fs = new FileStream(dest, FileMode.CreateNew, FileAccess.Write);
                    var enc = new PngBitmapEncoder();
                    enc.Frames.Add(BitmapFrame.Create(img));
                    enc.Save(fs);
                    added++;
                    try { StatusText.Text = "Pasted screenshot to Docs: " + Path.GetFileName(dest) + Path.GetFileName(dest); } catch { }
                }
            }
            else if (Clipboard.ContainsAudio())
            {
                // skip for v1 - audio via file drop / URL paste
            }
            else if (Clipboard.ContainsText())
            {
                var text = (Clipboard.GetText() ?? "").Trim().Trim('"', '\'');
                if (text.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                    || text.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    try { StatusText.Text = "Downloading into Docs..."; } catch { }
                    _ = SaveWebUrlsToDocsAsync(new List<string> { text });
                    return true;
                }
            }

            if (added > 0)
            {
                _docsBrowseDir = DocsStagingDir;
                RefreshDocsList();
                KeepLivePanesAfterDocs();
                return true;
            }

            try { StatusText.Text = "Clipboard empty (need an image, file, or media link). Copy a file/screenshot, or paste a direct download URL."; } catch { }
            return false;
        }
        catch (Exception ex)
        {
            try { StatusText.Text = "Paste failed: " + ex.Message; } catch { }
            return false;
        }
    }

    private void RefreshDocsList()
    {
        if (DocsList == null) return;
        EnsureDocsStaging();
        // Respect browse folder (Downloads / Desktop / DeskPad). Fall back to DeskPad if missing.
        var dir = (!string.IsNullOrWhiteSpace(_docsBrowseDir) && Directory.Exists(_docsBrowseDir))
            ? _docsBrowseDir
            : DocsStagingDir;
        _docsBrowseDir = dir;
        _docsItems.Clear();
        try
        {
            foreach (var path in Directory.GetFiles(dir).OrderByDescending(File.GetLastWriteTimeUtc))
            {
                var name = Path.GetFileName(path);
                if (!string.IsNullOrEmpty(_docsQuery) &&
                    name.IndexOf(_docsQuery, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                _docsItems.Add(new DocsFileItem(path));
            }
        }
        catch { }
        DocsList.ItemsSource = null;
        DocsList.ItemsSource = _docsItems;
        try
        {
            if (TxtDocsPath != null)
                TxtDocsPath.Text = dir;
        }
        catch { }
        // Keep source selection in sync with which folder the list shows.
        try
        {
            if (string.Equals(dir, DocsStagingDir, StringComparison.OrdinalIgnoreCase))
                _docsBrowseSource = "files";
            else if (string.Equals(dir, DownloadsDir, StringComparison.OrdinalIgnoreCase))
                _docsBrowseSource = "downloads";
            else
            {
                var desk = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                if (string.IsNullOrWhiteSpace(desk)) desk = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                if (!string.IsNullOrWhiteSpace(desk) && string.Equals(dir, desk, StringComparison.OrdinalIgnoreCase))
                    _docsBrowseSource = "desktop";
            }
            UpdateDocsBrowseSourceChrome();
        }
        catch { }
    }

    private void DocsList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        DocsList.Tag = e.GetPosition(null);
        // Keep Live panes filled while selecting Docs (no yield/blank from chrome focus).
        try { MarkDocsChromeActive(); KeepLivePanesAfterDocs(); } catch { }
    }

    private void DocsList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        // Cancel stuck click-through from an aborted micro-drag (looked like vanished panes).
        try { EndDocsFileDragHitThrough(); } catch { }
    }

    private void DocsList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        if (DocsList.SelectedItem is not DocsFileItem item) return;
        if (DocsList.Tag is not Point start) return;
        var pos = e.GetPosition(null);
        if (Math.Abs(pos.X - start.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;
        BeginDocsFileDragHitThrough();
        try
        {
            var data = new DataObject(DataFormats.FileDrop, new[] { item.FullPath });
            DragDrop.DoDragDrop(DocsList, data, DragDropEffects.Copy | DragDropEffects.Move);
        }
        catch { }
        finally
        {
            // Immediate clear Ã¢â‚¬â€ the old 4s delay left Live tiles click-through after every Docs click-drag.
            EndDocsFileDragHitThrough();
        }
    }
    private void DocsPanel_DragOver(object sender, DragEventArgs e)
    {
        if (CanAcceptDocsDrop(e.Data)
            || e.Data.GetDataPresent(typeof(WindowEntry))
            || e.Data.GetDataPresent(typeof(WorkingWindow)))
        {
            e.Effects = DragDropEffects.Copy;
            if (DocsDropHint != null) DocsDropHint.Visibility = Visibility.Visible;
        }
        else e.Effects = DragDropEffects.None;
        e.Handled = true;
    }

    private void DocsPanel_DragLeave(object sender, DragEventArgs e)
    {
        if (DocsDropHint != null) DocsDropHint.Visibility = Visibility.Collapsed;
    }

    private void DocsPanel_Drop(object sender, DragEventArgs e)
    {
        if (DocsDropHint != null) DocsDropHint.Visibility = Visibility.Collapsed;
        EnsureDocsStaging();
        try
        {
            int added = 0;
            if (e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] files)
            {
                foreach (var f in files) { CopyIntoStaging(f); added++; }
            }
            // Bitmap / DIB / Enhanced metafile-ish image payloads from browsers
            foreach (var fmt in new[] { DataFormats.Bitmap, DataFormats.Dib, DataFormats.Tiff })
            {
                if (!e.Data.GetDataPresent(fmt)) continue;
                try
                {
                    if (e.Data.GetData(fmt) is BitmapSource bmp)
                    {
                        SaveBitmapToDocs(bmp);
                        added++;
                        break;
                    }
                    if (e.Data.GetData(fmt) is System.Drawing.Image gdi)
                    {
                        using var tmp = new System.Drawing.Bitmap(gdi);
                        using var ms = new MemoryStream();
                        tmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                        ms.Position = 0;
                        var dec = new PngBitmapDecoder(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                        SaveBitmapToDocs(dec.Frames[0]);
                        added++;
                        break;
                    }
                }
                catch { }
            }
            var webUrls = TryGetDroppedWebUrls(e.Data); if (webUrls.Count > 0)
            {
                try { StatusText.Text = "Saving from web into Docs..."; } catch { }
                _ = SaveWebUrlsToDocsAsync(webUrls);
                added++;
            }
            if (added > 0) { RefreshDocsList(); KeepLivePanesAfterDocs(); }
            else try { StatusText.Text = "Docs: drop a file, or drag a direct media link (GIFs work; X/YouTube pages often only give a link)"; } catch { }
        }
        catch (Exception ex)
        {
            try { StatusText.Text = "Docs drop failed: " + ex.Message; } catch { }
        }
        e.Handled = true;
    }

    private static bool CanAcceptDocsDrop(IDataObject data)
    {
        try
        {
            if (data.GetDataPresent(DataFormats.FileDrop)) return true;
            if (data.GetDataPresent(DataFormats.Bitmap)) return true;
            if (data.GetDataPresent(DataFormats.Html)) return true;
            if (data.GetDataPresent(DataFormats.UnicodeText)) return true;
            if (data.GetDataPresent(DataFormats.Text)) return true;
            if (data.GetDataPresent("UniformResourceLocatorW")) return true;
            if (data.GetDataPresent("UniformResourceLocator")) return true;
        }
        catch { }
        return false;
    }

    private static bool LooksLikePageUrl(string u)
    {
        if (!Uri.TryCreate(u, UriKind.Absolute, out var uri)) return true;
        var host = uri.Host.ToLowerInvariant();
        // Social / video *pages* Ã¢â‚¬â€ not the media file itself
        if (host is "x.com" or "www.x.com" or "twitter.com" or "www.twitter.com"
            or "mobile.twitter.com" or "m.facebook.com" or "facebook.com" or "www.facebook.com"
            or "instagram.com" or "www.instagram.com" or "youtube.com" or "www.youtube.com"
            or "m.youtube.com" or "youtu.be" or "tiktok.com" or "www.tiktok.com")
            return true;
        if (host.EndsWith(".youtube.com", StringComparison.Ordinal)) return true;
        return false;
    }

    private static int MediaUrlScore(string u)
    {
        var s = 0;
        var lower = u.ToLowerInvariant();
        try
        {
            if (Uri.TryCreate(u, UriKind.Absolute, out var uri))
            {
                var path = uri.AbsolutePath.ToLowerInvariant();
                var host = uri.Host.ToLowerInvariant();
                if (path.EndsWith(".gif") || path.EndsWith(".png") || path.EndsWith(".jpg")
                    || path.EndsWith(".jpeg") || path.EndsWith(".webp") || path.EndsWith(".bmp")
                    || path.EndsWith(".mp4") || path.EndsWith(".webm") || path.EndsWith(".mov")
                    || path.EndsWith(".m4v")) s += 50;
                if (host.Contains("pbs.twimg.com") || host.Contains("twimg.com")
                    || host.Contains("cdninstagram") || host.Contains("fbcdn")
                    || host.Contains("ytimg.com") || host.Contains("ggpht.com")
                    || host.Contains("media.") || host.Contains("cdn.")
                    || host.Contains("img.") || host.Contains("images.")) s += 30;
                if (path.Contains("/media/") || path.Contains("/img/") || path.Contains("/image/")
                    || path.Contains("/video/") || path.Contains("/ext_tw_video")
                    || path.Contains("/amplify_video")) s += 20;
            }
        }
        catch { }
        if (lower.Contains(".gif") || lower.Contains("format=gif") || lower.Contains("mime=image")
            || lower.Contains("format=jpg") || lower.Contains("format=png") || lower.Contains("format=mp4"))
            s += 15;
        if (LooksLikePageUrl(u)) s -= 100;
        return s;
    }

    private static List<string> TryGetDroppedWebUrls(IDataObject data)
    {
        var list = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string? u)
        {
            if (string.IsNullOrWhiteSpace(u)) return;
            u = u.Trim().Trim('"', '\'', ')', '(', '<', '>');
            // protocol-relative
            if (u.StartsWith("//")) u = "https:" + u;
            if (!u.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                && !u.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return;
            if (u.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase)) return;
            if (u.StartsWith("blob:", StringComparison.OrdinalIgnoreCase)) return;
            if (u.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return;
            // strip HTML entity leftovers
            u = u.Replace("&amp;", "&");
            if (seen.Add(u)) list.Add(u);
        }
        try
        {
            if (data.GetDataPresent("UniformResourceLocatorW")) Add(data.GetData("UniformResourceLocatorW") as string);
            if (data.GetDataPresent("UniformResourceLocator")) Add(data.GetData("UniformResourceLocator") as string);
            if (data.GetDataPresent(DataFormats.UnicodeText)) Add(data.GetData(DataFormats.UnicodeText) as string);
            if (data.GetDataPresent(DataFormats.Text)) Add(data.GetData(DataFormats.Text) as string);
            if (data.GetDataPresent(DataFormats.Html))
            {
                var html = data.GetData(DataFormats.Html) as string;
                if (!string.IsNullOrEmpty(html))
                {
                    // src / href / poster / data-src / content (og:image)
                    foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(
                        html, @"(?is)(?:src|href|poster|data-src|data-lazy-src|content)\s*=\s*[""'](?<u>[^""']+)[""']"))
                        Add(m.Groups["u"].Value);
                    // srcset first candidate
                    foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(
                        html, @"(?is)srcset\s*=\s*[""'](?<u>[^""']+)[""']"))
                    {
                        var first = m.Groups["u"].Value.Split(',')[0].Trim().Split(' ')[0];
                        Add(first);
                    }
                }
            }
        }
        catch { }

        // Prefer real media URLs; keep page URLs last (usually useless for download)
        return list
            .OrderByDescending(MediaUrlScore)
            .ThenBy(u => LooksLikePageUrl(u) ? 1 : 0)
            .ToList();
    }

    private void SaveBitmapToDocs(BitmapSource bmp)
    {
        var dest = UniqueDest(Path.Combine(DocsStagingDir, $"web_{DateTime.Now:yyyyMMdd_HHmmss}.png"));
        using var fs = new FileStream(dest, FileMode.CreateNew, FileAccess.Write);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        enc.Save(fs);
        try { StatusText.Text = "Saved to Docs: " + Path.GetFileName(dest); } catch { }
    }

    private async System.Threading.Tasks.Task SaveWebUrlsToDocsAsync(List<string> urls)
    {
        int added = 0;
        string? lastErr = null;
        int skippedPages = 0;
        int triedMedia = 0;
        try
        {
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(90) };
            http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36");
            http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "image/avif,image/webp,image/apng,image/*,video/*,*/*;q=0.8");

            foreach (var url in urls)
            {
                if (LooksLikePageUrl(url))
                {
                    skippedPages++;
                    continue; // x.com / youtube watch pages are not the file
                }
                triedMedia++;
                try
                {
                    using var req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, url);
                    // Help CDNs (X/Twitter) that check Referer
                    if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
                    {
                        if (uri.Host.Contains("twimg.com", StringComparison.OrdinalIgnoreCase)
                            || uri.Host.Contains("twitter.com", StringComparison.OrdinalIgnoreCase)
                            || uri.Host.Contains("x.com", StringComparison.OrdinalIgnoreCase))
                            req.Headers.TryAddWithoutValidation("Referer", "https://x.com/");
                    }
                    using var resp = await http.SendAsync(req, System.Net.Http.HttpCompletionOption.ResponseHeadersRead);
                    if (!resp.IsSuccessStatusCode)
                    {
                        lastErr = $"HTTP {(int)resp.StatusCode} for media URL";
                        continue;
                    }
                    var ct = resp.Content.Headers.ContentType?.MediaType ?? "";
                    if (ct.StartsWith("text/html", StringComparison.OrdinalIgnoreCase))
                    {
                        lastErr = "Got a webpage, not the media file";
                        continue;
                    }
                    // sniff magic bytes if content-type is vague
                    await using var src = await resp.Content.ReadAsStreamAsync();
                    var header = new byte[12];
                    var n = await src.ReadAsync(header.AsMemory(0, 12));
                    var ext = GuessExtFromContentType(ct, url);
                    if (ext is ".bin" or ".img" or ".vid" || ct is "" or "application/octet-stream")
                    {
                        if (n >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF) ext = ".jpg";
                        else if (n >= 8 && header[0] == 0x89 && header[1] == (byte)'P' && header[2] == (byte)'N' && header[3] == (byte)'G') ext = ".png";
                        else if (n >= 6 && header[0] == (byte)'G' && header[1] == (byte)'I' && header[2] == (byte)'F') ext = ".gif";
                        else if (n >= 12 && header[0] == (byte)'R' && header[1] == (byte)'I' && header[2] == (byte)'F' && header[3] == (byte)'F'
                                 && header[8] == (byte)'W' && header[9] == (byte)'E' && header[10] == (byte)'B' && header[11] == (byte)'P') ext = ".webp";
                        else if (n >= 8 && header[4] == (byte)'f' && header[5] == (byte)'t' && header[6] == (byte)'y' && header[7] == (byte)'p') ext = ".mp4";
                        else if (n >= 4 && header[0] == 0x1A && header[1] == 0x45 && header[2] == 0xDF && header[3] == 0xA3) ext = ".webm";
                    }
                    if (ext is ".bin" && !ct.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
                        && !ct.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
                    {
                        lastErr = "URL was not an image or video file";
                        continue;
                    }
                    var name = SuggestWebFileName(url, ext);
                    var dest = UniqueDest(Path.Combine(DocsStagingDir, name));
                    await using var dst = File.Create(dest);
                    if (n > 0) await dst.WriteAsync(header.AsMemory(0, n));
                    await src.CopyToAsync(dst);
                    added++;
                }
                catch (Exception ex) { lastErr = ex.Message; }
            }
        }
        catch (Exception ex) { lastErr = ex.Message; }

        await Dispatcher.InvokeAsync(() =>
        {
            RefreshDocsList();
            KeepLivePanesAfterDocs();
            try
            {
                if (added > 0) StatusText.Text = $"Saved {added} web file(s) to Docs";
                else if (skippedPages > 0 && triedMedia == 0)
                    StatusText.Text = "That drop was a page link, not the media file. Right-click the image/video and Save as, then drop the file into Docs.";
                else StatusText.Text = lastErr ?? "Could not save that web drop";
            }
            catch { }
        });
    }

    private static string GuessExtFromContentType(string ct, string url)
    {
        if (ct.StartsWith("image/gif")) return ".gif";
        if (ct.StartsWith("image/png")) return ".png";
        if (ct.StartsWith("image/jpeg")) return ".jpg";
        if (ct.StartsWith("image/webp")) return ".webp";
        if (ct.StartsWith("image/")) return ".img";
        if (ct.StartsWith("video/mp4")) return ".mp4";
        if (ct.StartsWith("video/webm")) return ".webm";
        if (ct.StartsWith("video/")) return ".vid";
        if (ct.StartsWith("audio/mpeg") || ct.Contains("audio/mp3")) return ".mp3";
        if (ct.StartsWith("audio/wav") || ct.StartsWith("audio/x-wav")) return ".wav";
        if (ct.StartsWith("audio/ogg")) return ".ogg";
        if (ct.StartsWith("audio/mp4") || ct.StartsWith("audio/x-m4a")) return ".m4a";
        if (ct.StartsWith("audio/")) return ".aud";
        try {
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri)) {
                var e = Path.GetExtension(uri.AbsolutePath);
                if (!string.IsNullOrEmpty(e) && e.Length <= 6) return e.ToLowerInvariant();
            }
        } catch { }
        return ".bin";
    }

    private static string SuggestWebFileName(string url, string ext)
    {
        try {
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri)) {
                var leaf = Path.GetFileName(uri.AbsolutePath);
                if (!string.IsNullOrWhiteSpace(leaf) && leaf.Contains('.')) return leaf;
            }
        } catch { }
        return $"web_{DateTime.Now:yyyyMMdd_HHmmss}{ext}";
    }
    private void CopyIntoStaging(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
        var name = Path.GetFileName(path);
        var dest = Path.Combine(DocsStagingDir, name);
        if (string.Equals(Path.GetFullPath(path), Path.GetFullPath(dest), StringComparison.OrdinalIgnoreCase)
            || File.Exists(dest))
        {
            try { StatusText.Text = "Already in Docs: " + name; } catch { }
            return;
        }
        File.Copy(path, dest, overwrite: false);
        try { StatusText.Text = "Saved to Docs: " + name; } catch { }
    }



    private static string UniqueDest(string dest)
    {
        if (!File.Exists(dest)) return dest;
        var dir = Path.GetDirectoryName(dest) ?? DocsStagingDir;
        var stem = Path.GetFileNameWithoutExtension(dest);
        var ext = Path.GetExtension(dest);
        for (int i = 1; i < 1000; i++)
        {
            var next = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(next)) return next;
        }
        return Path.Combine(dir, $"{stem}_{DateTime.Now:HHmmss}{ext}");
    }
    private void DocsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DocsList.SelectedItem is not DocsFileItem item) return;
        try
        {
            EndDocsFileDragHitThrough();
            OpenDocInPreview(item.FullPath);
        }
        catch (Exception ex)
        {
            try { StatusText.Text = "Open failed: " + ex.Message; } catch { }
        }
    }

    /// <summary>Open a Docs list path in the singleton Preview (with prev/next cycle).</summary>
    private void OpenDocInPreview(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        MarkDocsChromeActive();
        var before = SnapshotAssignments(except: -1);
        _filesOverlayOpen = true;
        try { KeepLivePanesAfterDocs(); } catch { }
        DocsOverlayWindow.ZoneFilledProbe = IsPaneFilled;
        DocsOverlayWindow.ShowPreview(
            this,
            path,
            onClosed: () => { _filesOverlayOpen = false; KeepLivePanesAfterDocs(); },
            onAssignToPane: (p, zone) =>
            {
                try { AssignFilePathToPane(p, zone); }
                catch (Exception ex) { try { StatusText.Text = "Assign failed: " + ex.Message; } catch { } }
            },
            paneCount: Math.Max(1, _paneCount),
            onCycle: delta => CycleDocsInPreview(delta),
            getCycleCount: () => _docsItems.Count,
            onSwitchMode: mode => SwitchPreviewMode(mode));
        try { EnsureAssignmentsIntact(before); KeepLivePanesAfterDocs(); } catch { }
    }

    /// <summary>Preview banner Windows|Docs toggle: swap content via existing Open* paths; cycles stay mode-local.</summary>
    private void SwitchPreviewMode(string mode)
    {
        try
        {
            if (string.Equals(mode, "windows", StringComparison.OrdinalIgnoreCase))
            {
                WindowEntry? entry = null;
                try { entry = WindowList?.SelectedItem as WindowEntry; } catch { }
                if (entry == null || entry.Hwnd == IntPtr.Zero)
                {
                    try
                    {
                        var windows = (WindowList?.ItemsSource as IEnumerable<WindowEntry>)?.ToList()
                                      ?? WindowList?.Items.Cast<WindowEntry>().ToList()
                                      ?? new List<WindowEntry>();
                        entry = windows.FirstOrDefault(w => w != null && w.Hwnd != IntPtr.Zero && !IsAssignedLiveHwnd(w.Hwnd))
                                ?? windows.FirstOrDefault(w => w != null && w.Hwnd != IntPtr.Zero);
                    }
                    catch { }
                }
                if (entry == null || entry.Hwnd == IntPtr.Zero)
                {
                    try { StatusText.Text = "No windows to preview"; } catch { }
                    return;
                }
                try { if (WindowList != null) WindowList.SelectedItem = entry; } catch { }
                OpenWindowInPreview(entry);
                return;
            }

            // docs mode
            DocsFileItem? item = null;
            try { item = DocsList?.SelectedItem as DocsFileItem; } catch { }
            if (item == null || string.IsNullOrWhiteSpace(item.FullPath))
                item = _docsItems.FirstOrDefault(d => d != null && File.Exists(d.FullPath));
            if (item == null || string.IsNullOrWhiteSpace(item.FullPath))
            {
                try { StatusText.Text = "No docs to preview"; } catch { }
                return;
            }
            try { if (DocsList != null) DocsList.SelectedItem = item; } catch { }
            OpenDocInPreview(item.FullPath);
        }
        catch (Exception ex)
        {
            try { StatusText.Text = "Preview mode switch failed: " + ex.Message; } catch { }
        }
    }

    /// <summary>Cycle Preview to next/prev Docs list item (wrap-around). Same overlay; content swap only.</summary>
    private void CycleDocsInPreview(int delta)
    {
        try
        {
            if (_docsItems.Count == 0) return;
            var cur = DocsOverlayWindow.GetPreviewPath();
            int idx = -1;
            if (!string.IsNullOrEmpty(cur))
            {
                for (int i = 0; i < _docsItems.Count; i++)
                {
                    if (string.Equals(_docsItems[i].FullPath, cur, StringComparison.OrdinalIgnoreCase))
                    {
                        idx = i;
                        break;
                    }
                }
            }
            if (idx < 0) idx = 0;
            int n = _docsItems.Count;
            int next = ((idx + delta) % n + n) % n;
            var item = _docsItems[next];
            try { if (DocsList != null) DocsList.SelectedItem = item; } catch { }
            OpenDocInPreview(item.FullPath);
        }
        catch (Exception ex)
        {
            try { StatusText.Text = "Cycle failed: " + ex.Message; } catch { }
        }
    }

}

internal sealed class DocsFileItem
{
    public DocsFileItem(string path)
    {
        FullPath = path;
        Name = Path.GetFileName(path);
        Ext = Path.GetExtension(path).TrimStart('.').ToUpperInvariant();
        try { SizeLabel = FormatSize(new FileInfo(path).Length); } catch { SizeLabel = ""; }
        Thumb = ShellThumbLoader.GetThumb(path, 96);
    }

    public string FullPath { get; }
    public string Name { get; }
    public string Ext { get; }
    public string SizeLabel { get; }
    public ImageSource? Thumb { get; }
    public string Meta => string.IsNullOrEmpty(SizeLabel) ? Ext : $"{Ext}  \u00B7  {SizeLabel}";

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:0.#} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):0.#} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):0.#} GB";
    }
}

internal static class ShellThumbLoader
{
    private const uint SHGFI_ICON = 0x000000100;
    private const uint SHGFI_LARGEICON = 0x000000000;
    private const uint SHGFI_USEFILEATTRIBUTES = 0x000000010;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    public static ImageSource? GetThumb(string path, int size)
    {
        try
        {
            var ext = Path.GetExtension(path);
            if (IsImageExt(ext))
                return LoadImageThumb(path, size);

            // Safe shell file icon (PDF / video / etc.) Ã¢â‚¬â€ not full thumbnail COM (that crashed Docs before).
            var icon = TryShellFileIcon(path, size);
            if (icon != null) return icon;

            return MakePlaceholder(ext, size);
        }
        catch
        {
            try { return MakePlaceholder(Path.GetExtension(path), size); }
            catch { return null; }
        }
    }

    private static bool IsImageExt(string ext) =>
        ext.Equals(".png", StringComparison.OrdinalIgnoreCase)
        || ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
        || ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
        || ext.Equals(".gif", StringComparison.OrdinalIgnoreCase)
        || ext.Equals(".webp", StringComparison.OrdinalIgnoreCase)
        || ext.Equals(".bmp", StringComparison.OrdinalIgnoreCase);

    private static ImageSource? LoadImageThumb(string path, int size)
    {
        var bi = new BitmapImage();
        bi.BeginInit();
        bi.CacheOption = BitmapCacheOption.OnLoad;
        bi.DecodePixelWidth = size;
        bi.UriSource = new Uri(Path.GetFullPath(path));
        bi.EndInit();
        bi.Freeze();
        return bi;
    }

    private static ImageSource? TryShellFileIcon(string path, int size)
    {
        var shinfo = new SHFILEINFO();
        uint flags = SHGFI_ICON | SHGFI_LARGEICON;
        IntPtr hImg = SHGetFileInfo(path, 0, ref shinfo, (uint)System.Runtime.InteropServices.Marshal.SizeOf<SHFILEINFO>(), flags);
        if (hImg == IntPtr.Zero || shinfo.hIcon == IntPtr.Zero)
        {
            // Fallback by extension association when path is awkward
            flags = SHGFI_ICON | SHGFI_LARGEICON | SHGFI_USEFILEATTRIBUTES;
            hImg = SHGetFileInfo(path, 0x80 /*FILE_ATTRIBUTE_NORMAL*/, ref shinfo,
                (uint)System.Runtime.InteropServices.Marshal.SizeOf<SHFILEINFO>(), flags);
        }
        if (shinfo.hIcon == IntPtr.Zero) return null;
        try
        {
            var bs = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                shinfo.hIcon,
                Int32Rect.Empty,
                BitmapSizeOptions.FromWidthAndHeight(size, size));
            bs.Freeze();
            return bs;
        }
        finally
        {
            DestroyIcon(shinfo.hIcon);
        }
    }

    private static ImageSource MakePlaceholder(string ext, int size)
    {
        var label = string.IsNullOrEmpty(ext) ? "FILE" : ext.TrimStart('.').ToUpperInvariant();
        if (label.Length > 4) label = label.Substring(0, 4);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x11, 0x11, 0x11)),
                new Pen(new SolidColorBrush(Color.FromRgb(0xFF, 0xE5, 0x66)), 2),
                new Rect(1, 1, size - 2, size - 2));
            var ft = new FormattedText(
                label,
                System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                new Typeface("Segoe UI Semibold"),
                Math.Max(14, size / 5.0),
                new SolidColorBrush(Color.FromRgb(0xFF, 0xE5, 0x66)),
                1.0);
            dc.DrawText(ft, new Point((size - ft.Width) / 2, (size - ft.Height) / 2));
        }
        var rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
    }
}
