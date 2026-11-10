using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WorkflowDashboard;

public partial class DocsOverlayWindow : Window
{
    private readonly Action<string[]>? _onAddToDocs;
    private Action? _onClosed;
    private Action<string, int>? _onAssignToPane;
    private Action<int>? _onCycle;
    private Func<int>? _getCycleCount;
    private Action<string>? _onSwitchMode; // "docs" | "windows"
    private string _currentDir;
    private string? _previewPath;
    private bool _inPreview;
    private IntPtr _previewHwnd;
    private Action<IntPtr, int>? _onAssignWindow;
    private string? _windowTitle;
    private static DocsOverlayWindow? _openInstance;
    private DateTime _lastForceToFrontUtc;
    private const int QuietForceDebounceMs = 2000;
    public static bool HasOpenInstance => _openInstance != null && _openInstance.IsLoaded;

    /// <summary>Current doc path in Preview (null if window preview / none).</summary>
    public static string? GetPreviewPath() =>
        _openInstance != null && _openInstance.IsLoaded ? _openInstance._previewPath : null;

    /// <summary>Current HWND in window Preview (Zero if doc preview / none).</summary>
    public static IntPtr GetPreviewHwnd() =>
        _openInstance != null && _openInstance.IsLoaded ? _openInstance._previewHwnd : IntPtr.Zero;

    public DocsOverlayWindow(string initialDir, Action<string[]>? onAddToDocs, Action? onClosed, Action<string, int>? onAssignToPane = null)
    {
        InitializeComponent();
        _onAddToDocs = onAddToDocs;
        _onClosed = onClosed;
        _onAssignToPane = onAssignToPane;
        _currentDir = Directory.Exists(initialDir)
            ? initialDir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

        TxtTitle.Text = "Files";
        LoadFiles();

        Closed += (_, _) =>
        {
            try { Player.Stop(); } catch { }
            try { WindowThumb?.Clear(); } catch { }
            _onClosed?.Invoke();
        };
    }

    public static void ShowFilePicker(Window owner, string downloadsDir, Action<string[]> onAddToDocs, Action? onClosed = null)
    {
        if (_openInstance != null)
        {
            _openInstance._onClosed = onClosed;
            // Switch back to file list if previewing, refresh dir
            try
            {
                if (_openInstance._inPreview)
                    _openInstance.BackToFiles_Click(_openInstance, new RoutedEventArgs());
                _openInstance.SetFolder(downloadsDir);
                _openInstance.Activate();
                _openInstance.RaiseSuperTopmost();
                return;
            }
            catch
            {
                try { _openInstance.Close(); } catch { }
                _openInstance = null;
            }
        }

        var w = new DocsOverlayWindow(downloadsDir, onAddToDocs, onClosed)
        {
            Owner = null,
            Topmost = true,
            ShowActivated = true
        };
        _openInstance = w;
        w.Closed += (_, _) => { if (ReferenceEquals(_openInstance, w)) _openInstance = null; };
        w.Show();
        w.RaiseSuperTopmost();
    }

    public static void ShowPreview(Window owner, string filePath, Action? onClosed = null, Action<string, int>? onAssignToPane = null, int paneCount = 3, Action<int>? onCycle = null, Func<int>? getCycleCount = null, Action<string>? onSwitchMode = null)
    {
        if (_openInstance != null && _openInstance.IsLoaded)
        {
            _openInstance._onClosed = onClosed;
            _openInstance._onAssignToPane = onAssignToPane;
            _openInstance._onAssignWindow = null;
            _openInstance._onCycle = onCycle;
            _openInstance._getCycleCount = getCycleCount;
            _openInstance._onSwitchMode = onSwitchMode;
            _openInstance.ConfigureAssignButtons(paneCount);
            _openInstance.PlaceOverDeskNotSidebar(owner);
            _openInstance.OpenPreview(filePath);
            // Swap path: keep Docs focus enough for another double-click.
            _openInstance.ForceToFront(activate: false);
            return;
        }

        try { _openInstance?.Close(); } catch { }
        _openInstance = null;

        var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        var w = new DocsOverlayWindow(downloads, onAddToDocs: null, onClosed: onClosed, onAssignToPane: onAssignToPane)
        {
            Owner = null,
            Topmost = true,
            ShowActivated = true,
            Width = 720,
            Height = 640
        };
        w._onCycle = onCycle;
        w._getCycleCount = getCycleCount;
        w._onSwitchMode = onSwitchMode;
        _openInstance = w;
        w.Closed += (_, _) => { if (ReferenceEquals(_openInstance, w)) _openInstance = null; };
        w.PlaceOverDeskNotSidebar(owner);
        w.Show();
        w.PlaceOverDeskNotSidebar(owner); // re-center after Show (Manual Left/Top)
        w.ConfigureAssignButtons(paneCount);
        w.OpenPreview(filePath);
        w.ForceToFront(activate: true);
    }

    /// <summary>Preview a live HWND (new/unlinked window) with Assign-to-pane - same overlay as Docs.</summary>
    public static void ShowWindowPreview(Window? owner, IntPtr hwnd, string title, Action? onClosed = null, Action<IntPtr, int>? onAssignWindow = null, int paneCount = 3, Action<int>? onCycle = null, Func<int>? getCycleCount = null, Action<string>? onSwitchMode = null)
    {
        if (hwnd == IntPtr.Zero) return;

        if (_openInstance != null && _openInstance.IsLoaded)
        {
            _openInstance._onClosed = onClosed;
            _openInstance._onAssignToPane = null;
            _openInstance._onAssignWindow = onAssignWindow;
            _openInstance._onCycle = onCycle;
            _openInstance._getCycleCount = getCycleCount;
            _openInstance._onSwitchMode = onSwitchMode;
            _openInstance.ConfigureAssignButtons(paneCount);
            _openInstance.PlaceOverDeskNotSidebar(owner);
            _openInstance.OpenWindowPreview(hwnd, title);
            // Swap path: do not steal focus from Windows list (another click).
            _openInstance.ForceToFront(activate: false);
            return;
        }

        try { _openInstance?.Close(); } catch { }
        _openInstance = null;

        var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        var w = new DocsOverlayWindow(downloads, onAddToDocs: null, onClosed: onClosed)
        {
            Owner = null,
            Topmost = true,
            ShowActivated = true,
            Width = 720,
            Height = 640
        };
        w._onAssignWindow = onAssignWindow;
        w._onCycle = onCycle;
        w._getCycleCount = getCycleCount;
        w._onSwitchMode = onSwitchMode;
        _openInstance = w;
        w.Closed += (_, _) => { if (ReferenceEquals(_openInstance, w)) _openInstance = null; };
        w.PlaceOverDeskNotSidebar(owner);
        w.Show();
        w.PlaceOverDeskNotSidebar(owner); // re-center after Show (Manual Left/Top)
        w.ConfigureAssignButtons(paneCount);
        w.OpenWindowPreview(hwnd, title);
        w.ForceToFront(activate: true);
    }
    /// <summary>Keep Files/Preview above desk + Live TOPMOST tiles (called from dashboard).</summary>
    public static void RaiseOpenInstanceIfAny(bool activate = false, bool forceAboveTiles = false)
    {
        try
        {
            var w = _openInstance;
            if (w == null || !w.IsLoaded) return;
            w.ForceToFront(activate, forceAboveTiles);
        }
        catch { }
    }
    private void ConfigureAssignButtons(int paneCount)
    {
        int n = Math.Clamp(paneCount, 1, 3);
        _assignPaneCount = n;
        try
        {
            if (BtnAssignPane1 != null) BtnAssignPane1.Visibility = n >= 1 ? Visibility.Visible : Visibility.Collapsed;
            if (BtnAssignPane2 != null) BtnAssignPane2.Visibility = n >= 2 ? Visibility.Visible : Visibility.Collapsed;
            if (BtnAssignPane3 != null) BtnAssignPane3.Visibility = n >= 3 ? Visibility.Visible : Visibility.Collapsed;
            if (HeaderAssignChips != null) HeaderAssignChips.Visibility = Visibility.Visible;
        }
        catch { }
    }

    private void UpdateCycleButtons()
    {
        try
        {
            bool show = _inPreview && _onCycle != null;
            if (BtnCyclePrev != null) BtnCyclePrev.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (BtnCycleNext != null) BtnCycleNext.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            int count = 0;
            try { count = _getCycleCount?.Invoke() ?? 0; } catch { }
            bool enable = show && count > 1;
            if (BtnCyclePrev != null) BtnCyclePrev.IsEnabled = enable;
            if (BtnCycleNext != null) BtnCycleNext.IsEnabled = enable;
            UpdateModeToggleUi();
        }
        catch { }
    }

    private void CyclePrev_Click(object sender, RoutedEventArgs e) => RequestCycle(-1);

    private void CycleNext_Click(object sender, RoutedEventArgs e) => RequestCycle(+1);

    private void RequestCycle(int delta)
    {
        if (!_inPreview || _onCycle == null) return;
        try { _onCycle.Invoke(delta); }
        catch { }
        // Neighbor open uses ShowPreview/ShowWindowPreview swap path (ForceToFront activate:false).
        try { UpdateCycleButtons(); } catch { }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_inPreview || _onCycle == null) return;
        if (e.Key == Key.Left)
        {
            RequestCycle(-1);
            e.Handled = true;
        }
        else if (e.Key == Key.Right)
        {
            RequestCycle(+1);
            e.Handled = true;
        }
    }

    private void AssignPane_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        if (!int.TryParse(Convert.ToString(fe.Tag), out int zone)) return;
        try
        {
            if (_previewHwnd != IntPtr.Zero && _onAssignWindow != null)
            {
                _onAssignWindow.Invoke(_previewHwnd, zone);
                StatusHint("Linking window to pane " + (zone + 1) + "...");
                // Advance to the next window; close once every pane is filled (v1.0.2).
                AfterAssign(zone);
                return;
            }
            if (string.IsNullOrWhiteSpace(_previewPath)) return;
            _onAssignToPane?.Invoke(_previewPath!, zone);
            StatusHint("Linking to pane " + (zone + 1) + "...");
            AfterAssign(zone);
        }
        catch (Exception ex)
        {
            try { StatusHint("Assign failed: " + ex.Message); } catch { }
        }
    }
    /// <summary>Set by the dashboard: true when pane <c>z</c> already holds a linked window.</summary>
    public static Func<int, bool>? ZoneFilledProbe { get; set; }
    private readonly Dictionary<int, DateTime> _assignedZonesUtc = new();
    private int _assignPaneCount = 3;

    /// <summary>
    /// After an assign: every pane filled (already linked, or just assigned here within 20 s - doc links finish
    /// asynchronously) -> close Preview; otherwise advance to the next item as before.
    /// </summary>
    private void AfterAssign(int zone)
    {
        _assignedZonesUtc[zone] = DateTime.UtcNow;
        bool all = _assignPaneCount > 0;
        for (int z = 0; z < _assignPaneCount && all; z++)
        {
            bool recent = _assignedZonesUtc.TryGetValue(z, out var t) && (DateTime.UtcNow - t).TotalSeconds < 20;
            bool filled = false;
            try { filled = ZoneFilledProbe?.Invoke(z) == true; } catch { }
            all = recent || filled;
        }
        if (!all) { RequestCycle(+1); return; }
        StatusHint("All panes filled - closing Preview");
        Dispatcher.BeginInvoke(() => { try { Close(); } catch { } }, System.Windows.Threading.DispatcherPriority.Background);
    }

    private void RaiseSuperTopmost()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            Topmost = true;
            NativeMethods.SetWindowPos(
                hwnd,
                NativeMethods.HWND_TOPMOST,
                0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
        }
        catch { }
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        RaiseSuperTopmost();
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        RaiseSuperTopmost();
    }

    private void LoadFiles()
    {
        if (_inPreview) return;
        FileList.Items.Clear();
        TxtSub.Text = _currentDir;
        foreach (var fi in ListMediaAndDocs( _currentDir))
        {
            FileList.Items.Add(new OverlayFileItem
            {
                FullPath = fi.FullName,
                Label = $"{fi.Name}  |  {fi.LastWriteTime:MMM d HH:mm}  |  {FormatSize(fi.Length)}"
            });
        }

        FileList.DisplayMemberPath = "Label";
        if (FileList.Items.Count > 0) FileList.SelectedIndex = 0;
        if (FileList.Items.Count == 0)
            TxtSub.Text = _currentDir + "  (no previewable files here)";
    }

    private static List<FileInfo> ListMediaAndDocs(string dir)
    {
        var exts = PreviewableExts();
        try
        {
            return new DirectoryInfo(dir).GetFiles()
                .Where(f => exts.Contains(f.Extension))
                .OrderByDescending(f => f.LastWriteTime)
                .Take(300)
                .ToList();
        }
        catch { return new List<FileInfo>(); }
    }

    private static HashSet<string> PreviewableExts() => new(StringComparer.OrdinalIgnoreCase)
    {
        ".gif",".jpg",".jpeg",".png",".webp",".bmp",".tif",".tiff",".ico",".svg",
        ".mp4",".webm",".mov",".m4v",".avi",".wmv",".mkv",
        ".mp3",".wav",".ogg",".m4a",".flac",".aac",
        ".pdf",".txt",".csv",".json",".xml",".md",".log",".rtf",
        ".doc",".docx",".xls",".xlsx",".ppt",".pptx",".odt",".ods"
    };

    private void SetFolder(string dir)
    {
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return;
        _currentDir = dir;
        if (_inPreview) return;
        LoadFiles();
    }

    private void ModeWindows_Click(object sender, RoutedEventArgs e)
    {
        try { _onSwitchMode?.Invoke("windows"); }
        catch { }
    }

    private void ModeDocs_Click(object sender, RoutedEventArgs e)
    {
        try { _onSwitchMode?.Invoke("docs"); }
        catch { }
    }

    private void UpdateModeToggleUi()
    {
        try
        {
            bool show = _inPreview && _onSwitchMode != null;
            if (ModeToggleBar != null)
                ModeToggleBar.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (!show) return;

            bool windowsMode = _previewHwnd != IntPtr.Zero;
            void StyleSeg(System.Windows.Controls.Button? btn, bool active)
            {
                if (btn == null) return;
                if (active)
                {
                    btn.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty, "AccentBrush");
                    btn.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, "AccentTextBrush");
                    btn.Tag = "active";
                }
                else
                {
                    // Idle: transparent on the header, muted text; hover = accent text on accent-dim (template).
                    btn.Background = System.Windows.Media.Brushes.Transparent;
                    btn.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, "MutedTextBrush");
                    btn.Tag = "idle";
                }
            }
            StyleSeg(BtnModeWindows, windowsMode);
            StyleSeg(BtnModeDocs, !windowsMode);
        }
        catch { }
    }


    private string? SelectedPath()
    {
        if (FileList.SelectedItem is OverlayFileItem item) return item.FullPath;
        return null;
    }

    private void Add_Click(object sender, RoutedEventArgs e) => AddPaths(CollectSelectedPaths());

    private void AddPreview_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_previewPath))
            AddPaths(new[] { _previewPath! });
    }

    private string[] CollectSelectedPaths()
    {
        var paths = FileList.SelectedItems.Cast<OverlayFileItem>().Select(i => i.FullPath).ToArray();
        if (paths.Length == 0)
        {
            var one = SelectedPath();
            if (one != null) paths = new[] { one };
        }
        return paths;
    }

    private void AddPaths(string[] paths)
    {
        if (paths.Length == 0) return;
        _onAddToDocs?.Invoke(paths);
    }

    private void Preview_Click(object sender, RoutedEventArgs e)
    {
        var path = SelectedPath();
        if (path == null) return;
        OpenPreview(path);
        ForceToFront(activate: true);
    }

    private void FileList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        var path = SelectedPath();
        if (path == null) return;
        OpenPreview(path);
        ForceToFront(activate: true);
    }

    private void HideAllPreviews()
    {
        PreviewImage.Visibility = Visibility.Collapsed;
        PreviewImage.Source = null;
        Player.Visibility = Visibility.Collapsed;
        try { Player.Stop(); Player.Source = null; } catch { }
        PreviewText.Visibility = Visibility.Collapsed;
        PreviewText.Text = "";
        PreviewFallback.Visibility = Visibility.Collapsed;
        try { if (WindowThumb != null) { WindowThumb.Clear(); WindowThumb.Visibility = Visibility.Collapsed; } } catch { }
    }


    /// <summary>
    /// CenterPreview: place the singleton Preview over the Live desk (PaneGrid), not the full
    /// dashboard window. Side panel (Windows/Docs rail) and Flip (pane shares inside PaneGrid)
    /// are accounted for by using PaneGrid screen bounds converted to DIPs.
    /// </summary>
    private void PlaceOverDeskNotSidebar(Window? owner)
    {
        try
        {
            if (owner == null || !owner.IsVisible) return;

            // Default: whole owner window (DIPs).
            double deskLeft = owner.Left;
            double deskTop = owner.Top;
            double deskW = owner.ActualWidth > 1 ? owner.ActualWidth : Math.Max(400, owner.Width);
            double deskH = owner.ActualHeight > 1 ? owner.ActualHeight : Math.Max(400, owner.Height);

            // Prefer PaneGrid = Live desk host (excludes right SidePanel; Flip only reshuffles columns inside).
            try
            {
                var paneGrid = owner.FindName("PaneGrid") as FrameworkElement;
                if (paneGrid != null && paneGrid.IsVisible
                    && paneGrid.ActualWidth > 2 && paneGrid.ActualHeight > 2)
                {
                    var tlPx = paneGrid.PointToScreen(new Point(0, 0));
                    var brPx = paneGrid.PointToScreen(new Point(paneGrid.ActualWidth, paneGrid.ActualHeight));
                    var src = PresentationSource.FromVisual(owner);
                    if (src?.CompositionTarget != null)
                    {
                        var toDip = src.CompositionTarget.TransformFromDevice;
                        var tl = toDip.Transform(tlPx);
                        var br = toDip.Transform(brPx);
                        deskLeft = Math.Min(tl.X, br.X);
                        deskTop = Math.Min(tl.Y, br.Y);
                        deskW = Math.Abs(br.X - tl.X);
                        deskH = Math.Abs(br.Y - tl.Y);
                    }
                }
                else
                {
                    // Fallback when PaneGrid not ready: subtract visible SidePanel from owner width.
                    var side = owner.FindName("SidePanel") as FrameworkElement;
                    if (side != null && side.IsVisible && side.ActualWidth > 1)
                    {
                        double sideW = side.ActualWidth + side.Margin.Left + side.Margin.Right;
                        // SidePanel is Dock Right — desk is the left remainder.
                        deskW = Math.Max(200, deskW - sideW);
                    }
                }
            }
            catch { }

            if (deskW < 80 || deskH < 80) return;

            // Keep a usable size (not full-screen); clamp to desk.
            Width = Math.Min(900, Math.Max(420, deskW * 0.70));
            Height = Math.Min(Math.Max(420, deskH * 0.85), Math.Max(420, deskH - 24));
            if (Width > deskW - 8) Width = Math.Max(320, deskW - 8);
            if (Height > deskH - 8) Height = Math.Max(280, deskH - 8);
            Left = deskLeft + (deskW - Width) / 2.0;
            Top = deskTop + (deskH - Height) / 2.0;
            WindowStartupLocation = WindowStartupLocation.Manual;
        }
        catch { }
    }

        private void ForceToFront(bool activate = true, bool forceAboveTiles = false)
    {
        try
        {
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Show();
            Topmost = true;
            // Never Activate on quiet/force-above-tiles paths (assign/Assert blink source).
            if (activate && !forceAboveTiles)
                Activate();

            // SoftRefresh idle keep-alive: debounce quiet raises.
            // After Assert/Assign tiles re-TOPMOST: forceAboveTiles bypasses debounce (must win z-order).
            var now = DateTime.UtcNow;
            if (!activate && !forceAboveTiles && (now - _lastForceToFrontUtc).TotalMilliseconds < QuietForceDebounceMs)
                return;
            _lastForceToFrontUtc = now;

            RaiseSuperTopmost();
            // Second raise only on user/open activate — never on SoftRefresh/Assert quiet path.
            if (activate && !forceAboveTiles)
            {
                Dispatcher.BeginInvoke(() => { try { RaiseSuperTopmost(); } catch { } },
                    System.Windows.Threading.DispatcherPriority.Background);
            }
        }
        catch { }
    }


    private void OpenWindowPreview(IntPtr hwnd, string title)
    {
        _previewPath = null;
        _previewHwnd = hwnd;
        _windowTitle = title ?? "";
        _inPreview = true;
        FileList.Visibility = Visibility.Collapsed;
        FileActions.Visibility = Visibility.Collapsed;
        PreviewActions.Visibility = Visibility.Visible;
        PreviewHost.Visibility = Visibility.Visible;
        TxtTitle.Text = "Window preview";
        TxtSub.Text = string.IsNullOrWhiteSpace(_windowTitle) ? "Untitled window" : _windowTitle;
        HideAllPreviews();
        try
        {
            if (WindowThumb != null)
            {
                WindowThumb.Visibility = Visibility.Visible;
                WindowThumb.TrimOsFrame = true;   // no white OS border / resize frame around the live preview
                WindowThumb.Assign(hwnd);
            }
            TxtPreviewHint.Text = "Live window: pick a pane above, or Close to leave it free.";
        }
        catch (Exception ex)
        {
            ShowFallback("", "Window preview failed: " + ex.Message);
        }
        UpdateCycleButtons();
    }
    private void OpenPreview(string path)
    {
        _previewHwnd = IntPtr.Zero;
        _onAssignWindow = null;
        _previewPath = path;
        _inPreview = true;
        FileList.Visibility = Visibility.Collapsed;
        FileActions.Visibility = Visibility.Collapsed;
        PreviewActions.Visibility = Visibility.Visible;
        PreviewHost.Visibility = Visibility.Visible;
        TxtTitle.Text = "Preview";
        TxtSub.Text = Path.GetFileName(path);
        HideAllPreviews();
        try { if (WindowThumb != null) { WindowThumb.Clear(); WindowThumb.Visibility = Visibility.Collapsed; } } catch { }

        var ext = Path.GetExtension(path).ToLowerInvariant();
        TxtPreviewHint.Text = "";

        try
        {
            if (IsImageExt(ext))
            {
                var bi = new BitmapImage();
                bi.BeginInit();
                bi.CacheOption = BitmapCacheOption.OnLoad;
                bi.UriSource = new Uri(Path.GetFullPath(path));
                bi.EndInit();
                bi.Freeze();
                PreviewImage.Source = bi;
                PreviewImage.Visibility = Visibility.Visible;
                TxtPreviewHint.Text = "Image preview";
                UpdateCycleButtons();
                return;
            }

            if (IsAudioVideoExt(ext))
            {
                try { Player.Stop(); } catch { }
                Player.Source = null;
                Player.Source = new Uri(path);
                Player.Position = TimeSpan.Zero;
                Player.Visibility = Visibility.Visible;
                Player.Play();
                TxtPreviewHint.Text = IsAudioExt(ext) ? "Audio - use Play/Pause" : "Video preview";
                UpdateCycleButtons();
                return;
            }

            if (IsTextExt(ext))
            {
                var text = File.ReadAllText(path);
                if (text.Length > 200_000) text = text.Substring(0, 200_000) + "\n\n... truncated ...";
                PreviewText.Text = text;
                PreviewText.Visibility = Visibility.Visible;
                TxtPreviewHint.Text = "Text preview";
                UpdateCycleButtons();
                return;
            }

            if (ext == ".pdf")
            {
                if (TryPreviewPdf(path))
                {
                    TxtPreviewHint.Text = "PDF first page";
                    UpdateCycleButtons();
                    return;
                }
                ShowFallback(path, "Could not render this PDF inline.");
                UpdateCycleButtons();
                return;
            }

            ShowFallback(path, $"No inline preview for {ext.TrimStart('.').ToUpperInvariant()} files.");
        }
        catch (Exception ex)
        {
            ShowFallback(path, "Preview failed: " + ex.Message);
        }
        UpdateCycleButtons();
    }

    private void ShowFallback(string path, string message)
    {
        HideAllPreviews();
        TxtFallback.Text = message + "\n\n" + Path.GetFileName(path);
        PreviewFallback.Visibility = Visibility.Visible;
        TxtPreviewHint.Text = "Open with your default app if needed";
    }

    private bool TryPreviewPdf(string path)
    {
        try
        {
            // WinRT PDF API (Windows 10+)
            var fileTask = Windows.Storage.StorageFile.GetFileFromPathAsync(Path.GetFullPath(path)).AsTask();
            var file = fileTask.GetAwaiter().GetResult();
            var pdfTask = Windows.Data.Pdf.PdfDocument.LoadFromFileAsync(file).AsTask();
            var pdf = pdfTask.GetAwaiter().GetResult();
            if (pdf.PageCount == 0) return false;

            using var ras = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            var page = pdf.GetPage(0);
            var renderTask = page.RenderToStreamAsync(ras).AsTask();
            renderTask.GetAwaiter().GetResult();
            page.Dispose();

            ras.Seek(0);
            using var net = ras.AsStreamForRead();
            using var ms = new MemoryStream();
            net.CopyTo(ms);
            ms.Position = 0;
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.StreamSource = ms;
            bi.EndInit();
            bi.Freeze();
            PreviewImage.Source = bi;
            PreviewImage.Visibility = Visibility.Visible;
            if (pdf.PageCount > 1)
                TxtPreviewHint.Text = $"PDF page 1 of {pdf.PageCount}";
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void OpenExternal_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_previewPath)) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_previewPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            try { StatusHint(ex.Message); } catch { }
        }
    }

    private void StatusHint(string msg) => TxtPreviewHint.Text = msg;

    private void BackToFiles_Click(object sender, RoutedEventArgs e)
    {
        try { Player.Stop(); } catch { }
        Player.Source = null;
        _inPreview = false;
        _previewPath = null;
        _previewHwnd = IntPtr.Zero;
        _onAssignWindow = null;
        _onCycle = null;
        _getCycleCount = null;
        HideAllPreviews();
        PreviewHost.Visibility = Visibility.Collapsed;
        PreviewActions.Visibility = Visibility.Collapsed;
        try { if (HeaderAssignChips != null) HeaderAssignChips.Visibility = Visibility.Collapsed; } catch { }
        _onSwitchMode = null;
        try { UpdateModeToggleUi(); } catch { }
        FileList.Visibility = Visibility.Visible;
        FileActions.Visibility = Visibility.Visible;
        TxtTitle.Text = "Files";
        UpdateCycleButtons();
        LoadFiles();
    }

    private void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (Player.CanPause) Player.Pause();
            else Player.Play();
        }
        catch
        {
            try { Player.Play(); } catch { }
        }
    }

    private void Player_MediaEnded(object sender, RoutedEventArgs e)
    {
        try { Player.Position = TimeSpan.Zero; } catch { }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private static bool IsImageExt(string ext) =>
        ext is ".jpg" or ".jpeg" or ".png" or ".gif" or ".webp" or ".bmp" or ".tif" or ".tiff" or ".ico";

    private static bool IsAudioExt(string ext) =>
        ext is ".mp3" or ".wav" or ".ogg" or ".m4a" or ".flac" or ".aac";

    private static bool IsAudioVideoExt(string ext) =>
        IsAudioExt(ext) || ext is ".mp4" or ".webm" or ".mov" or ".m4v" or ".avi" or ".wmv" or ".mkv";

    private static bool IsTextExt(string ext) =>
        ext is ".txt" or ".csv" or ".json" or ".xml" or ".md" or ".log";

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return bytes + " B";
        if (bytes < 1024 * 1024) return (bytes / 1024d).ToString("0.#") + " KB";
        return (bytes / (1024d * 1024d)).ToString("0.#") + " MB";
    }

    private sealed class OverlayFileItem
    {
        public string FullPath { get; set; } = "";
        public string Label { get; set; } = "";
    }
}
