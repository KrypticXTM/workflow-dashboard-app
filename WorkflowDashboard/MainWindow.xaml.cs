using System.Text;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace WorkflowDashboard;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer _refreshTimer;
    private readonly DispatcherTimer _mediaTimer;
    private readonly MediaSessionService _media = new();
    private readonly HotkeyService _hotkeys = new();
    private readonly WindowTiler _tiler = new();
    private readonly AppSettings _settings;
    private readonly Button[] _paneBtns = new Button[4];
    private readonly PaneSlot[] _slots = new PaneSlot[4];

    private int _activePane = 1;
    private int _paneCount = 3;
    private string _layout = "equal";
    private IntPtr _ownHwnd = IntPtr.Zero;
    private bool _sideCollapsed;
    private bool _distractionFree;
    /// <summary>Topmost "Show UI" chip so exit-full-screen is not buried under Live HWND_TOPMOST tiles.</summary>
    private Window? _showUiOverlay;
    private int _paneDragFromZone = -1;
    private int _paneBtnDragVisual = -1;
    private Point _paneBtnDragStart;
    private Point _paneDragStart;
    private bool _docsMode;
    private DateTime _docsChromeQuietUntilUtc;
    private DateTime _suppressLiveRetileUntilUtc;
    private DateTime _chromePopupQuietUntilUtc;
    private bool _yieldedForOutsideFocus;
    private bool _colorStripOpen;
    private bool _docsFileDragClickThru;
    private DateTime _holdFillUntilUtc;
    private DispatcherTimer? _holdFillTimer;
    private bool _liveDesk = true;
    private bool _mainOnLeft = true;
    private double _uiZoom = 1.0;
    private string _sizePreset = ""; // full | 90 | 80 | 70 | empty
    private string _fitMode = "fill"; // fill | fit
    private string _accentHex = "#FFE566";
    private string _accentDefaultHex = "#FFE566";
    private string _backgroundHex = "#000000";
    private string _backgroundDefaultHex = "#000000";
    private int _wmpSuggestW;
    private int _wmpSuggestH;
    private bool _fileDropBusy;
    private readonly HashSet<IntPtr> _autoPreviewSeenHwnds = new();
    private DateTime _suppressAutoWindowPreviewUntilUtc;
    private IntPtr _lastAutoPreviewHwnd;
    private string? _lastFileDropPath;
    private DateTime _lastFileDropUtc;
    private int _holdFillZone = -1;
    private readonly string?[] _paneFitMode = new string?[4]; // null = use global _fitMode
    private bool _applyingSizePreset;
    private bool _lockHeight;
    private double _lockedHeight;
    private double _lockedTop;
    private bool _applyingLockHeight;
    private bool _deskMinimized;
    /// <summary>
    /// STABILIZE BUILD: crop/preview park/cloak OFF. Live Fill only.
    /// Assigned Live tiles use HWND_TOPMOST while desk is Active OR while Tim is focused on an
    /// assigned Live tile. On Deactivated to an unassigned window, clear TOPMOST so normal
    /// desktop z-order returns. Deactivated onto a Live-assigned HWND keeps pins (no blank panes).
    /// Desk Topmost=true while Activated (cleared on Deactivated / minimize / snip yield).
    /// </summary>
    private const bool CropPreviewEnabled = false;

    private DispatcherTimer? _liveRetileTimer;
    private DispatcherTimer? _snipWatchTimer;
    private bool _snipYieldActive;
    /// <summary>True after first full Ensure+Raise at snip start; SoftRefresh only heals if needed.</summary>
    private bool _snipStablePlaced;
    /// <summary>Zone that needed FitWidth (too thin for Fill); Expand button targets it.</summary>
    private int _thinExpandHintZone = -1;
 // / <summary>HWNDs we pinned HWND_TOPMOST for Live Fill '- sweep-cleared when unassigned or desk yields.</summary>
    private readonly HashSet<IntPtr> _liveTopmostPinned = new();
    private bool _didLaunchBrowserTopmostSweep;

    // Remember multi-pane layout before Expand / one-pane focus-in
    private bool _hasLayoutRestore;
    private int _savedPaneCount = 3;
    private string _savedLayout = "equal";
    private int _savedActivePane = 1;
    private bool _expandedMode;
    private int _expandedFromIndex; // zone index promoted to 0 during Expand

    private Point _dragStart;
    private bool _dragPending;
    private bool _suppressPaneClick;
    private bool _suppressPaneBtnClick;
    private int _activePaneBeforeBtnClick = 1;
    private Point _workingDragStart;
    private bool _workingDragPending;
    private WorkingWindow? _dragWorking;

    private sealed class PaneSlot
    {
        public Border Root = null!;
        public Grid ContentGrid = null!;
        public FrameworkElement TileHost = null!;
        public ThumbnailHost Thumb = null!;
        public StackPanel Placeholder = null!;
        public Image Icon = null!;
        public TextBlock Title = null!;
        public TextBlock Caption = null!;
        public TextBlock Hint = null!;
        public Button ExpandBtn = null!;
        public Border FitPrompt = null!;
        public Button CropBtn = null!; // demoted; kept for UpdateCropButtons compatibility
        public Button UncropBtn = null!;
        public Button MenuBtn = null!;
        public Button MoveLeftBtn = null!;
        public Button MoveRightBtn = null!;
        public Canvas CropOverlay = null!;
        public Border LinkOutline = null!;
        public int Index;
    }

    public MainWindow()
    {
        InitializeComponent();
        // Frameless custom title bar + maximized padding + update hook (MainWindow.TitleBar.cs).
        try { InitCustomTitleBar(); } catch { }
        try
        {
            if (BtnAccentColor != null)
            {
                BtnAccentColor.PreviewMouseLeftButtonDown += (_, __) =>
                {
                    _chromePopupQuietUntilUtc = DateTime.UtcNow.AddSeconds(2);
                    _suppressLiveRetileUntilUtc = DateTime.UtcNow.AddMilliseconds(500);
                };
            }
            try { BuildColorStrip(); } catch { }
        }
        catch { }
        _settings = SettingsStore.Load();
        _paneCount = Math.Clamp(_settings.PaneCount, 1, 3);
        _layout = _settings.Layout;
        if (string.Equals(_layout, "focus", StringComparison.OrdinalIgnoreCase))
            _layout = "large";
        if (_layout is not ("equal" or "main" or "large"))
            _layout = "equal";
        _activePane = Math.Clamp(_settings.ActivePane, 1, _paneCount);
        _fitMode = string.Equals(_settings.FitMode, "fit", StringComparison.OrdinalIgnoreCase) ? "fit" : "fill";
        _accentHex = string.IsNullOrWhiteSpace(_settings.AccentHex) ? "#FFE566" : _settings.AccentHex.Trim();
        _accentDefaultHex = string.IsNullOrWhiteSpace(_settings.AccentDefaultHex) ? "#FFE566" : _settings.AccentDefaultHex.Trim();
        _backgroundHex = string.IsNullOrWhiteSpace(_settings.BackgroundHex) ? "#000000" : _settings.BackgroundHex.Trim();
        _backgroundDefaultHex = string.IsNullOrWhiteSpace(_settings.BackgroundDefaultHex) ? "#000000" : _settings.BackgroundDefaultHex.Trim();
        _sideCollapsed = _settings.SidePanelCollapsed;
        _liveDesk = _settings.LiveDesk;
        _mainOnLeft = _settings.MainOnLeft;
        _uiZoom = Math.Clamp(_settings.Zoom, 0.8, 1.5);
        _sizePreset = _settings.SizePreset ?? "";
 // Lock H is session-only '- never restore selected on open (Tim: click-to-fix dance).
        _lockHeight = false;
        if (string.Equals(_sizePreset, "tall", StringComparison.OrdinalIgnoreCase))
            _sizePreset = "";

        BuildPaneButtons();
        BuildPaneSlots();
        PaneGrid.LayoutUpdated += (_, _) => UpdateTrayBevel();
        MediaList.ItemsSource = _media.Sessions;
        SyncMediaNowPlayingUi();
        SyncVolumeSlider();

        Loaded += OnLoaded;
        PreviewKeyDown += (_, e) =>
        {
            if (HandleLiveDiagHotkey(e)) return;
            if (e.Key == Key.Escape && _cropModeZone >= 0)
            {
                EndCropMode(apply: false);
                RefreshAllThumbnails();
                if (_liveDesk) ApplyLiveTiling();
                StatusText.Text = "Crop cancelled";
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Escape && _distractionFree)
            {
                SetDistractionFree(false);
                e.Handled = true;
            }
        };
        ContentRendered += OnContentRendered;
        Closed += OnClosed;
        // Pane-follow resize: debounce SizeChanged so linked HWNDs track pane screen rects
        // without SetWindowPos spam (black flashes). Final quiet size always retiles.
        SizeChanged += (_, e) =>
        {
            EnforceLockHeightOnSizeChanged(e);
            if (_distractionFree) SyncShowUiOverlay(); // full-screen chip
            // Color/theme thrash used to fire SizeChanged + full thumb refresh = black blink.
            if (DateTime.UtcNow < _suppressLiveRetileUntilUtc) return;
            ScheduleLiveRetile();
        };
        SourceInitialized += OnSourceInitialized;
        LocationChanged += (_, _) =>
        {
            if (_lockHeight && !_applyingLockHeight && !_applyingSizePreset
                && WindowState != WindowState.Maximized && WindowState != WindowState.Minimized)
            {
                // Allow move: update locked Top so subsequent height restores stay put.
                _lockedTop = Top;
            }
            if (_distractionFree) SyncShowUiOverlay();
            ScheduleLiveRetile();
        };
        Activated += Dashboard_Activated;
        StateChanged += Dashboard_StateChanged;
        Deactivated += Dashboard_Deactivated;

        _liveRetileTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(75) };
        _liveRetileTimer.Tick += (_, _) =>
        {
            _liveRetileTimer!.Stop();
            try
            {
                UpdateLayout();
                PaneGrid?.UpdateLayout();
            }
            catch { }
            ApplyLiveTiling();
        };

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        _refreshTimer.Tick += (_, _) => SoftRefresh();

        _mediaTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
        _mediaTimer.Tick += async (_, _) =>
        {
            try
            {
                _media.Refresh();
                SyncMediaPaneLabels();
                SyncVolumeSlider();
                await RefreshNowPlayingNowAsync(); // v1.0.2: shared with SMTC event refresh, never overlaps
            }
            catch { }
        };

        // Snipping Tool / Screen Sketch: desk Topmost buries snip UI; yield while visible.
        _snipWatchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _snipWatchTimer.Tick += (_, _) => CheckSnipToolYield();
    }

    private void BuildPaneButtons()
    {
        PaneButtons.Children.Clear();
        PaneButtons.ColumnDefinitions.Clear();
        PaneButtons.RowDefinitions.Clear();
        PaneButtons.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (int i = 0; i < 3; i++)
        {
            var btn = new Button
            {
                Content = $"{i + 1} <drag>",
                Tag = i + 1,
                Margin = new Thickness(0, 0, 3, 0),
                Padding = new Thickness(6, 2, 6, 2),
                MinWidth = 56,
                Height = 28, // v1.0.1 keycap face is inset 2px top / 4px bottom
                FontSize = 11,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                FontWeight = FontWeights.Bold,
                AllowDrop = true,
                Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = "Drag to another pane to swap  |  Click: Link Window  |  Double-click: swap focused here"
            };
            btn.Click += PaneBtn_Click;
            btn.PreviewMouseLeftButtonDown += PaneBtn_PreviewMouseLeftButtonDown;
            btn.PreviewMouseMove += PaneBtn_PreviewMouseMove;
            btn.PreviewMouseLeftButtonUp += PaneBtn_PreviewMouseLeftButtonUp;
            btn.DragOver += PaneBtn_DragOver;
            btn.DragLeave += PaneBtn_DragLeave;
            btn.Drop += PaneBtn_Drop;
            AttachPaneChipCollapseMenu(btn, i + 1);
            _paneBtns[i] = btn;
        }
    }

    private void BuildPaneSlots()
    {
        for (int i = 0; i < 4; i++)
        {
            var thumb = new ThumbnailHost();
            var icon = new Image { Width = 28, Height = 28, Stretch = Stretch.Uniform, Margin = new Thickness(0, 0, 0, 8) };
            var title = new TextBlock
            {
                Text = "Empty",
                FontSize = 12,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 220
            };
            title.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
            var placeholder = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Children = { icon, title }
            };
            var caption = new TextBlock
            {
                Text = $"Pane {i + 1}",
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(8, 6, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                IsHitTestVisible = false
            };
            caption.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
            var hint = new TextBlock
            {
                Text = "Preview only - Focus to interact",
                FontSize = 10,
                Margin = new Thickness(8, 0, 8, 8),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Bottom,
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed
            };
            hint.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");

            // Hidden legacy Expand (still driven by HighlightActivePane / Expand API).
            var expandBtn = new Button
            {
                Content = "Expand",
                FontSize = 10,
                Padding = new Thickness(8, 3, 8, 3),
                Tag = i,
                Visibility = Visibility.Collapsed,
                IsHitTestVisible = false
            };
            expandBtn.Click += PaneExpandBtn_Click;

 // Demoted Crop/Uncrop buttons '--'-'--'-'-'--'--'- actions live in the tiny top-center menu.
            var cropBtn = new Button
            {
                Content = "Crop",
                Tag = i,
                Visibility = Visibility.Collapsed,
                IsHitTestVisible = false
            };
            cropBtn.Click += PaneCropBtn_Click;
            var uncropBtn = new Button
            {
                Content = "Uncrop",
                Tag = i,
                Visibility = Visibility.Collapsed,
                IsHitTestVisible = false,
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                Padding = new Thickness(8, 3, 8, 3),
                Margin = new Thickness(8, 3, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Cursor = Cursors.Hand,
                BorderThickness = new Thickness(1),
                ToolTip = "Remove crop and restore Live Fill"
            };
            // Live theme refs (FindResource/Brushes.White snapshot never followed theme swaps).
            uncropBtn.SetResourceReference(Control.BackgroundProperty, "AccentDimBrush");
            uncropBtn.SetResourceReference(Control.ForegroundProperty, "AccentOnDimTextBrush");
            uncropBtn.SetResourceReference(Control.BorderBrushProperty, "AccentBrush");
            Panel.SetZIndex(uncropBtn, 260);
            uncropBtn.Click += PaneUncropBtn_Click;

            var btnResize = new Button { Content = "Change layout", FontSize = 11, Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(0, 0, 0, 6), Tag = i, HorizontalAlignment = HorizontalAlignment.Stretch };
            btnResize.Click += FitPrompt_ResizeDesk_Click;
            var btnGrowDesk = new Button { Content = "Expand dashboard to fit", FontSize = 11, Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(0, 0, 0, 6), Tag = i, HorizontalAlignment = HorizontalAlignment.Stretch,
                ToolTip = "Grow the dashboard window just enough for this app's minimum size (stays on this monitor)" };
            btnGrowDesk.Click += FitPrompt_ExpandDesk_Click;
            var btnMoveMain = new Button { Content = "Move to main pane", FontSize = 11, Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(0, 0, 0, 6), Tag = i, HorizontalAlignment = HorizontalAlignment.Stretch };
            btnMoveMain.Click += FitPrompt_MoveMain_Click;
            var btnKeep = new Button { Content = "Keep preview", FontSize = 11, Padding = new Thickness(10, 5, 10, 5), Tag = i, HorizontalAlignment = HorizontalAlignment.Stretch };
            btnKeep.Click += FitPrompt_KeepPreview_Click;
            var promptStack = new StackPanel
            {
                Margin = new Thickness(12),
                Children =
                {
                    new TextBlock
                    {
                        Text = "This pane is too small for the app - choose an option",
                        FontSize = 12,
                        FontWeight = FontWeights.Bold,
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(0, 0, 0, 8),
                        TextAlignment = TextAlignment.Center
                    },
                    btnGrowDesk,
                    btnResize,
                    btnMoveMain,
                    btnKeep
                }
            };
            var fitPrompt = new Border
            {
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(4),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                MaxWidth = 280,
                Child = promptStack,
                Visibility = Visibility.Collapsed,
                Tag = i
            };
            // Themed card: panel surface + theme text (was fixed near-black + White).
            fitPrompt.SetResourceReference(Border.BackgroundProperty, "PanelAltBrush");
            fitPrompt.SetResourceReference(Border.BorderBrushProperty, "AccentBrush");
            fitPrompt.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "TextBrush");

            var cropOverlay = new Canvas
            {
                // Nearly-transparent brush so hit-testing always works while cropping.
                Background = new SolidColorBrush(Color.FromArgb(0x01, 0, 0, 0)),
                Visibility = Visibility.Collapsed,
                IsHitTestVisible = true,
                Focusable = true
            };
            Panel.SetZIndex(cropOverlay, 200);
            cropOverlay.MouseLeftButtonDown += CropOverlay_MouseLeftButtonDown;
            cropOverlay.MouseMove += CropOverlay_MouseMove;
            cropOverlay.MouseLeftButtonUp += CropOverlay_MouseLeftButtonUp;

            // Tiny top-center per-pane menu (...)
            var menuCrop = new MenuItem { Header = "Crop", Tag = i };
            menuCrop.Click += PaneMenuCrop_Click;
            var menuUncrop = new MenuItem { Header = "Uncrop", Tag = i };
            menuUncrop.Click += PaneMenuUncrop_Click;
            var menuFill = new MenuItem { Header = "Fill", Tag = i };
            menuFill.Click += PaneMenuFill_Click;
            var menuFit = new MenuItem { Header = "Fit", Tag = i };
            menuFit.Click += PaneMenuFit_Click;
            var menuUnlink = new MenuItem { Header = "Unlink", Tag = i };
            menuUnlink.Click += PaneMenuUnlink_Click;
            var menuExpand = new MenuItem { Header = "Expand", Tag = i };
            menuExpand.Click += PaneMenuExpand_Click;
            var menuFloat = new MenuItem { Header = "Float (soon)", IsEnabled = false, Tag = i };
            var menuCollapse = BuildCollapseMenuItem(i);
            var paneMenu = new ContextMenu
            {
                Items =
                {
                    menuUncrop, menuCrop,
                    new Separator(),
                    menuFill, menuFit,
                    new Separator(),
                    menuUnlink, menuExpand, menuCollapse, menuFloat
                }
            };
            paneMenu.Opened += (_, _) => { RefreshPaneMenuState(i, paneMenu); SyncCollapseMenuItem(menuCollapse, i); };

            var menuBtn = new Button
            {
                Content = "...",
                Width = 22,
                Height = 18,
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                Padding = new Thickness(0, -2, 0, 0),
                Margin = new Thickness(0, 3, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top,
                Tag = i,
                ToolTip = "Pane menu: Crop / Uncrop / Fill / Fit / Unlink",
                Cursor = Cursors.Hand,
                /* themed */ Background = Brushes.Transparent,
                /* themed */ BorderBrush = Brushes.Transparent,
                BorderThickness = new Thickness(1),
                Opacity = 0.92,
                ContextMenu = paneMenu
            };
            Panel.SetZIndex(menuBtn, 250);
            menuBtn.Click += PaneMenuBtn_Click;
            // MenuBtn theme bind
            menuBtn.SetResourceReference(Control.BackgroundProperty, "PanelAltBrush");
            menuBtn.SetResourceReference(Control.BorderBrushProperty, "BorderBrushQuiet");
            menuBtn.SetResourceReference(Control.ForegroundProperty, "TextBrush");

            var moveLeftBtn = new Button
            {
                Content = "\u25C0",
                Width = 22,
                Height = 18,
                FontSize = 10,
                Padding = new Thickness(0),
                Margin = new Thickness(4, 3, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Tag = i,
                ToolTip = "Move linked window to the pane on the left (unlinks whatever is there)",
                Cursor = Cursors.Hand,
                /* themed */ Background = Brushes.Transparent,
                Foreground = (Brush)FindResource("AccentBrush"),
                /* themed */ BorderBrush = Brushes.Transparent,
                BorderThickness = new Thickness(1),
                Opacity = 0.92
            };
            Panel.SetZIndex(moveLeftBtn, 250);
            moveLeftBtn.PreviewMouseLeftButtonDown += PaneMoveLeft_PreviewMouseLeftButtonDown;
            moveLeftBtn.SetResourceReference(Control.BackgroundProperty, "PanelAltBrush");
            moveLeftBtn.SetResourceReference(Control.BorderBrushProperty, "BorderBrushQuiet");
            moveLeftBtn.SetResourceReference(Control.ForegroundProperty, "AccentBrush");

            var moveRightBtn = new Button
            {
                Content = "\u25B6",
                Width = 22,
                Height = 18,
                FontSize = 10,
                Padding = new Thickness(0),
                Margin = new Thickness(0, 3, 4, 0),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Tag = i,
                ToolTip = "Move linked window to the pane on the right (unlinks whatever is there)",
                Cursor = Cursors.Hand,
                /* themed */ Background = Brushes.Transparent,
                Foreground = (Brush)FindResource("AccentBrush"),
                /* themed */ BorderBrush = Brushes.Transparent,
                BorderThickness = new Thickness(1),
                Opacity = 0.92
            };
            Panel.SetZIndex(moveRightBtn, 250);
            moveRightBtn.PreviewMouseLeftButtonDown += PaneMoveRight_PreviewMouseLeftButtonDown;
            moveRightBtn.SetResourceReference(Control.BackgroundProperty, "PanelAltBrush");
            moveRightBtn.SetResourceReference(Control.BorderBrushProperty, "BorderBrushQuiet");
            moveRightBtn.SetResourceReference(Control.ForegroundProperty, "AccentBrush");

            // TileHost fills the pane (no caption chrome row).
            var grid = new Grid { Background = Brushes.Black };
            grid.Children.Add(thumb);
            grid.Children.Add(placeholder);
            grid.Children.Add(hint);
            grid.Children.Add(fitPrompt);
            grid.Children.Add(cropOverlay);
            // Keep demoted buttons in tree for legacy handlers (collapsed).
            grid.Children.Add(cropBtn);
            grid.Children.Add(uncropBtn);
            grid.Children.Add(expandBtn);

            var linkOutline = new Border
            {
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(LinkTargetIndicator.Thickness),
                CornerRadius = new CornerRadius(0),
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed,
                Opacity = 0,
                SnapsToDevicePixels = true
            };
            linkOutline.SetResourceReference(Border.BorderBrushProperty, "AccentBrush");
            linkOutline.SetResourceReference(UIElement.EffectProperty, "AccentGlowEffect");
            Panel.SetZIndex(linkOutline, 420);
            grid.Children.Add(linkOutline);

            // No per-pane caption bar: hardcoded #0A0A0A chrome fought themes, and sidebar
            // chips already show assign target / titles. Menu stays available via ContextMenu
            // (right-click empty/preview panes); Live tiles use the overlay menu when needed.
            caption.Visibility = Visibility.Collapsed;
            moveLeftBtn.Visibility = Visibility.Collapsed;
            moveRightBtn.Visibility = Visibility.Collapsed;
            menuBtn.Visibility = Visibility.Collapsed;

            var root = new Border
            {
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(0),
                Margin = new Thickness(0),
                Cursor = Cursors.Hand,
                Child = grid,
                ContextMenu = paneMenu,
                Tag = i
            };
            root.SetResourceReference(Border.BorderBrushProperty, "BorderBrushQuiet");
            root.PreviewMouseLeftButtonDown += Pane_PreviewMouseLeftButtonDown;
            root.MouseLeftButtonUp += Pane_Click;
            root.AllowDrop = true;
            root.DragOver += Pane_DragOver;
            root.DragLeave += Pane_DragLeave;
            root.Drop += Pane_Drop;
        root.PreviewMouseLeftButtonDown += PaneHost_PreviewMouseLeftButtonDown;
        root.PreviewMouseMove += PaneHost_PreviewMouseMove;
        root.PreviewMouseLeftButtonUp += PaneHost_PreviewMouseLeftButtonUp;
            root.ToolTip = "Click: select Link Window  -  Drop a window here to link (swap if occupied)";

            _slots[i] = new PaneSlot
            {
                Root = root,
                ContentGrid = grid,
                TileHost = grid,
                Thumb = thumb,
                Placeholder = placeholder,
                Icon = icon,
                Title = title,
                Caption = caption,
                Hint = hint,
                ExpandBtn = expandBtn,
                FitPrompt = fitPrompt,
                CropBtn = cropBtn,
                UncropBtn = uncropBtn,
                MenuBtn = menuBtn,
                MoveLeftBtn = moveLeftBtn,
                MoveRightBtn = moveRightBtn,
                CropOverlay = cropOverlay,
                LinkOutline = linkOutline,
                Index = i
            };
        }

        ApplyPaneLayout();
    }

    /// <summary>
    /// Apply Equal / Main / Large visual splits. Layouts must visibly differ.
    /// STABLE LTR: zone index i is always visual Pane i+1 (left-to-right / reading order).
 // / Main/Large only change star sizes '--'--'--'-'--'-'--'--'-'--'-'--'-'--'-'-'--'- never swap which slot sits where.
    /// 2 panes: Equal 50/50, Main ~70/30, Large ~85/15. Flip mirrors Main/Large large side.
    /// Pane click must NEVER call this (no Large reshape on assign-target click).
    /// </summary>
    private void ApplyPaneLayout()
    {
        UpdateWindowMinSizeForPanes();
        PaneGrid.Children.Clear();
        PaneGrid.RowDefinitions.Clear();
        PaneGrid.ColumnDefinitions.Clear();

        int n = _paneCount;
        if (n <= 0) return;

        if (n == 1)
        {
            PaneGrid.RowDefinitions.Add(new RowDefinition());
            PaneGrid.ColumnDefinitions.Add(new ColumnDefinition());
            PlaceSlot(0, 0, 0, 1, 1);
        }
        else if (n == 2)
        {
            PaneGrid.RowDefinitions.Add(new RowDefinition());
            double leftShare, rightShare;
            switch (_layout)
            {
                case "main":
                    if (_mainOnLeft) { leftShare = 0.70; rightShare = 0.30; }
                    else { leftShare = 0.30; rightShare = 0.70; }
                    break;
                case "large":
 // Large ~85/15 (not tied to ActivePane '--'--'--'-'--'-'--'--'-'--'-'--'-'--'-'-'--'- click must not reshape)
                    if (_mainOnLeft) { leftShare = 0.88; rightShare = 0.12; }
                    else { leftShare = 0.12; rightShare = 0.88; }
                    break;
                default:
                    leftShare = 0.50; rightShare = 0.50;
                    break;
            }
            // Main/Large: MinWidth=0 — thin-side floor applied in EnsureZonesDisjoint (no MinWidth overlap).
            // Equal keeps a soft floor so 50/50 never collapses.
            double colMin = string.Equals(_layout, "equal", StringComparison.OrdinalIgnoreCase) ? ZoneLayout.MinPaneWidth : 0;
            PaneGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(leftShare, GridUnitType.Star), MinWidth = colMin });
            PaneGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(rightShare, GridUnitType.Star), MinWidth = colMin });
            PlaceSlot(0, 0, 0, 1, 1);
            PlaceSlot(1, 0, 1, 1, 1);
        }
 else // n == 3 (max) '--'--'--'-'--'-'--'--'-'--'-'--'-'--'-'-'--'- stable LTR 1|2|3 with MinWidth on every column so pane 3 never collapses
        {
            PaneGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = ZoneLayout.MinPaneHeight });
            if (_layout == "large")
            {
                // Large center-stage 15|70|15. Thin sides use a soft MinWidth so WPF does not
                // inflate columns past PaneGrid and push pane 3 over the Docs/Windows sidebar.
                // Window MinWidth + ClipZonesToDeskClient still keep tiles usable and in-desk.
                // MinWidth MUST be 0. Prior 120/280 forced column sum past PaneGrid -> overlapping
                // screen rects covering all 3 panes + Docs/Windows sidebar.
                _dumpedLargeZonesOnce = false;
                PaneGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.12, GridUnitType.Star), MinWidth = 0 });
                PaneGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.76, GridUnitType.Star), MinWidth = 0 });
                PaneGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.12, GridUnitType.Star), MinWidth = 0 });
            }
            else if (_layout == "main")
            {
                // Main ~22|56|22. MinWidth=0 (same as Large) — side floor applied in EnsureZonesDisjoint /
                // ZoneLayout ratios, not WPF MinWidth (that overlapped zones into the sidebar).
                PaneGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.22, GridUnitType.Star), MinWidth = 0 });
                PaneGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.56, GridUnitType.Star), MinWidth = 0 });
                PaneGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.22, GridUnitType.Star), MinWidth = 0 });
            }
            else
            {
                PaneGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = ZoneLayout.MinPaneWidth });
                PaneGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = ZoneLayout.MinPaneWidth });
                PaneGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = ZoneLayout.MinPaneWidth });
            }
            PlaceSlot(0, 0, 0, 1, 1);
            PlaceSlot(1, 0, 1, 1, 1);
            PlaceSlot(2, 0, 2, 1, 1);
        }

        HighlightActivePane();
        RefreshAllThumbnails();
        SyncExpandUi();
        // Always deferred full re-tile of ALL assignments after layout change.
        Dispatcher.BeginInvoke(() =>
        {
            try { PaneGrid.UpdateLayout(); } catch { }
            SyncVisualPaneLabels();
            RefreshAllThumbnails();
            if (_liveDesk) ApplyLiveTiling();
            PostLayoutLiveHeal();
        }, DispatcherPriority.Loaded);
    }
    /// <summary>Nominal column shares for the current layout / pane count / flip (what ApplyPaneLayout sets).</summary>
    private double[] NominalPaneShares()
    {
        if (_paneCount == 2)
        {
            return _layout switch
            {
                "main" => _mainOnLeft ? new[] { 0.70, 0.30 } : new[] { 0.30, 0.70 },
                "large" => _mainOnLeft ? new[] { 0.88, 0.12 } : new[] { 0.12, 0.88 },
                _ => new[] { 0.5, 0.5 },
            };
        }
        if (_paneCount == 3)
        {
            return _layout switch
            {
                "main" => new[] { 0.22, 0.56, 0.22 },
                "large" => new[] { 0.12, 0.76, 0.12 },
                _ => new[] { 1 / 3.0, 1 / 3.0, 1 / 3.0 },
            };
        }
        return new[] { 1.0 };
    }

    private DateTime _lastNominalRestoreUtc = DateTime.MinValue;

    /// <summary>
    /// After a spill re-pass: if every assigned window now fits, no overflow min is pending, no app's
    /// real min-track needs more than its nominal share, yet the column stars drifted from the layout's
    /// nominal split, the drift was a transient (stale outer width at relink) -> re-apply the layout once.
    /// Rate-limited (one per 5 s) so a genuinely refusing app can never make this loop.
    /// </summary>
    private void MaybeRestoreNominalLayoutAfterTransientSpill()
    {
        try
        {
            if (_inSizeMove || _expandedMode || _paneCount < 2 || PaneGrid == null) return;
            if ((DateTime.UtcNow - _lastNominalRestoreUtc).TotalSeconds < 5) return;
            var cols = PaneGrid.ColumnDefinitions;
            if (cols.Count != _paneCount) return;
            for (int i = 0; i < _overflowMinWidth.Length; i++)
                if (_overflowMinWidth[i] > 0) return;
            if (!TryGetDeskClientScreenRect(out var desk)) return;
            var shares = NominalPaneShares();
            int usable = Math.Max(1, desk.Width - 4 * (_paneCount - 1));
            double sum = 0;
            for (int i = 0; i < cols.Count; i++) sum += Math.Max(0.0001, cols[i].Width.Value);
            // Expected widths = nominal shares with the Main/Large thin-side floor (center/main absorbs),
            // matching EnsureZonesDisjointInsideDesk, so a floored narrow desk is not seen as drift.
            var expW = new int[_paneCount];
            int mainIdx = 0, rest = 0;
            for (int i = 0; i < _paneCount; i++)
            {
                expW[i] = (int)Math.Round(usable * shares[i]);
                if (shares[i] > shares[mainIdx]) mainIdx = i;
            }
            if (_layout != "equal")
            {
                for (int i = 0; i < _paneCount; i++)
                    if (i != mainIdx) { expW[i] = Math.Max(expW[i], ZoneLayout.MinSideWidth); rest += expW[i]; }
                expW[mainIdx] = Math.Max(1, usable - rest);
            }
            bool drifted = false;
            for (int i = 0; i < _paneCount; i++)
            {
                double cur = Math.Max(0.0001, cols[i].Width.Value) / sum;
                if (Math.Abs(cur - (double)expW[i] / usable) > 0.02) drifted = true;
                int nominalW = expW[i];
                var w = _tiler.GetZone(i);
                if (w == null || w.Parked || w.PreviewProxy || !NativeMethods.IsWindow(w.Hwnd)) continue;
                NativeMethods.GetWindowMinTrackSizeRaw(w.Hwnd, out int rawMin, out _);
                if (rawMin + 8 > nominalW) return; // real need -> keep the bump
            }
            if (!drifted) return;
            _lastNominalRestoreUtc = DateTime.UtcNow;
            Dispatcher.BeginInvoke(() =>
            {
                try
                {
                    if (_inSizeMove || _expandedMode) return;
                    Array.Clear(_overflowMinWidth);
                    ApplyPaneLayout();
                }
                catch { }
            }, DispatcherPriority.Background);
        }
        catch { }
    }

    private void PlaceSlot(int index, int row, int col, int rowSpan, int colSpan)
    {
        var slot = _slots[index];
        Grid.SetRow(slot.Root, row);
        Grid.SetColumn(slot.Root, col);
        Grid.SetRowSpan(slot.Root, Math.Max(1, rowSpan));
        Grid.SetColumnSpan(slot.Root, Math.Max(1, colSpan));
        PaneGrid.Children.Add(slot.Root);
    }


    /// <summary>
    /// Visual Pane 1..N == zone index 0..N-1 (stable LTR). Returns identity order.
    /// Also runs a quick sanity check against screen X order when panes are measured.
    /// </summary>
    private int[] GetVisualZoneOrder()
    {
        int n = Math.Max(1, _paneCount);
        var identity = Enumerable.Range(0, n).ToArray();

        // Sanity: visual label n must match screen-ordered zone when layout is measured.
        try
        {
            var items = new System.Collections.Generic.List<(int idx, double x, double y)>();
            bool allMeasured = true;
            for (int i = 0; i < n; i++)
            {
                var root = _slots[i].Root;
                if (!(root.IsVisible && root.ActualWidth >= 2 && root.ActualHeight >= 2))
                {
                    allMeasured = false;
                    break;
                }
                var tl = root.PointToScreen(new Point(0, 0));
                items.Add((i, tl.X, tl.Y));
            }
            if (allMeasured && items.Count == n)
            {
                items.Sort((a, b) =>
                {
                    if (Math.Abs(a.x - b.x) < 2) return a.y.CompareTo(b.y);
                    return a.x.CompareTo(b.x);
                });
                for (int v = 0; v < n; v++)
                {
                    if (items[v].idx != v)
                    {
 // Should not happen with stable LTR placement '--'--'--'-'--'-'--'--'-'--'-'--'-'--'-'-'--'- log once-ish via status is too noisy;
                        // keep identity mapping (assign/link use zone == visual).
                        break;
                    }
                }
            }
        }
        catch { }

        return identity;
    }

    /// <summary>1-based visual pane number for an internal zone index (identity: zone+1).</summary>
    private int VisualPaneNumber(int zoneIndex)
    {
        if (zoneIndex < 0 || zoneIndex >= _paneCount) return zoneIndex + 1;
        return zoneIndex + 1;
    }

    /// <summary>Zone index from 1-based visual Pane label (identity mapping).</summary>
    private int ZoneIndexFromVisual(int visual1Based)
    {
        return Math.Clamp(visual1Based, 1, Math.Max(1, _paneCount)) - 1;
    }

    /// <summary>
    /// Refresh captions, pane buttons, and chip labels.
 // / Pane N button Tag = N = zone index N-1 + 1 '--'--'--'-'--'-'--'--'-'--'-'--'-'--'-'-'--'- same mapping as visible labels.
    /// </summary>
    private void SyncVisualPaneLabels()
    {
        var order = GetVisualZoneOrder();

        foreach (var w in _tiler.WorkingSet)
        {
            if (w.Parked) { w.DisplayPaneNumber = 0; continue; }
            if (w.ZoneIndex >= 0 && w.ZoneIndex < 4)
                w.DisplayPaneNumber = w.ZoneIndex + 1;
        }

                // Equal-width assign chips; arrows live ON each button (fixed glyph slots so sizes match).
        PaneButtons.Children.Clear();
        PaneButtons.ColumnDefinitions.Clear();
        PaneButtons.RowDefinitions.Clear();
        PaneButtons.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (int v = 0; v < _paneCount; v++)
        {
            PaneButtons.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(1, GridUnitType.Star)
            });
        }
        for (int v = 0; v < _paneCount; v++)
        {
            int zone = order[v];
            var btn = _paneBtns[zone];
            var assigned = _tiler.GetZone(zone);
            int paneNum = zone + 1;
            string tip = assigned == null
                ? $"Pane {paneNum} (empty) - click to Link Window; drag chip to swap; arrows move linked window"
                : $"Pane {paneNum}: {assigned.ShortTitle} - click; drag to swap; arrows move (unlinks target)";
            btn.ToolTip = tip;
            btn.Cursor = System.Windows.Input.Cursors.Hand;
            btn.Tag = paneNum;
            btn.Visibility = Visibility.Visible;
            bool active = paneNum == _activePane;
            bool hasLink = assigned != null && !assigned.Parked;
            btn.SetResourceReference(Control.BorderBrushProperty, active ? "AccentBrush" : "BorderBrushQuiet");
            btn.BorderThickness = new Thickness(active ? 2 : 1);
            if (active)
                btn.SetResourceReference(Control.BackgroundProperty, "AccentDimBrush");
            else
                btn.SetResourceReference(Control.BackgroundProperty, "PanelAltBrush");
            btn.SetResourceReference(Control.ForegroundProperty, active ? "AccentOnDimTextBrush" : "TextBrush");
            btn.FontWeight = FontWeights.Bold;

            if (btn.Parent is Panel oldHost)
                oldHost.Children.Remove(btn);

            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            bool canLeft = hasLink && v > 0;
            bool canRight = hasLink && v < _paneCount - 1;
            var leftTb = new TextBlock
            {
                Text = "\u25C0",
                Width = 14,
                FontSize = 10,
                FontWeight = FontWeights.Bold,
                TextAlignment = TextAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Opacity = canLeft ? 1 : 0.25,
                Tag = canLeft ? "move-left" : "pad"
            };
            leftTb.SetResourceReference(TextBlock.ForegroundProperty, canLeft ? (active ? "AccentOnDimTextBrush" : "AccentBrush") : "MutedTextBrush");
            var midTb = new TextBlock
            {
                Text = paneNum + " <drag>",
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(2, 0, 2, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Tag = "chip"
            };
            midTb.SetResourceReference(TextBlock.ForegroundProperty, active ? "AccentOnDimTextBrush" : "TextBrush");
            var rightTb = new TextBlock
            {
                Text = "\u25B6",
                Width = 14,
                FontSize = 10,
                FontWeight = FontWeights.Bold,
                TextAlignment = TextAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Opacity = canRight ? 1 : 0.25,
                Tag = canRight ? "move-right" : "pad"
            };
            rightTb.SetResourceReference(TextBlock.ForegroundProperty, canRight ? (active ? "AccentOnDimTextBrush" : "AccentBrush") : "MutedTextBrush");
            row.Children.Add(leftTb);
            row.Children.Add(midTb);
            row.Children.Add(rightTb);
            btn.Content = row;
            btn.Padding = new Thickness(2, 3, 2, 3);
            btn.Margin = new Thickness(0, 0, 4, 0);
            btn.MinWidth = 0;
            btn.HorizontalAlignment = HorizontalAlignment.Stretch;
            btn.HorizontalContentAlignment = HorizontalAlignment.Center;
            btn.PreviewMouseLeftButtonDown -= ChipArrow_PreviewMouseLeftButtonDown;
            btn.PreviewMouseLeftButtonDown += ChipArrow_PreviewMouseLeftButtonDown;

            Grid.SetColumn(btn, v);
            Grid.SetRow(btn, 0);
            PaneButtons.Children.Add(btn);
        }
        for (int i = _paneCount; i < _paneBtns.Length; i++)
        {
            if (_paneBtns[i] != null)
                _paneBtns[i].Visibility = Visibility.Collapsed;
        }

        for (int i = 0; i < _paneCount; i++)
        {
            int vn = i + 1;
            var assigned = _tiler.GetZone(i);
            bool active = vn == _activePane;
            if (assigned == null)
                _slots[i].Caption.Text = active ? $"Pane {vn}  -  Link Window" : $"Pane {vn}";
            else
                _slots[i].Caption.Text = active
                    ? $"Pane {vn}  -  Link Window  -  {assigned.ShortTitle}"
                    : $"Pane {vn} - {assigned.ShortTitle}";

            if (_slots[i].MoveLeftBtn != null) _slots[i].MoveLeftBtn.Visibility = Visibility.Collapsed;
            if (_slots[i].MoveRightBtn != null) _slots[i].MoveRightBtn.Visibility = Visibility.Collapsed;
        }
        for (int i = _paneCount; i < _slots.Length; i++)
        {
            if (_slots[i]?.MoveLeftBtn != null) _slots[i].MoveLeftBtn.Visibility = Visibility.Collapsed;
            if (_slots[i]?.MoveRightBtn != null) _slots[i].MoveRightBtn.Visibility = Visibility.Collapsed;
        }
    }
    /// <summary>
    /// Preview down on pane chrome: mark handled so WPF focus stays off desk chrome when possible.
    /// Actual ActivePane + HWND focus still happens on MouseLeftButtonUp.
    /// </summary>
    private void Pane_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d)
        {
            var btn = FindAncestor<Button>(d);
            if (btn != null && btn.Tag is int) return;
            if (FindAncestor<Canvas>(d) is Canvas c && c.Visibility == Visibility.Visible && _cropModeZone >= 0)
                return;
            if (FindAncestor<ContextMenu>(d) != null) return;
        }
        if (sender is Border brPreview && brPreview.Tag is int ziPreview && IsCropInteractionZone(ziPreview))
            return;
        // Do not move keyboard focus onto desk chrome (Expand button still works).
 // Do NOT set e.Handled here '--'--'--'-'--'-'--'--'-'--'-'--'-'--'-'-'--'- that can suppress MouseLeftButtonUp / Pane_Click.
        try { Keyboard.ClearFocus(); } catch { }
    }

    private void PaneBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressPaneBtnClick) { _suppressPaneBtnClick = false; return; }
        if (sender is Button b && b.Tag is int n)
        {
            // ActivePane + Focus target + immediately re-show/re-place/re-TOPMOST ALL Live tiles.
            _activePane = n;
            HighlightActivePane();
            ShowLinkTargetOutline(ZoneIndexFromVisual(n));
            Persist();
            SyncSideCropButtons();

            int zone = ZoneIndexFromVisual(n);
            var assigned = _tiler.GetZone(zone);
            IntPtr focusHwnd = assigned?.Hwnd ?? IntPtr.Zero;
            if (assigned != null && assigned.Collapsed) focusHwnd = IntPtr.Zero; // never un-minimize via focus
            _paneClickQuietUntilUtc = DateTime.UtcNow.AddMilliseconds(900);
            LogPaneClickDebug("PaneBtn", focusHwnd);

            if (assigned != null && CropPreviewEnabled && (assigned.PreviewProxy || assigned.HasCrop))
            {
                PreviewThumb_ClickFocus(zone);
            }
            else if (_liveDesk)
            {
                // Batch ALL tiles, Focus(target), Assert ALL again so siblings never stay under desk.
                AssertAllAssignedLiveTiles(keepFocusHwnd: focusHwnd, focus: focusHwnd != IntPtr.Zero);
            }
            else if (focusHwnd != IntPtr.Zero)
            {
                NativeMethods.FocusWindow(focusHwnd);
            }

            int vnBtn = VisualPaneNumber(_activePane - 1);
            StatusText.Text = assigned != null
                ? $"Link Window: Pane {vnBtn} - focused {assigned.ShortTitle}"
                : $"Link Window: Pane {vnBtn}";
        }
    }

    /// <summary>
    /// Double-click another Assign-target pane chip: move/swap the previously selected pane's assignment.
    /// </summary>
    private void PaneBtn_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Button b || b.Tag is not int n) return;
        if (e.ClickCount >= 2)
        {
            int srcVisual = _activePaneBeforeBtnClick;
            int dstVisual = n;
            if (srcVisual != dstVisual)
            {
                MoveOrSwapPaneAssignment(ZoneIndexFromVisual(srcVisual), ZoneIndexFromVisual(dstVisual));
                _suppressPaneBtnClick = true;
                e.Handled = true;
            }
        }
        else
        {
            _activePaneBeforeBtnClick = _activePane;
            _paneBtnDragVisual = n;
            _paneBtnDragStart = e.GetPosition(null);
        }
    }

    private void PaneBtn_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_paneBtnDragVisual < 0 || e.LeftButton != MouseButtonState.Pressed) return;
        var pos = e.GetPosition(null);
        if (Math.Abs(pos.X - _paneBtnDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y - _paneBtnDragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;
        int visual = _paneBtnDragVisual;
        _paneBtnDragVisual = -1;
        int zone = ZoneIndexFromVisual(visual);
        var w = _tiler.GetZone(zone);
        if (w == null || w.Parked) return;
        BeginDocsFileDragHitThrough();
        try
        {
            DragDrop.DoDragDrop(sender as DependencyObject ?? this, w, DragDropEffects.Move);
        }
        catch { }
        finally
        {
            EndDocsFileDragHitThrough();
        }
    }

    private void PaneBtn_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _paneBtnDragVisual = -1;
    }

    private void PaneBtn_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(typeof(WindowEntry)) || e.Data.GetDataPresent(typeof(WorkingWindow)))
        {
            e.Effects = DragDropEffects.Move;
            if (sender is Button btn)
                btn.BorderBrush = (Brush)FindResource("AccentBrush");
        }
        else if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            if (sender is Button btn2)
                btn2.BorderBrush = (Brush)FindResource("AccentBrush");
        }
        else e.Effects = DragDropEffects.None;
        e.Handled = true;
    }

    private void PaneBtn_DragLeave(object sender, DragEventArgs e)
    {
        HighlightActivePane();
    }

    private void PaneBtn_Drop(object sender, DragEventArgs e)
    {
        HighlightActivePane();
        // File drops: pane surface only (chip Drop was a second launch path ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¾Ãƒâ€šÃ‚Â¢ stacked WMP windows).
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Handled = true;
            return;
        }
        if (sender is not Button btn || btn.Tag is not int visual) return;
        int index = ZoneIndexFromVisual(visual);
        if (index < 0 || index >= _paneCount) return;
        DropAssignOntoZone(e, index);
        e.Handled = true;
    }

    private void Pane_Click(object sender, MouseButtonEventArgs e)
    {
        if (_suppressPaneClick) { _suppressPaneClick = false; return; }
        if (e.OriginalSource is DependencyObject d)
        {
            var btn = FindAncestor<Button>(d);
            if (btn != null && btn.Tag is int) return;
            if (FindAncestor<Canvas>(d) is Canvas c && c.Visibility == Visibility.Visible
                && _cropModeZone >= 0) return;
            if (FindAncestor<ContextMenu>(d) != null) return;
            if (FindAncestor<MenuItem>(d) != null) return;
        }

        if (sender is Border border && border.Tag is int index)
        {
            if (IsCropInteractionZone(index)) return;
            // ActivePane + Focus ONCE. Never hide siblings. One batch z-order raise or none.
            e.Handled = true;
            _activePane = index + 1;
            HighlightActivePane();
            ShowLinkTargetOutline(index);
            Persist();
            SyncSideCropButtons();

            var assigned = _tiler.GetZone(index);
            IntPtr focusHwnd = assigned?.Hwnd ?? IntPtr.Zero;
            if (assigned != null && assigned.Collapsed) focusHwnd = IntPtr.Zero; // card click expands instead
            _paneClickQuietUntilUtc = DateTime.UtcNow.AddMilliseconds(900);
            LogPaneClickDebug("Pane_Click", focusHwnd);

            if (assigned != null && CropPreviewEnabled && (assigned.PreviewProxy || assigned.HasCrop))
            {
                PreviewThumb_ClickFocus(index);
                int vnPrev = VisualPaneNumber(index);
                StatusText.Text = $"Focused {assigned.ShortTitle}";

            }
            else if (_liveDesk)
            {
                // Batch: re-show/re-place/re-TOPMOST ALL -> Focus(target) -> Assert ALL again.
                AssertAllAssignedLiveTiles(keepFocusHwnd: focusHwnd, focus: focusHwnd != IntPtr.Zero);
                int vnClick = VisualPaneNumber(index);
                StatusText.Text = assigned != null
                    ? $"Focused {assigned.ShortTitle}"
                    : $"Active pane: {vnClick} (empty)";
            }
            else
            {
                int vnClick2 = VisualPaneNumber(index);
                if (focusHwnd != IntPtr.Zero)
                {
                    NativeMethods.FocusWindow(focusHwnd);
                    StatusText.Text = $"Focused {assigned!.ShortTitle}";
                }
                else
                {
                    StatusText.Text = $"Active pane: {vnClick2} (empty)";
                }
                EnsureCroppedHwndsParked();
            }
        }
    }

    private string FitModeForZone(int zone)
    {
        if (zone >= 0 && zone < _paneFitMode.Length && !string.IsNullOrEmpty(_paneFitMode[zone]))
            return _paneFitMode[zone]!;
        return _fitMode;
    }

    private void PaneMenuBtn_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not Button btn || btn.Tag is not int zone) return;
        OpenPaneMenuWindow(zone);
    }

    private void RefreshPaneMenuState(int zone, ContextMenu menu)
    {
        var w = zone < _paneCount ? _tiler.GetZone(zone) : null;
        bool assigned = w != null && !w.Parked && NativeMethods.IsWindow(w.Hwnd);
        foreach (var obj in menu.Items)
        {
            if (obj is not MenuItem mi || mi.Header is not string h) continue;
            if (h == "Crop")
                mi.IsEnabled = CropPreviewEnabled && assigned && _cropModeZone != zone;
            else if (h == "Uncrop")
                mi.IsEnabled = CropPreviewEnabled && assigned && w!.HasCrop;
            else if (h is "Collapse window" or "Expand window")
                continue; // SyncCollapseMenuItem owns this item
            else if (h is "Fill" or "Fit" or "Unlink" or "Expand")
                mi.IsEnabled = assigned || h == "Expand";
            else if (h.StartsWith("Float", StringComparison.Ordinal))
                mi.IsEnabled = false;
        }
    }

    private void PaneMenuCrop_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not MenuItem mi || mi.Tag is not int zone) return;
        BeginCropMode(zone);
    }

    private void PaneMenuUncrop_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not MenuItem mi || mi.Tag is not int zone) return;
        UncropPane(zone);
    }

    private void PaneMenuFill_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not MenuItem mi || mi.Tag is not int zone) return;
        _paneFitMode[zone] = "fill";
        _activePane = zone + 1;
        HighlightActivePane();
        if (_liveDesk) ApplyLiveTiling(activateZone: zone);
        else RefreshAllThumbnails();
        StatusText.Text = $"Pane {zone + 1}: Fill";
    }

    private void PaneMenuFit_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not MenuItem mi || mi.Tag is not int zone) return;
        _paneFitMode[zone] = "fit";
        _activePane = zone + 1;
        HighlightActivePane();
        if (_liveDesk) ApplyLiveTiling(activateZone: zone);
        else RefreshAllThumbnails();
        StatusText.Text = $"Pane {zone + 1}: Fit";
    }

    private void PaneMenuUnlink_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not MenuItem mi || mi.Tag is not int zone) return;
        var item = _tiler.GetZone(zone);
        if (item == null)
        {
            StatusText.Text = $"Pane {zone + 1} empty";

            return;
        }
        UnlinkWorking(item);
    }

    private void PaneMenuExpand_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not MenuItem mi || mi.Tag is not int zone) return;
        _activePane = zone + 1;
        ExpandActivePane();
    }

    private void PaneExpandBtn_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is Button b && b.Tag is int index)
        {
            _activePane = index + 1;
            ExpandActivePane();
        }
    }

    private static T? FindAncestor<T>(DependencyObject? start) where T : DependencyObject
    {
        while (start != null)
        {
            if (start is T t) return t;
            start = VisualTreeHelper.GetParent(start);
        }
        return null;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _ownHwnd = new WindowInteropHelper(this).Handle;
        ApplyUiZoom(retile: false);
        ApplySidePanelState();
        SyncToggleUi();
        ApplyPaneLayout();
        SyncVisualPaneLabels();
        if (!string.IsNullOrEmpty(_sizePreset))
            ApplyDashboardSizePreset(_sizePreset);
        if (_lockHeight)
            ApplyLockHeight(fromToggle: false);
        RefreshPaneButtons();
        try { EnsureWmpFitPrefs(); } catch { }
        try { ApplyAccentColor(_accentHex, persist: false); } catch { }
            try { ApplyBackgroundColor(_backgroundHex, persist: false); } catch { }
        RefreshWindowList();
        try { SetDocsMode(false); } catch { try { EnsureDocsStaging(); RefreshDocsList(); } catch { } }
        try { _media.Refresh(); SyncMediaPaneLabels(); } catch { }

        DisableCropPipelineForStableLiveFill();

        _refreshTimer.Start();
        _mediaTimer.Start();
        _snipWatchTimer?.Start();

        bool hk = _hotkeys.Register(this, OnHotkeyMute, OnHotkeyFocus, OnHotkeyDeskFocus, OnHotkeySkipAd);
        SyncLiveUi();
        // v1.0.1: BtnMediaSkipAd reads "Auto-skip ads" (XAML); F8 (F9 fallback) stays the manual skip hotkey and
        // is named in its tooltip. InitSkipAdFeature starts the ready watcher / auto-skip.
        InitSkipAdFeature();
        InitMediaPreview();
        string skipLabel = string.IsNullOrEmpty(_hotkeys.SkipAdHotkeyLabel) ? "" : $", {_hotkeys.SkipAdHotkeyLabel} skip ad";
        StatusText.Text = hk
            ? (_liveDesk
                ? $"Ready - Live desk on. Assign apps to tile real windows. Ctrl+Shift+M/F media, Ctrl+Shift+D focus desk{skipLabel}"
                : $"Ready - Preview mode. Toggle Live to tile real windows. Ctrl+Shift+M/F media, Ctrl+Shift+D focus desk{skipLabel}")
            : "Ready (hotkeys unavailable)";

        // v1.0.2: re-center after size preset / zoom ran (exact physical-pixel placement on the primary).
        PlaceCenteredOnPrimary("Loaded");
        ScheduleLaunchRecenter();
        // Launch z-order pass 1: show desk above ordinary windows, then tile + TOPMOST linked.
        BringDashboardToFrontOnLaunch();
        // One-time: drop leftover TOPMOST on common browsers that are not Live-assigned.
        SweepUnassignedBrowserTopmostOnce();
    }

    private void OnContentRendered(object? sender, EventArgs e)
    {
        _suppressAutoWindowPreviewUntilUtc = DateTime.UtcNow.AddSeconds(8);
        try { SeedAutoPreviewSeenHwnds(); } catch { }
        // Launch z-order pass 2 after first paint (Loaded alone can lose to other apps).
        BringDashboardToFrontOnLaunch();
    }

    /// <summary>
    /// Startup foreground: WPF Topmost=true while restored (not minimized), plus Activate /
    /// ForceForeground on Loaded, ContentRendered, and delayed passes (150/500ms).
    /// Linked Live tiles stay HWND_TOPMOST above the desk. Single-instance activation also Topmosts.
    /// </summary>
    private void BringDashboardToFrontOnLaunch()
    {
        if (_ownHwnd == IntPtr.Zero)
            _ownHwnd = new WindowInteropHelper(this).Handle;
        if (_ownHwnd == IntPtr.Zero) return;

        try
        {
            if (WindowState == WindowState.Minimized)
                WindowState = WindowState.Normal;
 // Keep desk Topmost while restored '--'--'- do not keep desk TOPMOST (Live blink).
            try { Topmost = true; } catch { }
            Show();
            Activate();
        }
        catch { }

        ApplyLaunchForegroundPass("sync");

        Dispatcher.BeginInvoke(() =>
        {
            ApplyLaunchForegroundPass("idle");
        }, DispatcherPriority.ApplicationIdle);

 // Other apps often steal FG right after first paint '--'--'--'-'--'-'--'--'-'--'-'--'-'--'-'-'--'- delayed passes win reliably.
        ScheduleLaunchForegroundPass(150, "t150");
        ScheduleLaunchForegroundPass(500, "t500");
    }

    private void ScheduleLaunchForegroundPass(int delayMs, string tag)
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(delayMs) };
        t.Tick += (_, _) =>
        {
            t.Stop();
            ApplyLaunchForegroundPass(tag);
        };
        t.Start();
    }

    private void ApplyLaunchForegroundPass(string tag)
    {
        try
        {
            if (_ownHwnd == IntPtr.Zero)
                _ownHwnd = new WindowInteropHelper(this).Handle;
            if (_ownHwnd == IntPtr.Zero) return;
            if (WindowState == WindowState.Minimized) return;

            try { Topmost = true; } catch { }
            try { Activate(); } catch { }
            bool ok = NativeMethods.ForceForegroundWindow(_ownHwnd);
            // Keep desk TOPMOST while restored; Live tiles stay HWND_TOPMOST above desk.
            try { if (WindowState != WindowState.Minimized) Topmost = true; } catch { }
            RaiseDashboardAboveUnlinked();
            try { Activate(); } catch { }

            if (_liveDesk)
            {
                ApplyLiveTiling();
                RepairAllTilesZOrderAboveDashboard(keepFocusHwnd: IntPtr.Zero);
            }

            try
            {
                var dir = Path.GetDirectoryName(DebugLogPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                var fg = NativeMethods.GetForegroundWindow();
                File.AppendAllText(DebugLogPath,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] launch-fg tag={tag} ok={ok} technique={NativeMethods.LastForegroundTechnique} topmost={Topmost} own=0x{_ownHwnd.ToInt64():X} fg=0x{fg.ToInt64():X} match={fg == _ownHwnd}{Environment.NewLine}");
            }
            catch { }
        }
        catch { }
    }
    private void OnClosed(object? sender, EventArgs e)
    {
        StopSkipAdFeature();
        ShutdownMediaPreview();
        Persist();
        _refreshTimer.Stop();
        _mediaTimer.Stop();
        _snipWatchTimer?.Stop();
        EndCropMode(apply: false);
        CloseAllPaneChromeOverlays();
        CloseShowUiOverlay();
        _hotkeys.Dispose();
        _media.Dispose();
        foreach (var s in _slots)
            s.Thumb.Clear();
        _tiler.RestoreAllAndClear();
    }

    private void Persist()
    {
        _settings.PaneCount = _paneCount;
        _settings.Layout = _layout;
        _settings.ActivePane = _activePane;
        _settings.SidePanelCollapsed = _sideCollapsed;
        _settings.LiveDesk = _liveDesk;
        _settings.MainOnLeft = _mainOnLeft;
        _settings.Zoom = _uiZoom;
        _settings.SizePreset = _sizePreset ?? "";
        _settings.FitMode = _fitMode;
        _settings.AccentHex = _accentHex;
        _settings.AccentDefaultHex = _accentDefaultHex;
        _settings.BackgroundHex = _backgroundHex;
        _settings.BackgroundDefaultHex = _backgroundDefaultHex;
        _settings.LockHeight = false; // never restore Lock H on next launch
        _settings.PreviewCrops ??= new Dictionary<string, CropRectDto>(StringComparer.OrdinalIgnoreCase);
        SettingsStore.Save(_settings);
    }

    private void SyncToggleUi()
    {
        TbPane1.IsChecked = _paneCount == 1;
        TbPane2.IsChecked = _paneCount == 2;
        TbPane3.IsChecked = _paneCount == 3;
        TbLayoutEqual.IsChecked = _layout == "equal";
        TbLayoutMain.IsChecked = _layout == "main";
        TbLayoutLarge.IsChecked = _layout == "large";
        if (TbLiveDesk != null)
            TbLiveDesk.IsChecked = _liveDesk;
        if (TbFlip != null)
            TbFlip.IsChecked = !_mainOnLeft;
        SyncSizeUi();
        SyncLiveUi();
        SyncExpandUi();
    }

    private void SyncExpandUi()
    {
        BtnBackLayout.IsEnabled = _hasLayoutRestore;
        BtnExpandPane.Content = _expandedMode ? "Expanded" : "Expand";
        BtnExpandPane.IsEnabled = !_expandedMode || _paneCount > 1;
    }

    private void ApplySidePanelState()
    {
        UpdateWindowMinSizeForPanes();
        if (_distractionFree)
        {
            SidePanel.Visibility = Visibility.Collapsed;
            SidePanel.Width = 0;
            SidePanel.Margin = new Thickness(0);
            return;
        }
        if (_sideCollapsed)
        {
            SidePanel.Visibility = Visibility.Collapsed;
            SidePanel.Width = 0;
            SidePanel.Margin = new Thickness(0);
            BtnToggleSide.Content = "Windows";
            BtnToggleSide.ToolTip = "Expand Windows + media side panel";
        }
        else
        {
            SidePanel.Visibility = Visibility.Visible;
            SidePanel.Width = 300;
            SidePanel.Margin = new Thickness(10, 0, 0, 0);
            BtnToggleSide.Content = "Collapse";
            BtnToggleSide.ToolTip = "Collapse Windows + media side panel";
        }
        // Refresh thumbs + live tiles after layout shift
        Dispatcher.BeginInvoke(() =>
        {
            RefreshAllThumbnails();
            ApplyLiveTiling();
        }, DispatcherPriority.Loaded);
    }

    private void BtnToggleSide_Click(object sender, RoutedEventArgs e)
    {
        if (_distractionFree) return;
        _sideCollapsed = !_sideCollapsed;
        ApplySidePanelState();
        Persist();
        StatusText.Text = _sideCollapsed ? "Side panel collapsed" : "Side panel expanded";
    }

    private void BtnFocusDesk_Click(object sender, RoutedEventArgs e) => SetDistractionFree(!_distractionFree);

    private void BtnShowUi_Click(object sender, RoutedEventArgs e) => SetDistractionFree(false);

    private void OnHotkeyDeskFocus()
    {
        Dispatcher.Invoke(() =>
        {
            try { SetDistractionFree(!_distractionFree); }
            catch { StatusText.Text = "Full screen hotkey failed."; }
        });
    }

    /// <summary>
    /// Full-screen desk: hide header + bottom chrome + side panel.
 // / Live tiles stay tiled. Not persisted '- always off on launch.
    /// Exit via Esc, Ctrl+Shift+D, or Show UI (Topmost overlay when Live).
    /// </summary>
    private void SetDistractionFree(bool on)
    {
        if (_distractionFree == on) return;
        _distractionFree = on;

        if (on)
        {
            HeaderChrome.Visibility = Visibility.Collapsed;
            BottomChrome.Visibility = Visibility.Collapsed;
            SidePanel.Visibility = Visibility.Collapsed;
            SidePanel.Width = 0;
            SidePanel.Margin = new Thickness(0);
            if (DeskCaptionRow != null)
                DeskCaptionRow.Visibility = Visibility.Collapsed;
            RootDock.Margin = new Thickness(4);
            if (DeskChrome != null)
            {
                DeskChrome.Padding = new Thickness(2);
                DeskChrome.BorderThickness = new Thickness(0);
            }
            // In-window button stays collapsed under Live tiles; Topmost overlay is the real exit affordance.
            BtnShowUi.Visibility = Visibility.Collapsed;
            if (BtnFocusDesk != null) BtnFocusDesk.Content = "Full screen";
            StatusText.Text = "Full screen";

            SyncShowUiOverlay();
        }
        else
        {
            HeaderChrome.Visibility = Visibility.Visible;
            BottomChrome.Visibility = Visibility.Visible;
            if (DeskCaptionRow != null)
                DeskCaptionRow.Visibility = Visibility.Collapsed;
            RootDock.Margin = new Thickness(10);
            if (DeskChrome != null)
            {
                DeskChrome.Padding = new Thickness(6);
                DeskChrome.BorderThickness = new Thickness(1);
            }
            BtnShowUi.Visibility = Visibility.Collapsed;
            HideShowUiOverlay();
            if (BtnFocusDesk != null) BtnFocusDesk.Content = "Full screen";
            ApplySidePanelState();
            StatusText.Text = "UI restored";
        }

        UpdateWindowMinSizeForPanes();
        Dispatcher.BeginInvoke(() =>
        {
            try
            {
                UpdateLayout();
                PaneGrid?.UpdateLayout();
            }
            catch { }
            RefreshAllThumbnails();
            if (_liveDesk) ApplyLiveTiling();
            SyncShowUiOverlay();
        }, DispatcherPriority.Loaded);
    }
    private void PaneCount_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && int.TryParse(fe.Tag?.ToString(), out int n))
        {
            // Manual pane change clears expanded restore if user deliberately picks count
            if (_expandedMode)
                _expandedMode = false;

            _paneCount = Math.Clamp(n, 1, 3);
            if (_activePane > _paneCount) _activePane = _paneCount;
            _tiler.TrimZones(_paneCount, restoreExtras: true);
            SyncToggleUi();
            ApplyPaneLayout();
            RefreshPaneButtons();
            Persist();
            StatusText.Text = $"{_paneCount} panes";
        }
    }

    private void Layout_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is string tag)
        {
            if (_expandedMode)
                _expandedMode = false;

            _layout = tag;
            Array.Clear(_overflowMinWidth);
            SyncToggleUi();
            ApplyPaneLayout();
            Persist();
            StatusText.Text = $"Layout: {_layout} - re-tiling all assigned panes";
        }
    }

    /// <summary>
    /// Expand active pane to a one-pane desk view; remember prior layout for Back.
    /// Also focuses the real HWND if assigned.
    /// </summary>
    private void Flip_Click(object sender, RoutedEventArgs e)
    {
        _mainOnLeft = !_mainOnLeft;
        if (TbFlip != null)
            TbFlip.IsChecked = !_mainOnLeft;
        ApplyPaneLayout();
        Persist();
        StatusText.Text = _mainOnLeft
            ? "Flip: main/large large pane on the left"
            : "Flip: main/large large pane on the right";
    }
    private void FitMode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is string tag && (tag == "fit" || tag == "fill"))
        {
            _fitMode = tag;
        Array.Clear(_paneFitMode);
            SyncToggleUi();
            Persist();
            if (_liveDesk) ApplyLiveTiling();
            StatusText.Text = _fitMode == "fit"
                ? "Tile Fit: window sized to fit pane (centered) — not DPI magnify"
                : "Tile Fill: stretch linked window to full pane";
        }
    }
    private void ExpandActivePane()
    {
        if (!_expandedMode)
        {
            _savedPaneCount = _paneCount;
            _savedLayout = _layout;
            _savedActivePane = _activePane;
            _hasLayoutRestore = true;
            _expandedFromIndex = Math.Clamp(_activePane, 1, 4) - 1;
            if (_expandedFromIndex != 0)
                _tiler.SwapZones(0, _expandedFromIndex);
        }

        _expandedMode = true;
        _paneCount = 1;
        _layout = "large";
        _activePane = 1;
        // Do NOT TrimZones - other pane assignments stay for Back to layout.

        SyncToggleUi();
        ApplyPaneLayout();
        RefreshPaneButtons();
        Persist();

        var z = _tiler.GetZone(0);
        if (z != null)
        {
            // Assert: ShowAndRaise ALL, Focus(target), ShowAndRaise ALL again (forced TOPMOST unbury).
            if (_liveDesk)
                AssertAllAssignedLiveTiles(keepFocusHwnd: z.Hwnd, focus: true);
            else
                NativeMethods.FocusWindow(z.Hwnd);
            StatusText.Text = $"Expanded + focused {z.ShortTitle} - Back to layout restores multi-pane";
        }
        else
            StatusText.Text = "Expanded to one pane - Back to layout restores prior layout";
    }

    private void BtnExpandPane_Click(object sender, RoutedEventArgs e)
    {
        if (_thinExpandHintZone >= 0 && _thinExpandHintZone < _paneCount)
            _activePane = _thinExpandHintZone + 1;
        ExpandActivePane();
    }

    private void BtnBackLayout_Click(object sender, RoutedEventArgs e)
    {
        if (!_hasLayoutRestore)
        {
            StatusText.Text = "No prior layout to restore.";
            return;
        }

        // Undo Expand zone promotion before restoring pane count
        if (_expandedMode && _expandedFromIndex != 0)
            _tiler.SwapZones(0, _expandedFromIndex);

        _expandedMode = false;
        _paneCount = Math.Clamp(_savedPaneCount, 1, 3);
        _layout = _savedLayout is "equal" or "main" or "large" ? _savedLayout : (string.Equals(_savedLayout, "focus", StringComparison.OrdinalIgnoreCase) ? "large" : "equal");
        _activePane = Math.Clamp(_savedActivePane, 1, _paneCount);
        SyncToggleUi();
        ApplyPaneLayout();
        RefreshPaneButtons();
        Persist();
        if (_liveDesk) ApplyLiveTiling();
        StatusText.Text = $"Restored {_paneCount} panes - {_layout}";
    }

    private void HighlightActivePane()
    {
        var accent = (Brush)FindResource("AccentBrush");
        var quiet = (Brush)FindResource("BorderBrushQuiet");
        for (int i = 0; i < 4; i++)
        {
            bool active = (i + 1) == _activePane && i < _paneCount;
            // No assign-target outline on Live panes (distracting). Active shown on sidebar chips only.
            // Resource reference (not a snapshot) so Bg/Accent swaps always recolor pane edges.
            _slots[i].Root.SetResourceReference(Border.BorderBrushProperty, "BorderBrushQuiet");
            _slots[i].Root.BorderThickness = new Thickness(1);
            var assigned = i < _paneCount ? _tiler.GetZone(i) : null;
            bool liveCovered = _liveDesk && assigned != null && !assigned.Parked && !assigned.PreviewProxy
                && NativeMethods.IsWindow(assigned.Hwnd) && !NativeMethods.IsIconic(assigned.Hwnd);
            // Live covered panes stay transparent so the desk never paints black over real HWNDs.
            if (liveCovered)
                _slots[i].Root.Background = Brushes.Transparent;
            else
                _slots[i].Root.SetResourceReference(Border.BackgroundProperty, "PanelBrush");
            _slots[i].Caption.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
 // Expand lives in the compact pane menu '--'-'--'-'-'--'--'- keep legacy button collapsed.
            _slots[i].ExpandBtn.Visibility = Visibility.Collapsed;
        }
        RefreshPaneButtons();
        SyncVisualPaneLabels();
        SyncExpandUi();
        SyncSideCropButtons();
        ApplyLinkTargetOutlineVisual();
    }

    private void RefreshPaneButtons() => SyncVisualPaneLabels();

    private void RefreshAllThumbnails()
    {
        for (int i = 0; i < 4; i++)
        {
            var slot = _slots[i];
            if (i >= _paneCount)
            {
                HideCollapseCard(i);
                slot.Thumb.Clear();
                slot.Hint.Visibility = Visibility.Collapsed;
                slot.ExpandBtn.Visibility = Visibility.Collapsed;
                if (slot.MenuBtn != null) slot.MenuBtn.Visibility = Visibility.Collapsed;
                ShowFitPromptUi(i, false);
                UpdateCropButtons(i);
                if (slot.CropOverlay != null)
                    slot.CropOverlay.Visibility = Visibility.Collapsed;
                continue;
            }

            var assigned = _tiler.GetZone(i);
            if (assigned == null || assigned.Parked || !NativeMethods.IsWindow(assigned.Hwnd))
            {
                HideCollapseCard(i);
                slot.Thumb.Clear();
                slot.Placeholder.Visibility = Visibility.Visible;
                // Empty pane: theme-derived soft gradient (brush only; no margin/border/effect on the host).
                if (slot.ContentGrid != null)
                    slot.ContentGrid.SetResourceReference(Panel.BackgroundProperty, "PaneGradientBrush");
                slot.Icon.Source = null;
                slot.Title.Text = "Empty - Link Window";
                int vnEmpty = VisualPaneNumber(i);
                slot.Caption.Text = (i + 1) == _activePane ? $"Pane {vnEmpty}  -  Link Window" : $"Pane {vnEmpty}";
                slot.Hint.Visibility = Visibility.Collapsed;
                slot.ExpandBtn.Visibility = Visibility.Collapsed;
                ShowFitPromptUi(i, false);
                UpdateCropButtons(i);
                continue;
            }

            // v1.0.1: Collapsed / Floating panes render the compact card instead.
            if (RefreshCollapsedPaneUi(i, assigned))
            {
                int vnC = VisualPaneNumber(i);
                slot.Caption.Text = $"Pane {vnC} - {assigned.ShortTitle} (collapsed)";
                slot.ExpandBtn.Visibility = Visibility.Collapsed;
                UpdateCropButtons(i);
                if (slot.CropOverlay != null)
                {
                    slot.CropOverlay.Visibility = Visibility.Collapsed;
                    slot.CropOverlay.IsHitTestVisible = false;
                }
                continue;
            }

            bool minimized = NativeMethods.IsIconic(assigned.Hwnd);
            bool previewProxy = assigned.PreviewProxy;
            bool cropOverlay = CropPreviewEnabled && (assigned.HasCrop || _cropModeZone == i);
            bool showThumb = !_liveDesk || minimized || previewProxy || cropOverlay;

            int vn = VisualPaneNumber(i);
            slot.Caption.Text = (i + 1) == _activePane
                ? $"Pane {vn}  -  Link Window  -  {assigned.ShortTitle}"
                : $"Pane {vn} - {assigned.ShortTitle}";
            slot.Icon.Source = assigned.Icon;
            slot.Title.Text = _liveDesk && !minimized && !previewProxy && !cropOverlay
                ? $"{assigned.ShortTitle}\n(Live - real window)"
                : assigned.ShortTitle;
            if (assigned.HasCrop)
                slot.Hint.Text = "Cropped fill --------------'---------------------------------------------------------------------------------------------'------------------------------------------------------------------------------------'---------------------------------------------------------------------------'----------------------------------------------------------------------------------------------------------------------------------------------'------------------------------------------------------------------------------------'---------------------------------------------------------------------------'----------------------------------------------------------------------------------------------------------------------------------------'---------------------------------------------------------------------- Uncrop to restore; click to Focus";
            else if (previewProxy)
                slot.Hint.Text = "Preview --------------'---------------------------------------------------------------------------------------------'------------------------------------------------------------------------------------'---------------------------------------------------------------------------'----------------------------------------------------------------------------------------------------------------------------------------------'------------------------------------------------------------------------------------'---------------------------------------------------------------------------'----------------------------------------------------------------------------------------------------------------------------------------'---------------------------------------------------------------------- fitted thumb; click to Focus";
            else
                slot.Hint.Text = _liveDesk
                    ? (minimized ? "Minimized - thumbnail preview" : "Live - interact with the real window")
                    : "Preview only - Focus to interact";
            slot.ExpandBtn.Visibility = Visibility.Collapsed;

            if (_liveDesk && !minimized && !previewProxy && !cropOverlay)
            {
                // Live Fill: no DWM thumbnails over the real window.
                slot.Thumb.Clear();
                slot.Placeholder.Visibility = Visibility.Collapsed;
                slot.Hint.Visibility = Visibility.Collapsed;
                if (slot.ContentGrid != null)
                    slot.ContentGrid.Background = Brushes.Transparent;
                slot.Root.Background = Brushes.Transparent;
                ShowFitPromptUi(i, false);
            }
            else if (showThumb)
            {
                if (slot.ContentGrid != null)
                    slot.ContentGrid.Background = Brushes.Black;

                // Apply crop / fill mode before/after assign.
                bool hasCrop = assigned.HasCrop;
                // PreviewProxy + global Fill: cover the pane (no letterbox bars).
                bool fillThumb = hasCrop || (previewProxy && _fitMode == "fill");
                slot.Thumb.FillHost = fillThumb;
                slot.Thumb.SourceCrop = hasCrop ? assigned.CropRect : null;

                if (slot.Thumb.SourceHwnd != assigned.Hwnd)
                    slot.Thumb.Assign(assigned.Hwnd);
                else
                    slot.Thumb.RefreshLayout();

                // Re-apply after Assign (Assign Clear(keepCrop) keeps host crop fields).
                slot.Thumb.FillHost = fillThumb;
                slot.Thumb.SourceCrop = hasCrop ? assigned.CropRect : null;
                slot.Thumb.RefreshLayout();

                slot.Placeholder.Visibility = slot.Thumb.HasThumbnail
                    ? Visibility.Collapsed
                    : Visibility.Visible;
                slot.Hint.Visibility = Visibility.Visible;
                ShowFitPromptUi(i, assigned.ShowFitPrompt && previewProxy);
            }
            else
            {
                if (slot.ContentGrid != null)
                    slot.ContentGrid.Background = Brushes.Black;
                slot.Thumb.Clear();
                slot.Placeholder.Visibility = Visibility.Visible;
                slot.Hint.Visibility = Visibility.Visible;
                ShowFitPromptUi(i, false);
            }

            UpdateCropButtons(i);

            // Overlay only while actively selecting a crop. After apply: thumb + Menu/Uncrop only.
            if (slot.CropOverlay != null)
            {
                if (IsCropInteractionZone(i))
                {
                    slot.CropOverlay.Visibility = Visibility.Visible;
                    slot.CropOverlay.IsHitTestVisible = true;
                    slot.CropOverlay.Background = new SolidColorBrush(Color.FromArgb(0x66, 0, 0, 0));
                    Panel.SetZIndex(slot.CropOverlay, 200);
                    slot.Hint.Visibility = Visibility.Visible;
                    slot.Hint.Text = "Drag to select crop region. Esc cancels.";
                    if (slot.MenuBtn != null)
                        slot.MenuBtn.Visibility = Visibility.Collapsed;
                }
                else
                {
                    slot.CropOverlay.Children.Clear();
                    slot.CropOverlay.Visibility = Visibility.Collapsed;
                    slot.CropOverlay.IsHitTestVisible = false;
                    if (slot.MenuBtn != null)
                    {
                        slot.MenuBtn.Visibility = Visibility.Collapsed;
                        slot.MenuBtn.IsHitTestVisible = false;
                    }
                }
            }
        }
    }
    /// <summary>
    /// Kill crop/preview park/cloak/overlay wars for this build. Live Fill is the stable path.
    /// </summary>
    private void DisableCropPipelineForStableLiveFill()
    {
        if (CropPreviewEnabled) return;
        try { EndCropMode(apply: false); } catch { }
        foreach (var w in _tiler.WorkingSet.ToList())
        {
            if (w == null) continue;
            if (w.HasCrop)
            {
                w.CropRect = null;
                try { UnparkCroppedHwnd(w); } catch { }
            }
            if (NativeMethods.IsWindow(w.Hwnd))
            {
                try { NativeMethods.UnparkFromCropPreview(w.Hwnd); } catch { }
            }
        }
        try
        {
            if (_settings.PreviewCrops != null)
                _settings.PreviewCrops.Clear();
        }
        catch { }
        SyncSideCropButtons();
        HideAllPaneChromeOverlays();
        ClosePaneMenuWindow();
        try { StatusText.Text = "Crop disabled"; } catch { }
    }

    /// <summary>True when any assigned Live HWND is missing/off its current pane rect (blank/misplaced).</summary>
    private bool AnyLiveTileOffPane()
    {
        if (!_liveDesk) return false;
        var zones = ComputePaneScreenZones();
        if (zones.Length == 0) return false;
        for (int i = 0; i < _paneCount; i++)
        {
            var w = _tiler.GetZone(i);
            if (w == null || w.Parked || w.PreviewProxy) continue;
            if (CropPreviewEnabled && (w.HasCrop || IsCropInteractionZone(i))) continue;
            if (!NativeMethods.IsWindow(w.Hwnd)) continue;
            if (i >= zones.Length || zones[i].IsEmpty) return true;
            if (NativeMethods.IsIconic(w.Hwnd)) return true;
            if (!NativeMethods.IsWindowVisible(w.Hwnd)) return true;
            // Live Fill should sit on the pane. Low overlap / off-rect => blank until click.
            if (!NativeMethods.IsVisiblyAt(w.Hwnd, zones[i], tol: 24))
            {
                if (!NativeMethods.GetWindowRect(w.Hwnd, out var wr)) return true;
                int paneArea = Math.Max(1, zones[i].Width * zones[i].Height);
                int interW = Math.Max(0, Math.Min(wr.Right, zones[i].Right) - Math.Max(wr.Left, zones[i].Left));
                int interH = Math.Max(0, Math.Min(wr.Bottom, zones[i].Bottom) - Math.Max(wr.Top, zones[i].Top));
                double overlap = (double)(interW * interH) / paneArea;
                if (overlap < 0.50) return true;
            }
        }
        return false;
    }
    private bool AnyFillPreviewShouldBeLive()
    {
        if (!_liveDesk) return false;
        for (int i = 0; i < _paneCount; i++)
        {
            var w = _tiler.GetZone(i);
            if (w == null || w.Parked || !w.PreviewProxy) continue;
            if (w.HasCrop || w.Collapsed || w.Floating) continue;
            if (string.Equals(FitModeForZone(i), "fill", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private void PromoteFillPreviewToLive()
    {
        if (!_liveDesk) return;
        for (int i = 0; i < _paneCount; i++)
        {
            var w = _tiler.GetZone(i);
            if (w == null || w.Parked || !w.PreviewProxy || w.HasCrop) continue;
            if (w.Collapsed || w.Floating) continue;
            if (!string.Equals(FitModeForZone(i), "fill", StringComparison.OrdinalIgnoreCase)) continue;
            EnterLiveFill(w);
        }
    }
    private void SoftRefresh()
    {
        if (!_docsFileDragClickThru && _liveDesk)
        {
            try
            {
                foreach (var w in _tiler.WorkingSet)
                {
                    if (w.Parked) continue;
                    if (NativeMethods.IsWindow(w.Hwnd))
                        NativeMethods.SetWindowClickThrough(w.Hwnd, through: false);
                }
            }
            catch { }
        }
        CheckSnipToolYield();
        // v1.0.1: taskbar-restored collapsed window -> Expand; minimized floating window -> card.
        try { ReconcileCollapsedWindows(); } catch { }
        // v1.0.2: desk inactive + not snipping: never leave a Linked tile under the desk (z-order only).
        if (!_snipYieldActive && !IsActive) LowerDeskBelowBuriedTiles();
        // STABILIZE: SoftRefresh = prune-dead + window list only.
        // Docs/Preview: re-pin indefinitely while overlay open / Docs chrome focused (not 4s-only).
        // Prefer cheap heal; full Assert only when a tile lost TOPMOST (avoids Raise blink loop).
        try
        {
            if (_liveDesk
                && WindowState != WindowState.Minimized && !_deskMinimized)
            {
                // Snip hands-off: while Snipping Tool active, do NOT SetWindowPos / SetLiveTopmost /
                // SoftRefresh heal / Ensure on Live tiles — only desk Topmost=false (CheckSnipToolYield).
                if (_snipYieldActive)
                {
                    // Intentional noop on tiles — touching them blanks/blinks during snip.
                }
                else if (_yieldedForOutsideFocus && !IsActive && !IsDocsChromeProtecting())
                {
                    EnsureLiveTilesVisibleForCapture(healOnly: false);
                }
                else if (IsDocsChromeProtecting())
                {
                    // Clear stuck click-through that looked like vanished panes.
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

                    bool needAssert = _yieldedForOutsideFocus;
                    if (!needAssert)
                    {
                        for (int i = 0; i < _paneCount; i++)
                        {
                            var w = _tiler.GetZone(i);
                            if (w == null || w.Parked || w.PreviewProxy) continue;
                            if (!NativeMethods.IsWindow(w.Hwnd)) continue;
                            if (NativeMethods.IsIconic(w.Hwnd) || !NativeMethods.IsWindowVisible(w.Hwnd)
                                || !NativeMethods.IsWindowTopmost(w.Hwnd))
                            { needAssert = true; break; }
                        }
                    }
                    if (needAssert)
                        AssertAllAssignedLiveTiles(IntPtr.Zero, focus: false, allowWhenLiveFocused: true, forceWhenInactive: true);
                    // Debounced quiet raise (Assert force-path already re-raised when tiles were re-pinned).
                    else if (_filesOverlayOpen) try { DocsOverlayWindow.RaiseOpenInstanceIfAny(activate: false); } catch { }
                }
                else if (_colorStripOpen)
                {
                    // Color strip: SoftRefresh stays quiet — only force-pin if a tile lost TOPMOST.
                    // Accent/bg apply path already ShowAndRaise; avoid RaiseDashboard thrash.
                    bool needPin = false;
                    for (int i = 0; i < _paneCount; i++)
                    {
                        var w = _tiler.GetZone(i);
                        if (w == null || w.Parked || w.PreviewProxy) continue;
                        if (!NativeMethods.IsWindow(w.Hwnd)) continue;
                        if (NativeMethods.IsIconic(w.Hwnd) || !NativeMethods.IsWindowVisible(w.Hwnd)
                            || !NativeMethods.IsWindowTopmost(w.Hwnd) || IsTileBelowDesk(w.Hwnd))
                        { needPin = true; break; }
                    }
                    if (needPin)
                    {
                        try
                        {
                            var zones = ComputePaneScreenZones();
                            if (zones.Length > 0)
                                ShowAndRaiseAllLiveTiles(zones);
                        }
                        catch { }
                    }
                }
                else
                    EnsureLiveTilesTopmostIfNeeded();
            }
        }
        catch { }
        bool mapChanged = _tiler.PruneDead();

        var selectedHwnd = (WindowList.SelectedItem as WindowEntry)?.Hwnd ?? IntPtr.Zero;
        var windows = WindowEnumerator.GetTopLevelWindows(_ownHwnd, Process.GetCurrentProcess().Id);
        windows = CollapseWmplayerExtras(windows);

        var current = WindowList.Items.Cast<WindowEntry>().Select(w => w.Hwnd).ToHashSet();
        var next = windows.Select(w => w.Hwnd).ToHashSet();
        bool listChanged = !current.SetEquals(next);
        if (listChanged)
        {
            StampLinkedFlags(windows);
            WindowList.ItemsSource = windows;
            if (selectedHwnd != IntPtr.Zero)
            {
                var match = windows.FirstOrDefault(w => w.Hwnd == selectedHwnd);
                if (match != null)
                    WindowList.SelectedItem = match;
            }
        }
        else
        {
            SyncLinkedFlagsInList();
        }

        foreach (var w in _tiler.WorkingSet)
        {
            var match = windows.FirstOrDefault(x => x.Hwnd == w.Hwnd);
            if (match != null && match.Title != w.Title)
            {
                w.Title = TitleSanitizer.Clean(match.Title);
                w.NotifyTitle();
            }
        }

        if (mapChanged)
            RefreshPaneButtons();

 // No periodic retile/yield here - that interrupted video and blanked panes.
        // Heal only via PostLayoutLiveHeal; z-yield only via Deactivated.
        try { StampNowPlayingFlags(); } catch { }
        // Auto Preview only when the window list gains a NEW hwnd (never from Assert).
        // MaybeAutoPreview is disabled; keep call site for listChanged-only (never Assert).
        if (listChanged) try { MaybeAutoPreviewNewWindows(); } catch { }
    }

    /// <summary>
    /// If the user is working in a normal (unassigned) window, drop Live HWND_TOPMOST
    /// so that window is not stuck behind tiled panes / the desk.
    /// </summary>
    private void YieldLiveTopmostIfOutsideDesk()
    {
        YieldLiveZOrderForOutsideFocus();
    }

    /// <summary>
    /// Desk lost focus to an unassigned app: drop desk Topmost + Live HWND_TOPMOST so that
    /// app can sit on top for quick work. Keep linked tiles raised (HWND_TOP, not TOPMOST)
    /// so panes stay visible above the desk instead of blanking under it.
    /// Re-pin on Dashboard_Activated.
    /// </summary>
            private void YieldLiveZOrderForOutsideFocus()
    {
        // Preview overlay / Docs chrome / same-process FG: never bury Live tiles.
        if (IsDocsChromeProtecting())
        {
            _yieldedForOutsideFocus = false;
            try { KeepLivePanesAfterDocs(); } catch { }
            return;
        }
        // Snip UI up: never Clear TOPMOST/bury tiles under desk — hand off to snip yield.
        if (IsSnipToolUiPresent())
        {
            try { CheckSnipToolYield(); } catch { }
            return;
        }
        try { Topmost = false; } catch { }
        if (!_liveDesk) return;
        if (WindowState == WindowState.Minimized || _deskMinimized) return;
        if (DateTime.UtcNow < _chromePopupQuietUntilUtc) return;
        if (LiveLinkGuard.ShouldSuppressOutsideYield(DateTime.UtcNow, _paneClickQuietUntilUtc, _linkQuietUntilUtc, _linkInteractionQuietUntilUtc))
            return;
        if (_colorStripOpen) return;

        try
        {
            _yieldedForOutsideFocus = true;

            if (_ownHwnd != IntPtr.Zero && NativeMethods.IsWindow(_ownHwnd))
            {
                NativeMethods.ClearExTopmostKeepZOrder(_ownHwnd);
                NativeMethods.SetWindowPos(
                    _ownHwnd, NativeMethods.HWND_NOTOPMOST, 0, 0, 0, 0,
                    NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
            }

            var fg = NativeMethods.GetForegroundWindow();
            if (fg == IntPtr.Zero || fg == _ownHwnd || IsAssignedLiveHwnd(fg))
                fg = IntPtr.Zero;

            // Drop Live TOPMOST so outside apps can cover panes, but ALWAYS keep tiles
            // above the opaque desk (never HideWindow / bury under chrome).
            EnsureLiveTilesVisibleForCapture();

            if (fg != IntPtr.Zero && NativeMethods.IsWindow(fg))
            {
                // Outside app on top of tiles + desk (do NOT re-insert desk under FG last).
                NativeMethods.SetWindowPos(
                    fg, NativeMethods.HWND_TOP, 0, 0, 0, 0,
                    NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
            }
            else
            {
                // FG still desk/unknown ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬Ãƒâ€šÃ‚Â retry shortly so VLC/etc. is not left under the desk.
                ScheduleRaiseOutsideForeground();
            }
        }
        catch { }
    }

    private void RefreshThumbnailsSoft()
    {
        for (int i = 0; i < _paneCount && i < _slots.Length; i++)
        {
            var assigned = _tiler.GetZone(i);
            if (assigned == null || assigned.Parked || !NativeMethods.IsWindow(assigned.Hwnd))
                continue;
            if (_liveDesk && (assigned.Collapsed || assigned.Floating))
                continue; // card owns the pane (RefreshCollapsedPaneUi)
            if (!(assigned.PreviewProxy || assigned.HasCrop || NativeMethods.IsIconic(assigned.Hwnd) || !_liveDesk))
                continue;
            var slot = _slots[i];
            if (slot.Thumb.SourceHwnd != assigned.Hwnd)
                slot.Thumb.Assign(assigned.Hwnd);
            else
                slot.Thumb.RefreshLayout();
            UpdateCropButtons(i);
        }
    }

            private static bool IsMediaPlayerEntry(WindowEntry w)
    {
        if (w == null) return false;
        if (w.ProcessName.IndexOf("wmplayer", StringComparison.OrdinalIgnoreCase) >= 0) return true;
        if (w.ProcessName.IndexOf("MediaPlayer", StringComparison.OrdinalIgnoreCase) >= 0) return true;
        if ((w.Title ?? "").IndexOf("Windows Media Player", StringComparison.OrdinalIgnoreCase) >= 0) return true;
        return false;
    }

    /// <summary>One Media Player row in the list: prefer Live-linked, else largest.</summary>
    private List<WindowEntry> CollapseWmplayerExtras(List<WindowEntry> windows)
    {
        try
        {
            var players = windows.Where(IsMediaPlayerEntry).ToList();
            if (players.Count <= 1) return windows;

            var linked = new HashSet<IntPtr>();
            foreach (var w in _tiler.WorkingSet)
                if (w != null && !w.Parked && w.Hwnd != IntPtr.Zero)
                    linked.Add(w.Hwnd);

            WindowEntry? keep = players.FirstOrDefault(w => linked.Contains(w.Hwnd));
            if (keep == null)
            {
                keep = players
                    .Select(w =>
                    {
                        long area = 0;
                        try
                        {
                            if (NativeMethods.IsWindowVisible(w.Hwnd)
                                && NativeMethods.GetWindowRect(w.Hwnd, out var r) && !r.IsEmpty)
                                area = (long)r.Width * r.Height;
                        }
                        catch { }
                        return (w, area);
                    })
                    .OrderByDescending(t => linked.Contains(t.w.Hwnd))
                    .ThenByDescending(t => t.area)
                    .Select(t => t.w)
                    .FirstOrDefault();
            }
            if (keep == null) return windows;

            var keepHwnd = keep.Hwnd;
            return windows.Where(w => !IsMediaPlayerEntry(w) || w.Hwnd == keepHwnd).ToList();
        }
        catch { return windows; }
    }
private void RefreshWindowList()
    {
        var selectedHwnd = (WindowList.SelectedItem as WindowEntry)?.Hwnd ?? IntPtr.Zero;
        var windows = WindowEnumerator.GetTopLevelWindows(_ownHwnd, Process.GetCurrentProcess().Id);
        windows = CollapseWmplayerExtras(windows);
        StampLinkedFlags(windows);
        WindowList.ItemsSource = windows;
        if (selectedHwnd != IntPtr.Zero)
        {
            var match = windows.FirstOrDefault(w => w.Hwnd == selectedHwnd);
            if (match != null)
                WindowList.SelectedItem = match;
        }
        StatusText.Text = $"{windows.Count} windows - {_tiler.WorkingSet.Count} working - {(_liveDesk ? "Live desk" : "Preview")}";
    }

    private WindowEntry? Selected => WindowList.SelectedItem as WindowEntry;

    private void StampLinkedFlags(IEnumerable<WindowEntry> windows)
    {
        var linked = _tiler.WorkingSet
            .Where(w => !w.Parked && NativeMethods.IsWindow(w.Hwnd))
            .Select(w => w.Hwnd)
            .ToHashSet();
        uint npPid = 0;
        string npApp = "";
        try { npPid = _media.NowPlayingProcessId; npApp = _media.NowPlayingApp ?? ""; } catch { }
        foreach (var e in windows)
        {
            e.IsLinked = linked.Contains(e.Hwnd);
            bool play = false;
            if (npPid != 0 && e.ProcessId == npPid) play = true;
            else if (!string.IsNullOrEmpty(npApp)
                     && string.Equals(e.ProcessName, npApp, StringComparison.OrdinalIgnoreCase))
                play = true;
            e.IsNowPlaying = play;
        }
    }

    private void StampNowPlayingFlags()
    {
        if (WindowList.ItemsSource is IEnumerable<WindowEntry> list)
            StampLinkedFlags(list);
    }

    private void SyncLinkedFlagsInList()
    {
        if (WindowList.ItemsSource is IEnumerable<WindowEntry> list)
            StampLinkedFlags(list);
    }

    /// <summary>Prevent drag-start; assign/unlink on Down so one click never races SoftRefresh.</summary>
    private void LinkToggle_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragPending = false;
        e.Handled = true;
        _linkToggleArmed = true;
        PerformLinkToggle(sender);
    }

    /// <summary>Green = linked to a pane; red = unlinked. Up is a no-op if Down already handled.</summary>
    private void LinkToggle_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _dragPending = false;
        if (_linkToggleArmed)
        {
            _linkToggleArmed = false;
            return;
        }
        PerformLinkToggle(sender);
    }

    /// <summary>UI Automation Invoke on a link chip (same as a click).</summary>
    private void LinkChip_Invoked(object sender, RoutedEventArgs e)
    {
        _dragPending = false;
        _linkToggleArmed = false;
        PerformLinkToggle(sender);
    }

    private void PerformLinkToggle(object sender)
    {
        WindowEntry? entry = null;
        if (sender is FrameworkElement fe)
        {
            entry = fe.Tag as WindowEntry;
            if (entry == null)
                entry = fe.DataContext as WindowEntry;
        }
        if (entry == null || entry.Hwnd == IntPtr.Zero)
            return;

        WindowList.SelectedItem = entry;

        var existing = _tiler.WorkingSet.FirstOrDefault(w => w.Hwnd == entry.Hwnd && !w.Parked);
        if (existing != null)
        {
            UnlinkWorking(existing);
            SyncLinkedFlagsInList();
            return;
        }

        AssignToPane(entry, ZoneIndexFromVisual(_activePane), swapIfOccupied: false);
        SyncLinkedFlagsInList();
    }

    private void AssignToActive()
    {
        var entry = Selected;
        if (entry == null)
        {
            StatusText.Text = "Select a window first.";
            return;
        }
        AssignToPane(entry, ZoneIndexFromVisual(_activePane), swapIfOccupied: false);
    }

    /// <summary>
    /// Assign into a specific pane. Never clears unrelated panes.
    /// When <paramref name="swapIfOccupied"/> and both source+target are assigned, swaps.
    /// Linking is user-initiated only: never auto-Expand or auto-move to largest pane.
    /// If the pane is too small for the app min size: PreviewProxy + fitted DWM thumb + chooser
    /// (Change layout / Move to main / Keep preview). Real HWND bounds left unchanged. Never auto-Expand.
    /// </summary>
    private void AssignToPane(WindowEntry entry, int zoneIndex, bool swapIfOccupied)
    {
        zoneIndex = Math.Clamp(zoneIndex, 0, _paneCount - 1);
        NativeMethods.GetWindowTrackSize(entry.Hwnd, out int minW, out int minH, out _, out _);
        bool fixedSize = NativeMethods.IsFixedSizeWindow(entry.Hwnd)
            || entry.ProcessName.Contains("movavi", StringComparison.OrdinalIgnoreCase)
            || entry.Title.Contains("movavi", StringComparison.OrdinalIgnoreCase);

        var zones = ComputePaneScreenZones();
        int target = zoneIndex;
        bool tooSmall = false;
        string sizeNote = "";

        if (_liveDesk && zones.Length > 0 && target < zones.Length && !zones[target].IsEmpty)
        {
            tooSmall = !NativeMethods.PaneFitsMin(zones[target], minW, minH);
            if (tooSmall)
 sizeNote = $" pane too small for app min {minW}x{minH}; PreviewProxy";
            else if (fixedSize)
                sizeNote = $" (fixed-size; Fit centers in pane)";
        }

        var beforeOther = SnapshotAssignments(except: target);

        bool ok = _tiler.Assign(entry, target, tile: false, activate: false, swapIfOccupied: swapIfOccupied);
        // Displaced occupant was minimized by the tiler; drop our pin bookkeeping for it.
        var displaced = _tiler.LastDisplacedHwnd;
        if (displaced != IntPtr.Zero && displaced != entry.Hwnd)
            _liveTopmostPinned.Remove(displaced);

        EnsureAssignmentsIntact(beforeOther);

        _activePane = target + 1;
        HighlightActivePane();
        RefreshPaneButtons();

        int visualTarget = VisualPaneNumber(target);
        var w = _tiler.GetZone(target);

        if (ok && w != null && _liveDesk && tooSmall
            && !string.Equals(FitModeForZone(target), "fill", StringComparison.OrdinalIgnoreCase))
        {
            EnterPreviewProxy(w, showPrompt: true);
            BeginLinkTargetOutlineFade();
            RefreshAllThumbnails();
            ShowFitPromptUi(target, true);
            StatusText.Text = $"Preview -> Pane {visualTarget}: {w.ShortTitle}{sizeNote}";
        }
        else if (ok && w != null && _liveDesk)
        {
            EnterLiveFill(w);
            _linkQuietUntilUtc = DateTime.UtcNow.AddMilliseconds(1500);
            _paneClickQuietUntilUtc = DateTime.UtcNow.AddMilliseconds(1500);
            BeginLinkTargetOutlineFade();
            // Settle layout so first place uses the same zones as later Assert (initial-link align).
            try { UpdateLayout(); PaneGrid?.UpdateLayout(); } catch { }
            // Tile ALL panes. While Preview open: no Focus/Activate (blink + buries overlay). Raise Preview last.
            if (_filesOverlayOpen)
                ApplyLiveTiling(activateZone: -1);
            else
                ApplyLiveTiling(activateZone: target);
            SchedulePostAssignHardClamp(target);
            SyncLivePaneChromeOverlays();
            if (_filesOverlayOpen) try { DocsOverlayWindow.RaiseOpenInstanceIfAny(activate: false, forceAboveTiles: true); } catch { }
            if (fixedSize)
                StatusText.Text = $"Live -> Pane {visualTarget}: {w.ShortTitle}{sizeNote}";
            else
                StatusText.Text = $"Live -> Pane {visualTarget}: {w.ShortTitle} (other panes kept){sizeNote}";
        }
        else if (ok)
        {
            if (w != null) EnterLiveFill(w); // Live off: thumbnails only
            RefreshAllThumbnails();
            StatusText.Text = $"Assigned -> Pane {visualTarget}: {entry.ShortTitle} (preview - toggle Live to tile){sizeNote}";
        }
        else
            StatusText.Text = "Assign failed.";
        SyncLinkedFlagsInList();
    }
    private Dictionary<int, IntPtr> SnapshotAssignments(int except)
    {
        var map = new Dictionary<int, IntPtr>();
        for (int i = 0; i < 4; i++)
        {
            if (i == except) continue;
            var w = _tiler.GetZone(i);
            if (w != null) map[i] = w.Hwnd;
        }
        return map;
    }

    private void EnsureAssignmentsIntact(Dictionary<int, IntPtr> beforeOther)
    {
        // Restore sibling zones that should still hold their HWND after a target-only Assign.
        foreach (var kv in beforeOther)
        {
            var w = _tiler.GetZone(kv.Key);
            if (w != null && w.Hwnd == kv.Value)
                continue; // intact

            // If this HWND intentionally moved to another zone (source of the assign), leave it.
            var still = _tiler.WorkingSet.FirstOrDefault(x => x.Hwnd == kv.Value && !x.Parked);
            if (still != null && still.ZoneIndex != kv.Key && still.ZoneIndex >= 0)
                continue;

            // Sibling missing or wrong ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬Ãƒâ€šÃ‚Â re-Assign via tiler (no tile / no activate).
            if (kv.Value == IntPtr.Zero || !NativeMethods.IsWindow(kv.Value))
                continue;
            var entry = CaptureWindowEntry(kv.Value) ?? EntryFromHwnd(kv.Value);
            if (entry == null)
            {
                entry = new WindowEntry
                {
                    Hwnd = kv.Value,
                    Title = still?.Title ?? "",
                    ProcessName = still?.ProcessName ?? ""
                };
            }
            try { _tiler.Assign(entry, kv.Key, tile: false, activate: false, swapIfOccupied: false); }
            catch { }
        }
    }

    private void BtnAssign_Click(object sender, RoutedEventArgs e) => AssignToActive();

    private void BtnUnassign_Click(object sender, RoutedEventArgs e)
    {
        UnlinkActiveOrSelected();
    }

    /// <summary>Unlink active pane (or selected list window if assigned) and restore prior desktop bounds.</summary>
    private void UnlinkActiveOrSelected()
    {
        WorkingWindow? item = _tiler.GetZone(ZoneIndexFromVisual(_activePane));
        if (item == null && Selected != null)
            item = _tiler.WorkingSet.FirstOrDefault(w => w.Hwnd == Selected.Hwnd);
        if (item == null)
        {
            StatusText.Text = "Nothing to Unlink - select a Working chip or active pane.";
            return;
        }
        string name = item.ShortTitle;
        _liveTopmostPinned.Remove(item.Hwnd);
        _tiler.Unassign(item, restore: true, minimize: true);
        RefreshPaneButtons();
        RefreshAllThumbnails();
        if (_liveDesk) ApplyLiveTiling();
        StatusText.Text = $"Unlinked {name} - minimized (restores to its prior size).";
        SyncLinkedFlagsInList();
    }

    private void UnlinkWorking(WorkingWindow item)
    {
        string name = item.ShortTitle;
        _liveTopmostPinned.Remove(item.Hwnd);
        _tiler.Unassign(item, restore: true, minimize: true);
        RefreshPaneButtons();
        RefreshAllThumbnails();
        if (_liveDesk) ApplyLiveTiling();
        StatusText.Text = $"Unlinked {name} - minimized (restores to its prior size).";
        SyncLinkedFlagsInList();
    }

    private void BtnFocus_Click(object sender, RoutedEventArgs e)
    {
        if (Selected != null)
        {
            if (_liveDesk)
                AssertAllAssignedLiveTiles(keepFocusHwnd: Selected.Hwnd, focus: true);
            else
                NativeMethods.FocusWindow(Selected.Hwnd);
            StatusText.Text = $"Focused {Selected.ShortTitle} (restored if minimized)";
            return;
        }

        var z = _tiler.GetZone(ZoneIndexFromVisual(_activePane));
        if (z != null)
        {
            if (_liveDesk)
                AssertAllAssignedLiveTiles(keepFocusHwnd: z.Hwnd, focus: true);
            else
                _tiler.FocusOnly(z);
            StatusText.Text = $"Focused {z.ShortTitle} (restored if minimized)";
            RefreshAllThumbnails();
            return;
        }

        StatusText.Text = "Nothing to focus - select a window or assign a pane.";
    }



    private void BtnPark_Click(object sender, RoutedEventArgs e)
    {
        var z = _tiler.GetZone(ZoneIndexFromVisual(_activePane));
        if (z == null)
        {
            StatusText.Text = "Active pane empty - nothing to park.";
            return;
        }
        _tiler.Park(z);
        RefreshPaneButtons();
        RefreshAllThumbnails();
        if (_liveDesk) ApplyLiveTiling();
        StatusText.Text = $"Parked {z.ShortTitle} (chip kept; Live skips minimized).";
    }

    private void BtnRefresh_Click(object sender, RoutedEventArgs e)
    {
        try { EnsureWmpFitPrefs(); } catch { }
        try { ApplyAccentColor(_accentHex, persist: false); } catch { }
            try { ApplyBackgroundColor(_backgroundHex, persist: false); } catch { }
        RefreshWindowList();
        try { SetDocsMode(false); } catch { try { EnsureDocsStaging(); RefreshDocsList(); } catch { } }
        try { _media.Refresh(); SyncMediaPaneLabels(); } catch { }
        RefreshAllThumbnails();
    }

    private void WindowList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Selected != null)
        {
            OpenWindowInPreview(Selected);
            e.Handled = true;
        }
    }


    private void BtnMediaMute_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is MediaSessionItem item)
        {
            if (_media.ToggleMute(item.ProcessId))
                StatusText.Text = item.IsMuted ? $"Muted {item.ProcessName}" : $"Unmuted {item.ProcessName}";
            else
                StatusText.Text = "Mute failed.";
        }
    }

    private async void BtnMediaRewind10_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var (ok, detail) = await _media.TrySeekRelativeAsync(-MediaSeek.StepSeconds);
            StatusText.Text = detail;
            if (!ok) { /* keep detail */ }
        }
        catch { StatusText.Text = "Rewind failed."; }
    }

    private async void BtnMediaForward10_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var (ok, detail) = await _media.TrySeekRelativeAsync(MediaSeek.StepSeconds);
            StatusText.Text = detail;
            if (!ok) { /* keep detail */ }
        }
        catch { StatusText.Text = "Forward failed."; }
    }

    private async void BtnMediaPrev_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var (ok, detail) = await _media.TryPreviousAsync();
            StatusText.Text = detail;
            if (!ok) { /* keep detail */ }
        }
        catch { StatusText.Text = "Previous failed."; }
    }

    private async void BtnMediaPlayPause_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var (ok, detail) = await _media.TryPlayPauseAsync();
            StatusText.Text = detail;
        }
        catch { StatusText.Text = "Play/Pause failed."; }
    }

    private async void BtnMediaNext_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var (ok, detail) = await _media.TryNextAsync();
            StatusText.Text = detail;
        }
        catch { StatusText.Text = "Next failed."; }
    }

    // ---- Volume slider (replaces Vol + / Vol -; same target: default render endpoint master volume).
    private bool _volSyncing;
    private DateTime _volUserUntilUtc = DateTime.MinValue;

    private void SldMediaVolume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_volSyncing || !IsLoaded) return;
        _volUserUntilUtc = DateTime.UtcNow.AddMilliseconds(1500); // don't fight the user's drag with device sync
        if (_media.SetMasterVolume((float)(e.NewValue / 100.0), out float level))
        {
            int pct = (int)Math.Round(level * 100);
            SetVolumeReadout(pct.ToString());
            StatusText.Text = $"Volume {pct}%";
        }
        else
            StatusText.Text = "Volume: no output device.";
    }

    private void SldMediaVolume_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        if (SldMediaVolume == null) return;
        SldMediaVolume.Value = Math.Clamp(SldMediaVolume.Value + (e.Delta > 0 ? 2 : -2), 0, 100);
        e.Handled = true;
    }

    /// <summary>Narrow Media panel: drop the level number first (tooltip keeps it) so nothing squishes or wraps.</summary>
    private void MediaVolumeHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (TxtMediaVolume == null) return;
        var want = e.NewSize.Width < 70 ? Visibility.Collapsed : Visibility.Visible;
        if (TxtMediaVolume.Visibility != want) TxtMediaVolume.Visibility = want;
    }

    private void SetVolumeReadout(string text)
    {
        if (TxtMediaVolume != null && TxtMediaVolume.Text != text) TxtMediaVolume.Text = text;
        string tip = text == "--" ? "Volume (no output device)" : $"Volume {text}{(text == "Mute" ? "" : "%")} (drag, click, or scroll)";
        if (SldMediaVolume != null && !Equals(SldMediaVolume.ToolTip, tip)) SldMediaVolume.ToolTip = tip;
    }

    /// <summary>Pull the slider from the device (other apps / keyboard keys / device switch change it).</summary>
    private void SyncVolumeSlider()
    {
        try
        {
            if (SldMediaVolume == null) return;
            if (DateTime.UtcNow < _volUserUntilUtc || SldMediaVolume.IsMouseCaptureWithin) return;
            if (!_media.TryGetMasterVolume(out float level, out bool muted))
            {
                SetVolumeReadout("--");
                return;
            }
            double v = Math.Round(level * 100);
            _volSyncing = true;
            try { if (Math.Abs(SldMediaVolume.Value - v) >= 0.5) SldMediaVolume.Value = v; }
            finally { _volSyncing = false; }
            SetVolumeReadout(muted ? "Mute" : ((int)v).ToString());
        }
        catch { }
    }

    private async void BtnMediaSkipAd_Click(object sender, RoutedEventArgs e)
        => await RunSkipAdAsync();

    private void OnHotkeySkipAd()
    {
        _ = Dispatcher.InvokeAsync(async () => await RunSkipAdAsync());
    }

    // v1.0.1: RunSkipAdAsync (manual skip) lives in MainWindow.SkipAd.cs with the watcher / auto-skip.

    /// <summary>Prefer a Live-linked browser/YouTube pane for Skip Ad so F8 hits the right window first try.</summary>
    private IntPtr PreferSkipAdHwnd()
    {
        try
        {
            // 1) Now-playing linked pane
            uint npPid = 0;
            // Only steer by the Media card's source when that source IS YouTube: a lock on Spotify (or any other
            // source) must not influence which YouTube window the ad-skip watcher looks at.
            try { if (string.Equals(_media.NowPlayingSite, "YouTube", StringComparison.OrdinalIgnoreCase)) npPid = _media.NowPlayingProcessId; } catch { }
            foreach (var w in _tiler.WorkingSet)
            {
                if (w == null || w.Parked || w.Hwnd == IntPtr.Zero) continue;
                if (!NativeMethods.IsWindow(w.Hwnd)) continue;
                if (npPid != 0)
                {
                    NativeMethods.GetWindowThreadProcessId(w.Hwnd, out uint wpid);
                    if (wpid == npPid) return w.Hwnd;
                }
                string t = w.Title ?? "";
                if (t.IndexOf("YouTube", StringComparison.OrdinalIgnoreCase) >= 0)
                    return w.Hwnd;
            }
            // 2) Any linked browser
            foreach (var w in _tiler.WorkingSet)
            {
                if (w == null || w.Parked || w.Hwnd == IntPtr.Zero) continue;
                if (!NativeMethods.IsWindow(w.Hwnd)) continue;
                string p = w.ProcessName ?? "";
                if (p.Equals("opera", StringComparison.OrdinalIgnoreCase)
                    || p.Equals("chrome", StringComparison.OrdinalIgnoreCase)
                    || p.Equals("msedge", StringComparison.OrdinalIgnoreCase)
                    || p.Equals("firefox", StringComparison.OrdinalIgnoreCase)
                    || p.Equals("brave", StringComparison.OrdinalIgnoreCase))
                    return w.Hwnd;
            }
        }
        catch { }
        return IntPtr.Zero;
    }

    /// <summary>
    /// Re-assert Live TOPMOST after media actions that may briefly steal focus.
    /// When FG is an assigned Live tile, assert even though desk IsActive is false.
    /// </summary>
    private void PreserveLiveTilesAfterMediaAction(bool restoreDeskFocus = false)
    {
        if (!_liveDesk) return;
        try
        {
            if (restoreDeskFocus)
            {
                try { Activate(); } catch { }
                AssertAllAssignedLiveTiles(IntPtr.Zero, focus: false);
                return;
            }

            var fg = NativeMethods.GetForegroundWindow();
            if (IsAssignedLiveHwnd(fg))
                AssertAllAssignedLiveTiles(fg, focus: false, allowWhenLiveFocused: true);
            else if (IsActive)
                AssertAllAssignedLiveTiles(IntPtr.Zero, focus: false);
        }
        catch { }
    }

    private bool IsAssignedLiveHwnd(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !_liveDesk) return false;
        foreach (var w in _tiler.WorkingSet)
        {
            if (w.Parked) continue;
            if (w.ZoneIndex < 0 || w.ZoneIndex >= _paneCount) continue;
            if (w.Hwnd == hwnd) return true;
        }
        return false;
    }


    /// <summary>Label mute rows with P1/P2/P3 when that process is a Live Working window. Keep every audio PID (no app-name collapse).</summary>
    private void SyncMediaPaneLabels()
    {
        try
        {
            var pidToPane = new Dictionary<uint, string>();
            foreach (var w in _tiler.WorkingSet)
            {
                if (w.Parked || w.Hwnd == IntPtr.Zero) continue;
                if (w.ZoneIndex < 0 || w.ZoneIndex >= _paneCount) continue;
                try
                {
                    NativeMethods.GetWindowThreadProcessId(w.Hwnd, out uint pid);
                    if (pid == 0) continue;
                    string label = "P" + w.DisplayPaneNumber;
                    // Prefer first pane if same process spans multiple (rare).
                    if (!pidToPane.ContainsKey(pid))
                        pidToPane[pid] = label;
                }
                catch { }
            }

            foreach (var s in _media.Sessions)
                s.PaneLabel = pidToPane.TryGetValue(s.ProcessId, out var pl) ? pl : "";

            // Stable order: pane-linked first (P1,P2,P3), then everything else.
            var ordered = _media.Sessions
                .OrderBy(s => string.IsNullOrEmpty(s.PaneLabel) ? 1 : 0)
                .ThenBy(s => s.PaneLabel, StringComparer.OrdinalIgnoreCase)
                .ThenBy(s => s.ProcessName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(s => s.ProcessId)
                .ToList();

            for (int i = 0; i < ordered.Count; i++)
            {
                int cur = _media.Sessions.IndexOf(ordered[i]);
                if (cur >= 0 && cur != i)
                    _media.Sessions.Move(cur, i);
            }
        }
        catch { }
    }
    private void SyncMediaNowPlayingUi()
    {
        try
        {
            if (ImgMediaNow != null)
                ImgMediaNow.Source = _media.NowPlayingIcon;
        try { StampNowPlayingFlags(); } catch { }
            if (TxtMediaNow != null)
            {
                // Friendly, sanitized "<App> - <window/media title>" (MediaSessionService.NowPlayingLabel).
                // Never the raw AUMID / process id; mojibake is repaired or dropped by TitleSanitizer.
                string label = TitleSanitizer.Clean(_media.NowPlayingLabel);
                if (string.IsNullOrWhiteSpace(label))
                {
                    string app = MediaSessionService.FriendlyAppName(_media.NowPlayingApp);
                    string title = TitleSanitizer.Clean(_media.NowPlayingTitle);
                    label = string.IsNullOrWhiteSpace(_media.NowPlayingApp) && string.IsNullOrWhiteSpace(title)
                        ? "No media window"
                        : (string.IsNullOrWhiteSpace(title) ? app : app + " - " + title);
                }
                TxtMediaNow.Text = label;
                string tip = _media.NowPlayingTooltip;
                TxtMediaNow.ToolTip = string.IsNullOrWhiteSpace(tip) ? label : tip;
            }
            UpdateMediaPreview(); // v1.0.2 live preview / fallback / "Nothing playing" strip
        }
        catch { }
    }

    private void OnHotkeyMute()
    {
        Dispatcher.Invoke(() =>
        {
            try
            {
                if (_media.TogglePrimaryMute())
                {
                    var p = _media.Sessions.FirstOrDefault(s => s.ProcessId == _media.PrimaryProcessId);
                    StatusText.Text = p == null
                        ? "Toggled primary media mute"
                        : (p.IsMuted ? $"Muted {p.ProcessName}" : $"Unmuted {p.ProcessName}");
                }
                else
                    StatusText.Text = "No media session to mute.";
            }
            catch
            {
                StatusText.Text = "Mute hotkey failed.";
            }
        });
    }

    private void OnHotkeyFocus()
    {
        Dispatcher.Invoke(() =>
        {
            try
            {
                var hwnd = _media.FindPrimaryWindow();
                if (hwnd == IntPtr.Zero)
                {
                    StatusText.Text = "No media window to focus.";
                    return;
                }
                NativeMethods.FocusWindow(hwnd);
                if (_liveDesk && IsAssignedLiveHwnd(hwnd))
                    AssertAllAssignedLiveTiles(keepFocusHwnd: hwnd, focus: false, allowWhenLiveFocused: true);
                StatusText.Text = "Focused media window (skip ads manually if needed).";
            }
            catch
            {
                StatusText.Text = "Focus hotkey failed.";
            }
        });
    }
    private void LiveDesk_Click(object sender, RoutedEventArgs e)
    {
        _liveDesk = TbLiveDesk.IsChecked == true;
        if (!_liveDesk)
            ClearAllLiveTopmost();
        Persist();
        SyncLiveUi();
        RefreshAllThumbnails();
        if (_liveDesk)
        {
            ApplyLiveTiling();
            StatusText.Text = "Live desk ON - assigned windows tiled to pane geometry.";
        }
        else
        {
            StatusText.Text = "Live desk OFF - thumbnail preview only (windows left in place).";
        }
    }

    private void SyncLiveUi()
    {
        if (TbLiveDesk != null)
            TbLiveDesk.IsChecked = _liveDesk;
        if (DeskHint != null)
        {
            DeskHint.Text = _liveDesk
                ? "  -  Live desk - real windows tiled to panes"
                : "  -  Preview - Focus to interact (Live off)";
        }
    }

    /// <summary>
    /// Debounce Live retile after desk SizeChanged / move. Timer (75ms after last event)
    /// always fires ApplyLiveTiling so linked windows follow new pane screen bounds.
    /// Flip / layout / assign call ApplyLiveTiling directly (no debounce needed).
    /// </summary>
    /// <summary>One-shot delayed Live re-tile after pane layout changes (blank-pane race).</summary>
    private void PostLayoutLiveHeal()
    {
        if (!_liveDesk) return;
 // Single quiet heal ~180ms after layout/size '- avoid double re-TOPMOST blink.
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        t.Tick += (_, _) =>
        {
            t.Stop();
            try
            {
                if (!_liveDesk || WindowState == WindowState.Minimized || _deskMinimized) return;
                try { PaneGrid?.UpdateLayout(); } catch { }
                PromoteFillPreviewToLive();
                // Quiet retile only ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬Ãƒâ€¦Ã‚Â¡ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â AssertAll re-TOPMOST was blinking/vanishing panes.
                ApplyLiveTiling();
                if (_distractionFree) SyncShowUiOverlay();
            }
            catch { }
        };
        t.Start();
    }

    /// <summary>WMP/ONLYOFFICE often resize themselves after media loads; re-force Fill for a few seconds.</summary>

    /// <summary>WMP often keeps video zoomed to the previous HWND size; nudge Fill twice so ShrinkToFit reflows.</summary>
        private void NudgeWmpVideoReflow(IntPtr hwnd, int index)
    {
        if (hwnd == IntPtr.Zero || index < 0) return;
        _ = Dispatcher.BeginInvoke(async () =>
        {
            try
            {
                // Two gentle fills after media loads ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬Ãƒâ€¦Ã‚Â¡ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â aggressive multi-pass caused a resize loop + extra WMP windows.
                foreach (int wait in new[] { 700, 1600 })
                {
                    await System.Threading.Tasks.Task.Delay(wait == 700 ? 700 : 900);
                    if (!NativeMethods.IsWindow(hwnd)) return;
                    var zw = _tiler.GetZone(index);
                    if (zw != null && zw.Hwnd == hwnd && (zw.Collapsed || zw.Floating)) return;
                    var zones = ComputePaneScreenZones();
                    if (index >= zones.Length || zones[index].IsEmpty) continue;
                    NativeMethods.TileWindow(hwnd, zones[index], activate: false, "fill");
                }
            }
            catch { }
        }, DispatcherPriority.ApplicationIdle);
    }
    private static bool IsBrowserProcessName(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName)) return false;
        string p = processName.Trim();
        return p.Equals("opera", StringComparison.OrdinalIgnoreCase)
            || p.Equals("chrome", StringComparison.OrdinalIgnoreCase)
            || p.Equals("msedge", StringComparison.OrdinalIgnoreCase)
            || p.Equals("brave", StringComparison.OrdinalIgnoreCase)
            || p.Equals("firefox", StringComparison.OrdinalIgnoreCase)
            || p.Equals("vivaldi", StringComparison.OrdinalIgnoreCase)
            || p.Equals("browser", StringComparison.OrdinalIgnoreCase)
            || p.Equals("chromium", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Initial-link heal: layout/DPI often settles a few frames after Assign.
    /// Re-place + hard-clamp twice so the first tile is centered/flush in-zone.
    /// </summary>
    private void SchedulePostAssignHardClamp(int zoneIndex)
    {
        if (!_liveDesk || zoneIndex < 0) return;
        foreach (int delayMs in new[] { 50, 180 })
        {
            int z = zoneIndex;
            int wait = delayMs;
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(wait) };
            t.Tick += (_, _) =>
            {
                t.Stop();
                try
                {
                    if (!_liveDesk || WindowState == WindowState.Minimized || _deskMinimized) return;
                    var zones = ComputePaneScreenZones();
                    if (z >= zones.Length || zones[z].IsEmpty) return;
                    var w = _tiler.GetZone(z);
                    if (w == null || w.Parked || w.PreviewProxy) return;
                    if (!NativeMethods.IsWindow(w.Hwnd)) return;
                    if (string.Equals(FitModeForZone(z), "fill", StringComparison.OrdinalIgnoreCase))
                        NativeMethods.PlaceFillOrFitWidth(w.Hwnd, zones[z], out _);
                    else
                        NativeMethods.TileWindow(w.Hwnd, zones[z], activate: false, FitModeForZone(z));
                    if (NativeMethods.VisibleExceedsZone(w.Hwnd, zones[z], tol: 2))
                        NativeMethods.HardClampOuterToZone(w.Hwnd, zones[z]);
                    // Also re-clamp siblings so a late Large side bump cannot leave overlap.
                    for (int i = 0; i < _paneCount; i++)
                    {
                        if (i == z) continue;
                        var sib = _tiler.GetZone(i);
                        if (sib == null || sib.Parked || sib.PreviewProxy) continue;
                        if (i >= zones.Length || zones[i].IsEmpty) continue;
                        if (!NativeMethods.IsWindow(sib.Hwnd)) continue;
                        if (NativeMethods.VisibleExceedsZone(sib.Hwnd, zones[i], tol: 2))
                            NativeMethods.HardClampOuterToZone(sib.Hwnd, zones[i]);
                    }
                    VerifyLiveTilesSelfCheck(zones);
                }
                catch { }
            };
            t.Start();
        }
    }


    private void StartHoldFillEnforce(int zoneIndex, int ms = 2800)
    {
        // Browsers: skip SetWindowPos hold-fill loop (soft). Assign already PlaceFillOrFitWidth + PinLiveTopmost / ShowAndRaise.
        try
        {
            var bw = _tiler.GetZone(zoneIndex);
            if (bw != null && IsBrowserProcessName(bw.ProcessName))
                return;
        }
        catch { }
        // Only nudge the dropped pane; retile-all was vanishing every Live window.
        _holdFillZone = zoneIndex;
        _holdFillUntilUtc = DateTime.UtcNow.AddMilliseconds(ms);
        if (_holdFillTimer == null)
        {
            _holdFillTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(320) };
            _holdFillTimer.Tick += (_, _) =>
            {
                if (DateTime.UtcNow >= _holdFillUntilUtc)
                {
                    _holdFillTimer.Stop();
                    _holdFillZone = -1;
                    return;
                }
                if (!_liveDesk) return;
                int i = _holdFillZone;
                if (i < 0 || i >= _paneCount) return;
                try
                {
                    var zones = ComputePaneScreenZones();
                    var w = _tiler.GetZone(i);
                    if (w == null || w.Parked || w.PreviewProxy) return;
                    if (i >= zones.Length || zones[i].IsEmpty) return;
                    if (!NativeMethods.IsWindow(w.Hwnd)) return;
                    NativeMethods.TileWindow(w.Hwnd, zones[i], activate: false, FitModeForZone(i));
                }
                catch { }
            };
        }
        _holdFillTimer.Stop();
        _holdFillTimer.Start();
    }
    private void ScheduleLiveRetile()
    {
        if (!_liveDesk || _liveRetileTimer == null) return;
        if (_applyingSizePreset) return;
        if (WindowState == WindowState.Minimized || _deskMinimized) return;
        if (DateTime.UtcNow < _suppressLiveRetileUntilUtc) return;
        _liveRetileTimer.Stop();
        // Live drag: longer debounce; WM_EXITSIZEMOVE runs the authoritative settle pass.
        _liveRetileTimer.Interval = TimeSpan.FromMilliseconds(CurrentLiveRetileIntervalMs());
        _liveRetileTimer.Start();
    }

    private void ApplyLiveTiling(int activateZone = -1)
    {
        _thinExpandHintZone = -1;
        if (!_liveDesk) return;
        if (!IsLoaded || _ownHwnd == IntPtr.Zero) return;
        if (WindowState == WindowState.Minimized || _deskMinimized) return;

        // If desk grew enough, promote PreviewProxy -> LiveFill before tiling.
        PromotePreviewIfFitsQuiet();

        var zones = ComputePaneScreenZones();
        if (zones.Length == 0) return;

        // v1.0.1: collapse panes the app can't fit (or re-expand with hysteresis) BEFORE placing,
        // then recompute zones (collapsed panes stop reserving min-track width).
        if (EvaluateCollapseStates(zones))
        {
            zones = ComputePaneScreenZones();
            if (zones.Length == 0) return;
        }

        // v1.0.2: mid-drag -> one DeferWindowPos batch, no z-order/show/activate (MainWindow.LiveResize.cs).
        // The WM_EXITSIZEMOVE settle pass runs the full path below (clamp, spill, collapse).
        if (_inSizeMove && TryBatchLiveTilesDuringDrag(zones))
        {
            ReassertLiveTilesIfBuried();
            return;
        }

        bool anyTooSmall = false;
        int moved = 0;

        for (int i = 0; i < _paneCount; i++)
        {
            var w = _tiler.GetZone(i);
            if (w == null || w.Parked) continue;
            if (!NativeMethods.IsWindow(w.Hwnd)) continue;
            // Iconic tiles must be restored in ShowAndRaise — do not skip here.
            if (i >= zones.Length || zones[i].IsEmpty) continue;

            // PreviewProxy only; crop park/cloak DISABLED this build (CropPreviewEnabled=false).
            bool cropBusy = CropPreviewEnabled && (w.HasCrop || IsCropInteractionZone(i));
            string zoneFit = FitModeForZone(i);
            bool wantFill = string.Equals(zoneFit, "fill", StringComparison.OrdinalIgnoreCase);

            // Collapsed (minimized, card in pane) / Floating (over the desk): never placed into the zone.
            if (w.Collapsed || w.Floating)
                continue;

            // Fill mode: prefer Live stretch even when pane < app min (no letterboxed Preview).
            if (w.PreviewProxy && wantFill && !cropBusy)
            {
                EnterLiveFill(w);
                NativeMethods.PlaceFillOrFitWidth(w.Hwnd, zones[i], out _);
                if (NativeMethods.VisibleExceedsZone(w.Hwnd, zones[i], tol: 2))
                    NativeMethods.HardClampOuterToZone(w.Hwnd, zones[i]);
                moved++;
                continue;
            }

            if (w.PreviewProxy || cropBusy)
            {
                if (w.PreviewProxy) anyTooSmall = true;
                if (cropBusy)
                    ParkCroppedHwnd(w);
                else
                    ClearLiveTopmostTracked(w.Hwnd);
                continue;
            }

            NativeMethods.GetWindowMinTrackSize(w.Hwnd, out int winMinW, out int winMinH);
            if (!NativeMethods.PaneFitsMin(zones[i], winMinW, winMinH))
            {
                if (wantFill)
                {
                    // Fill if we can; else FitWidth+center. Stay Live (no PreviewProxy).
                    NativeMethods.PlaceFillOrFitWidth(w.Hwnd, zones[i], out string thinMode);
                    if (NativeMethods.VisibleExceedsZone(w.Hwnd, zones[i], tol: 2))
                        NativeMethods.HardClampOuterToZone(w.Hwnd, zones[i]);
                    if (string.Equals(thinMode, "fitwidth", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(thinMode, "fitheight", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(thinMode, "fitboth", StringComparison.OrdinalIgnoreCase)
                        || winMinW > zones[i].Width
                        || winMinH > zones[i].Height)
                    {
                        _thinExpandHintZone = i;
                        try { StatusText.Text = $"Expand pane {i + 1} for full window"; } catch { }
                        try { if (BtnExpandPane != null) BtnExpandPane.Content = $"Expand pane {i + 1}"; } catch { }
                    }
                    moved++;
                    continue;
                }
                // Fit mode: PreviewProxy + prompt.
                EnterPreviewProxy(w, showPrompt: true);
                anyTooSmall = true;
                continue;
            }

 // Do not activate mid-loop — FocusWindow mid-tile buries already-placed siblings under the desk.
            string fit = FitModeForZone(i);
            bool okPlace;
            if (string.Equals(fit, "fill", StringComparison.OrdinalIgnoreCase))
            {
                okPlace = NativeMethods.PlaceFillOrFitWidth(w.Hwnd, zones[i], out string placeMode);
                if (NativeMethods.VisibleExceedsZone(w.Hwnd, zones[i], tol: 2))
                    NativeMethods.HardClampOuterToZone(w.Hwnd, zones[i]);
                if (string.Equals(placeMode, "fitwidth", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(placeMode, "fitheight", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(placeMode, "fitboth", StringComparison.OrdinalIgnoreCase))
                {
                    _thinExpandHintZone = i;
                    try { StatusText.Text = $"Expand pane {i + 1} for full window"; } catch { }
                    try { if (BtnExpandPane != null) BtnExpandPane.Content = $"Expand pane {i + 1}"; } catch { }
                }
            }
            else
                okPlace = NativeMethods.TileWindow(w.Hwnd, zones[i], activate: false, fit);
            if (okPlace)
                moved++;
        }

        // Batch show+place+raise EVERY assigned Live HWND (do not leave 1..N-1 under desk).
        AssertAllAssignedLiveTiles(
            keepFocusHwnd: activateZone >= 0 ? (_tiler.GetZone(activateZone)?.Hwnd ?? IntPtr.Zero) : IntPtr.Zero,
            zonesOverride: zones,
            focus: activateZone >= 0);

        // If HardClamp still refused (min-track under-report / Chromium), record actual outer
        // widths, re-HonorAssigned (fair steal from center), and place again once.
        bool anySpill = false;
        for (int i = 0; i < _paneCount; i++)
        {
            var w = _tiler.GetZone(i);
            if (w == null || w.Parked || w.PreviewProxy) continue;
            if (i >= zones.Length || zones[i].IsEmpty) continue;
            if (!NativeMethods.IsWindow(w.Hwnd)) continue;
            if (NativeMethods.VisibleExceedsZone(w.Hwnd, zones[i], tol: 2))
            {
                anySpill = true;
                NativeMethods.GetWindowMinTrackSizeRaw(w.Hwnd, out int rawMin, out _);
                int visW = 0;
                if (NativeMethods.TryGetVisibleFrame(w.Hwnd, out var vf) && vf.Width > 0)
                    visW = vf.Width;
                else if (NativeMethods.GetWindowRect(w.Hwnd, out var cur) && cur.Width > 0)
                    visW = cur.Width;
                if (visW > 0)
                {
                    // Cap at raw min-track (plus small slack), never DWM outer chrome width.
                    int deskW = 0;
                    for (int z = 0; z < zones.Length; z++)
                        if (!zones[z].IsEmpty) deskW += zones[z].Width;
                    int cap = Math.Max(rawMin + 8, Math.Max(zones[i].Width, deskW * 2 / 5));
                    int eff = Math.Max(rawMin, Math.Min(visW, cap));
                    _overflowMinWidth[i] = Math.Max(_overflowMinWidth[i], eff);
                }
            }
            else if (i < _overflowMinWidth.Length)
                _overflowMinWidth[i] = 0; // cleared when in-zone
        }
        if (anySpill)
        {
            zones = ComputePaneScreenZones();
            for (int i = 0; i < _paneCount; i++)
            {
                var w = _tiler.GetZone(i);
                if (w == null || w.Parked || w.PreviewProxy) continue;
                if (i >= zones.Length || zones[i].IsEmpty) continue;
                if (!NativeMethods.IsWindow(w.Hwnd)) continue;
                if (string.Equals(FitModeForZone(i), "fill", StringComparison.OrdinalIgnoreCase))
                {
                    NativeMethods.PlaceFillOrFitWidth(w.Hwnd, zones[i], out _);
                    if (NativeMethods.VisibleExceedsZone(w.Hwnd, zones[i], tol: 2))
                        NativeMethods.HardClampOuterToZone(w.Hwnd, zones[i]);
                }
                else
                    NativeMethods.TileWindow(w.Hwnd, zones[i], activate: false, FitModeForZone(i));
                if (!NativeMethods.VisibleExceedsZone(w.Hwnd, zones[i], tol: 2) && i < _overflowMinWidth.Length)
                    _overflowMinWidth[i] = 0;
            }
            AssertAllAssignedLiveTiles(
                keepFocusHwnd: activateZone >= 0 ? (_tiler.GetZone(activateZone)?.Hwnd ?? IntPtr.Zero) : IntPtr.Zero,
                zonesOverride: zones,
                focus: activateZone >= 0);

            // v1.0.1: still spilling after the steal-from-center re-pass (app refuses its pane) ->
            // collapse it (not mid-drag; the settle pass decides). One retile follows; collapsed
            // panes are skipped by the spill check, so this cannot loop.
            if (!_inSizeMove)
            {
                bool collapsedAny = false;
                for (int i = 0; i < _paneCount; i++)
                {
                    var w = _tiler.GetZone(i);
                    if (w == null || w.Parked || w.PreviewProxy) continue;
                    if (i >= zones.Length || zones[i].IsEmpty) continue;
                    if (!NativeMethods.IsWindow(w.Hwnd)) continue;
                    if (!NativeMethods.VisibleExceedsZone(w.Hwnd, zones[i], tol: 2)) continue;
                    ComputeCollapseNeed(w, i, out int nw, out int nh);
                    CollapseWindow(w, manual: false, Math.Max(nw, zones[i].Width + 1), nh);
                    collapsedAny = true;
                }
                if (collapsedAny)
                    ScheduleLiveRetile();
            }
        }

        // Startup/relink (Tim: Main restored but not sized on open): HonorAssignedMinTracks bakes
        // temporary min-track / spill bumps into the PaneGrid column stars as pixel widths, and every
        // later zone pass reuses those stars. When nothing needs the bump any more, re-apply the
        // saved layout once so the nominal Main/Large/Equal split comes back.
        MaybeRestoreNominalLayoutAfterTransientSpill();

        DumpLargeZoneVsActual(zones);
        VerifyLiveTilesSelfCheck(zones);

        if (anyTooSmall)
        {
            // Prefer per-pane prompt; keep a short status.
            if (_thinExpandHintZone < 0)
                StatusText.Text = "Pane too small";
        }
        else if (_thinExpandHintZone < 0)
        {
            try
            {
                if (BtnExpandPane != null && !_expandedMode)
                    BtnExpandPane.Content = "Expand";
            }
            catch { }
        }

        RefreshAllThumbnails();
    }

    /// <summary>Promote PreviewProxy panes that now fit, without calling ApplyLiveTiling (avoids recursion).</summary>
    private void PromotePreviewIfFitsQuiet()
    {
        // Resize choppiness (Tim): never flip Preview<->Live mid-drag; the WM_EXITSIZEMOVE settle pass decides.
        if (_inSizeMove) return;
        var zones = ComputePaneScreenZones();
        for (int i = 0; i < _paneCount; i++)
        {
            var w = _tiler.GetZone(i);
            if (w == null || !w.PreviewProxy || w.Parked || w.HasCrop) continue;
            if (w.Collapsed || w.Floating) continue; // EvaluateCollapseStates owns re-expand (hysteresis)
            if (i >= zones.Length || zones[i].IsEmpty) continue;
            NativeMethods.GetWindowMinTrackSize(w.Hwnd, out int minW, out int minH);
            // Hysteresis: demotion happens at < min, promotion needs min + 6 % (+8 px) so a pane sitting
            // right at the threshold no longer blinks between Preview and Live on every retile.
            int promoteW = minW + Math.Max(8, minW * 6 / 100);
            int promoteH = minH + Math.Max(8, minH * 6 / 100);
            if (NativeMethods.PaneFitsMin(zones[i], promoteW, promoteH))
            {
                EnterLiveFill(w);
                ShowFitPromptUi(i, false);
            }
        }
    }
    /// <summary>
    /// After pane click / Focus / Assign, Dashboard may sit above sibling Live tiles.
    /// Batch: ShowWindow(SW_SHOWNOACTIVATE) + SetWindowPos into EVERY assigned Live pane rect,
    /// then HWND_TOP all Live tiles above the desk (never cloak/hide non-active panes).
    /// </summary>
    private void RepairLiveVisibilityAndZOrder(IntPtr keepFocusHwnd, NativeMethods.Rect[]? zonesOverride = null)
    {
        AssertAllAssignedLiveTiles(keepFocusHwnd, zonesOverride, focus: keepFocusHwnd != IntPtr.Zero);
    }

    /// <summary>
    /// CRITICAL: keep ALL assigned Live panes visible simultaneously.
    /// Order: (optional) raise desk non-topmost -> show/place/TOPMOST every Live tile ->
    /// optional Focus(target) -> show/place/TOPMOST every Live tile AGAIN.
    /// Never leave RaiseDashboardAboveUnlinked as the last z-order step.
    /// </summary>
    private void AssertAllAssignedLiveTiles(IntPtr keepFocusHwnd, NativeMethods.Rect[]? zonesOverride = null, bool focus = false, bool allowWhenLiveFocused = false, bool forceWhenInactive = false)
    {
        if (!_liveDesk) return;
        if (!IsLoaded || _ownHwnd == IntPtr.Zero) return;
        if (WindowState == WindowState.Minimized || _deskMinimized) return;
        if (_snipYieldActive) return;
        if (DateTime.UtcNow < _chromePopupQuietUntilUtc && !forceWhenInactive) return;
        // Outside-app yield: do not re-TOPMOST linked panes over the user's new window.
        if (_yieldedForOutsideFocus && !IsActive && !forceWhenInactive)
        {
            var liveFg = keepFocusHwnd != IntPtr.Zero ? keepFocusHwnd : NativeMethods.GetForegroundWindow();
            if (!IsAssignedLiveHwnd(liveFg)) return;
        }

        // Outside desk focus: only re-pin when FG (or keepFocus) is an assigned Live tile,
        // unless forceWhenInactive (Docs tab) which keeps panes filled while side panel is used.
        if (!IsActive)
        {
            if (forceWhenInactive && _liveDesk)
            {
                var zonesForce = zonesOverride ?? ComputePaneScreenZones();
                if (zonesForce.Length == 0) return;
                ShowAndRaiseAllLiveTiles(zonesForce);
                SweepStaleLiveTopmostPins();
                SyncLivePaneChromeOverlays();
                // Preview must win z-order after Live TOPMOST re-pin (desk inactive while overlay focused).
                if (_filesOverlayOpen) try { DocsOverlayWindow.RaiseOpenInstanceIfAny(activate: false, forceAboveTiles: true); } catch { }
                return;
            }
            if (!allowWhenLiveFocused) return;
            var liveFg = keepFocusHwnd != IntPtr.Zero ? keepFocusHwnd : NativeMethods.GetForegroundWindow();
            if (!IsAssignedLiveHwnd(liveFg)) return;

            var zonesLive = zonesOverride ?? ComputePaneScreenZones();
            if (zonesLive.Length == 0) return;
            // Keep tiles visible without forcing desk Topmost/activation (avoids thrash).
            ShowAndRaiseAllLiveTiles(zonesLive);
            SweepStaleLiveTopmostPins();
            SyncLivePaneChromeOverlays();
            if (_filesOverlayOpen) try { DocsOverlayWindow.RaiseOpenInstanceIfAny(activate: false, forceAboveTiles: true); } catch { }
            return;
        }

        try { if (!_snipYieldActive && WindowState != WindowState.Minimized) SetDeskTopmostBelowTiles(); } catch { }

        var zones = zonesOverride ?? ComputePaneScreenZones();
        if (zones.Length == 0) return;

        // Desk above ordinary unlinked apps. Tiles pin TOPMOST above desk while Activated.
        // v1.0.2: not during a live resize when the desk is already topmost below its tiles - HWND_TOP would
        // lift it over every tile until each is re-pinned (panes blank for a moment).
        if (!SkipDeskRaiseForResize()) RaiseDashboardAboveUnlinked();
        ShowAndRaiseAllLiveTiles(zones);

        if (focus && keepFocusHwnd != IntPtr.Zero && NativeMethods.IsWindow(keepFocusHwnd))
        {
            NativeMethods.FocusWindow(keepFocusHwnd);
            // After FocusWindow ALWAYS Assert ALL again so siblings are not left under desk.
            ShowAndRaiseAllLiveTiles(zones);
        }

        SweepStaleLiveTopmostPins();
        SyncLivePaneChromeOverlays();
 // Live HWND_TOPMOST pins bury the full-screen Show UI chip '- re-raise it last.
        if (_distractionFree) SyncShowUiOverlay();
        // Quiet re-pin only (no Activate ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬Ãƒâ€šÃ‚Â Activate here caused Preview blink loop).
        if (_filesOverlayOpen) try { DocsOverlayWindow.RaiseOpenInstanceIfAny(activate: false, forceAboveTiles: true); } catch { }
    }

    /// <summary>
    /// For EACH assigned Live HWND: ShowWindow(SW_SHOWNOACTIVATE) + place into pane rect +
    /// SetWindowPos HWND_TOPMOST|SWP_SHOWWINDOW. Desk Topmost=true while restored; tiles pin above.
    /// </summary>
    private void ShowAndRaiseAllLiveTiles(NativeMethods.Rect[] zones)
    {
        List<IntPtr>? floatingPins = null;
        for (int i = 0; i < _paneCount; i++)
        {
            var w = _tiler.GetZone(i);
            if (w == null || w.Parked) continue;
            if (!NativeMethods.IsWindow(w.Hwnd)) continue;
            if (i >= zones.Length || zones[i].IsEmpty) continue;

            // v1.0.1: collapsed tiles stay minimized (TOPMOST cleared at collapse); floating tiles keep
            // their own rect but stay in the Live TOPMOST band above the desk.
            if (w.Collapsed)
                continue;
            if (w.Floating)
            {
                // Pinned after the loop so a floating window always sits above its Live neighbours.
                if (!NativeMethods.IsIconic(w.Hwnd))
                    (floatingPins ??= new List<IntPtr>()).Add(w.Hwnd);
                continue;
            }

            bool cropBusy = CropPreviewEnabled && (w.HasCrop || IsCropInteractionZone(i));
            if (w.PreviewProxy || cropBusy)
            {
                if (cropBusy)
                    ParkCroppedHwnd(w);
                else
                    ClearLiveTopmostTracked(w.Hwnd);
                continue;
            }

            // ALWAYS ShowWindow so siblings never stay cloaked/hidden after focus/Assert.
            NativeMethods.ShowWindow(w.Hwnd,
                NativeMethods.IsIconic(w.Hwnd) ? NativeMethods.SW_RESTORE : NativeMethods.SW_SHOWNOACTIVATE);
            // Docs/pane drag hit-through must never stick (looks like vanished windows).
            if (!_docsFileDragClickThru)
                NativeMethods.SetWindowClickThrough(w.Hwnd, through: false);
            NativeMethods.EnsureTiledVisibleNoActivate(w.Hwnd, zones[i], FitModeForZone(i));
            // FORCE HWND_TOPMOST even if bit already set - desk Raise buries siblings in topmost band.
            PinLiveTopmost(w.Hwnd, force: true);
        }
        if (floatingPins != null)
            foreach (var fh in floatingPins)
                PinLiveTopmost(fh, force: true);
        if (_distractionFree) SyncShowUiOverlay();
    }
    private void RepairAllTilesZOrderAboveDashboard(IntPtr keepFocusHwnd, NativeMethods.Rect[]? zonesOverride = null)
        => AssertAllAssignedLiveTiles(keepFocusHwnd, zonesOverride, focus: keepFocusHwnd != IntPtr.Zero);

    private void Dashboard_Activated(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized || _deskMinimized) return;
        if (_snipYieldActive) { CheckSnipToolYield(); return; }
        // v1.0.2: Topmost=true alone lifts the desk over every TOPMOST tile (panes vanish until re-pinned).
        if (_liveDesk) SetDeskTopmostBelowTiles();
        else try { Topmost = true; } catch { }
        _yieldedForOutsideFocus = false;
        // Sync re-pin immediately ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬Ãƒâ€šÃ‚Â BeginInvoke-only left tiles under the opaque desk (black panes).
        // v1.0.2: during chrome-quiet (Color strip / swatch clicks) AssertAllAssignedLiveTiles returns early,
        // but Topmost=true above has already lifted the desk over the TOPMOST tiles = blank panes. Re-raise
        // the siblings together (quiet-bypassing helper) whenever one ended up below the desk.
        bool chromeQuiet = DateTime.UtcNow < _chromePopupQuietUntilUtc || _colorStripOpen;
        try
        {
            if (_liveDesk)
            {
                if (chromeQuiet) ReassertLiveTilesIfBuried();
                else AssertAllAssignedLiveTiles(IntPtr.Zero, focus: false);
            }
            else
                RaiseDashboardAboveUnlinked();
        }
        catch { }
        Dispatcher.BeginInvoke(() =>
        {
            if (WindowState == WindowState.Minimized || _deskMinimized) return;
            if (_snipYieldActive) return;
            if (!IsActive) return;
            if (_liveDesk)
            {
                if (DateTime.UtcNow < _chromePopupQuietUntilUtc || _colorStripOpen) ReassertLiveTilesIfBuried();
                else AssertAllAssignedLiveTiles(IntPtr.Zero, focus: false);
            }
            else
                RaiseDashboardAboveUnlinked();
        }, DispatcherPriority.Loaded);
    }

    /// <summary>
    /// CRITICAL: when Tim leaves the desk for an unassigned window, drop Live HWND_TOPMOST
    /// so normal desktop z-order returns (Chrome not stuck behind Opera Live tile).
    /// If the new foreground HWND is an assigned Live tile (e.g. tiled YouTube), do NOT clear
 // / topmost '- optionally re-assert tiles so panes stay. Re-assert on Activated for desk FG.
    /// </summary>
    private void Dashboard_Deactivated(object? sender, EventArgs e)
    {
        try { CheckSnipToolYield(); } catch { }
        // Color menu / brief chrome popups: do not Yield (gray panes) or Assert-thrash.
        if (DateTime.UtcNow < _chromePopupQuietUntilUtc)
        {
            try { Topmost = false; } catch { }
            return;
        }
        // Docs quiet / Preview overlay / Docs sidebar / same-process FG: never Yield.
        if (IsDocsChromeProtecting())
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (_snipYieldActive || WindowState == WindowState.Minimized || _deskMinimized) return;
                if (_liveDesk)
                    AssertAllAssignedLiveTiles(IntPtr.Zero, focus: false, allowWhenLiveFocused: true, forceWhenInactive: true);
            }, DispatcherPriority.Input);
            return;
        }
        if (_colorStripOpen)
        {
            // Do NOT Topmost=true the opaque desk — that buries Linked tiles under chrome.
            // Keep tiles force-pinned above desk; strip stays hit-testable in TopChrome (tiles retiled below).
            try { Topmost = false; } catch { }
            Dispatcher.BeginInvoke(() =>
            {
                if (!_colorStripOpen || _snipYieldActive) return;
                if (WindowState == WindowState.Minimized || _deskMinimized) return;
                try { RaiseDeskAboveLiveForChrome(); } catch { }
            }, DispatcherPriority.Input);
            return;
        }
        if (_snipYieldActive) return;

        // Docs tab is desk chrome ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã¢â‚¬Â ÃƒÂ¢Ã¢â€šÂ¬Ã¢â€žÂ¢ÃƒÆ’Ã†â€™ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã¢â‚¬Â¦Ãƒâ€šÃ‚Â¡ÃƒÆ’Ã†â€™ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬Ãƒâ€¦Ã‚Â¡ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã†â€™ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â never blank Live panes while using it.
        if (_docsMode)
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (_snipYieldActive || WindowState == WindowState.Minimized || _deskMinimized) return;
                if (!_liveDesk) return;
                AssertAllAssignedLiveTiles(IntPtr.Zero, focus: false, allowWhenLiveFocused: true, forceWhenInactive: true);
            }, DispatcherPriority.Input);
            return;
        }

        var fg = NativeMethods.GetForegroundWindow();
        if (IsAssignedLiveHwnd(fg))
        {
 // Interacting with a Live-assigned pane '- keep TOPMOST pins; desk can drop WPF Topmost.
            try { Topmost = false; } catch { }
            Dispatcher.BeginInvoke(() =>
            {
                if (_snipYieldActive || IsActive) return;
                if (WindowState == WindowState.Minimized || _deskMinimized) return;
                var fg2 = NativeMethods.GetForegroundWindow();
                if (IsAssignedLiveHwnd(fg2))
                    AssertAllAssignedLiveTiles(fg2, focus: false, allowWhenLiveFocused: true);
                else if (!IsActive)
                {
                    // Race: FG moved to unassigned after deactivate ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬Ãƒâ€šÃ‚Â yield z-order.
                    if (LiveLinkGuard.ShouldSuppressOutsideYield(DateTime.UtcNow, _paneClickQuietUntilUtc, _linkQuietUntilUtc, _linkInteractionQuietUntilUtc)) return;
                    YieldLiveZOrderForOutsideFocus();
                }
            }, DispatcherPriority.Input);
            return;
        }

 // FG may still briefly report desk during Deactivated '- defer the clear decision.
        if (fg == IntPtr.Zero || fg == _ownHwnd)
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (_snipYieldActive || IsActive) return;
                if (WindowState == WindowState.Minimized || _deskMinimized) return;
                var fg2 = NativeMethods.GetForegroundWindow();
                if (IsAssignedLiveHwnd(fg2))
                {
                    try { Topmost = false; } catch { }
                    AssertAllAssignedLiveTiles(fg2, focus: false, allowWhenLiveFocused: true);
                    return;
                }
                if (LiveLinkGuard.ShouldSuppressOutsideYield(DateTime.UtcNow, _paneClickQuietUntilUtc, _linkQuietUntilUtc, _linkInteractionQuietUntilUtc)) return;
                if (DateTime.UtcNow < _chromePopupQuietUntilUtc) return;
                YieldLiveZOrderForOutsideFocus();
            }, DispatcherPriority.Input);
            return;
        }

        // Pane-click Focus handoff: never Clear TOPMOST mid-transition (blank panes).
        if (LiveLinkGuard.ShouldSuppressOutsideYield(DateTime.UtcNow, _paneClickQuietUntilUtc, _linkQuietUntilUtc, _linkInteractionQuietUntilUtc))
        {
            try { Topmost = false; } catch { }
            Dispatcher.BeginInvoke(() =>
            {
                if (_snipYieldActive || IsActive) return;
                if (WindowState == WindowState.Minimized || _deskMinimized) return;
                var fg2 = NativeMethods.GetForegroundWindow();
                if (IsAssignedLiveHwnd(fg2))
                    AssertAllAssignedLiveTiles(fg2, focus: false, allowWhenLiveFocused: true);
            }, DispatcherPriority.Input);
            return;
        }

 // Same-process FG (Docs search, side panel, etc.) is still the desk '- never drop Live pins.
        try
        {
            NativeMethods.GetWindowThreadProcessId(fg, out uint fgPid);
            if (fgPid == (uint)Environment.ProcessId)
            {
                // Desk chrome (Docs sidebar, menus, Preview): never Yield ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÂ¢Ã¢â€šÂ¬Ã‚Â Assert so panes stay filled.
                try { Topmost = false; } catch { }
                Dispatcher.BeginInvoke(() =>
                {
                    if (_snipYieldActive || WindowState == WindowState.Minimized || _deskMinimized) return;
                    if (_liveDesk)
                        AssertAllAssignedLiveTiles(IntPtr.Zero, focus: false, allowWhenLiveFocused: true, forceWhenInactive: true);
                }, DispatcherPriority.Input);
                return;
            }
        }
        catch { }

 // Docs lily pad is desk chrome '- interacting with it must not blank Live panes.
        if (_docsMode)
        {
            try { Topmost = false; } catch { }
            Dispatcher.BeginInvoke(() =>
            {
                if (_snipYieldActive || IsActive) return;
                if (WindowState == WindowState.Minimized || _deskMinimized) return;
                if (_liveDesk) AssertAllAssignedLiveTiles(IntPtr.Zero, focus: false, allowWhenLiveFocused: true);
            }, DispatcherPriority.Input);
            return;
        }

        // True outside-desk work (unassigned window) ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬Ãƒâ€šÃ‚Â yield so new apps can sit on top.
        YieldLiveZOrderForOutsideFocus();
    }

    private void Dashboard_StateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
        {
            _deskMinimized = true;
            try { Topmost = false; } catch { }
            // Minimize ALL linked Live HWNDs together; keep assignments (do NOT Unassign).
            MinimizeAllLinkedLiveHwnds();
            ClearAllLiveTopmost(force: true);
            try { StatusText.Text = "Minimized"; } catch { }

            return;
        }

        if (!_deskMinimized) return;
        _deskMinimized = false;

        Dispatcher.BeginInvoke(() =>
        {
            if (WindowState == WindowState.Minimized) return;
            try { Topmost = true; } catch { }
            RaiseDashboardAboveUnlinked();
            if (_liveDesk)
            {
                RestoreAllLinkedLiveHwnds();
                ApplyLiveTiling();
                AssertAllAssignedLiveTiles(IntPtr.Zero, focus: false);
            }
            else
                RefreshAllThumbnails();
            try { StatusText.Text = "Restored"; } catch { }
        }, DispatcherPriority.Loaded);
    }

    /// <summary>SW_MINIMIZE every assigned Live HWND. Assignments stay; do not Unassign.</summary>
    private void MinimizeAllLinkedLiveHwnds()
    {
        foreach (var w in _tiler.WorkingSet.ToList())
        {
            if (w.Parked) continue;
            if (w.ZoneIndex < 0 || w.ZoneIndex >= _paneCount) continue;
            if (!NativeMethods.IsWindow(w.Hwnd)) continue;
            try
            {
                NativeMethods.ClearLiveTopmost(w.Hwnd);
                if (!NativeMethods.IsIconic(w.Hwnd))
                    NativeMethods.ShowWindow(w.Hwnd, NativeMethods.SW_MINIMIZE);
            }
            catch { }
        }
    }

    /// <summary>SW_RESTORE linked Live HWNDs after desk restore (before retile / Assert).</summary>
    private void RestoreAllLinkedLiveHwnds()
    {
        foreach (var w in _tiler.WorkingSet.ToList())
        {
            if (w.Parked) continue;
            if (w.Collapsed) continue; // stays minimized behind its pane card
            if (w.ZoneIndex < 0 || w.ZoneIndex >= _paneCount) continue;
            if (!NativeMethods.IsWindow(w.Hwnd)) continue;
            try
            {
                if (NativeMethods.IsIconic(w.Hwnd))
                    NativeMethods.ShowWindow(w.Hwnd, NativeMethods.SW_RESTORE);
            }
            catch { }
        }
    }

    /// <summary>
    /// Desk above ordinary unlinked apps. Desk stays Topmost=true while restored;
    /// Live assigned tiles use HWND_TOPMOST above the desk.
 // / Caller MUST re-raise ALL Live tiles after this '- never leave desk as last raise.
    /// </summary>
    private void RaiseDashboardAboveUnlinked()
    {
        if (_ownHwnd == IntPtr.Zero || !NativeMethods.IsWindow(_ownHwnd)) return;
        if (WindowState == WindowState.Minimized || _deskMinimized)
        {
            if (Topmost) Topmost = false;
            return;
        }
        if (_snipYieldActive)
        {
            if (Topmost) Topmost = false;
            return;
        }
        // While Tim is in Chrome/other app, do not re-assert desk Topmost.
        if (!IsActive)
        {
            if (Topmost) Topmost = false;
            return;
        }
        // Keep WPF Topmost while Activated so desk stays above ordinary apps.
        // v1.0.2: with Linked tiles, stay topmost but directly BELOW them (HWND_TOP covered every pane until
        // each tile was re-pinned cross-process = the "all panes disappear" blink).
        if (_liveDesk && TopmostLiveTiles().Count > 0) { SetDeskTopmostBelowTiles(); return; }
        try { Topmost = true; } catch { }
        NativeMethods.SetWindowPos(
            _ownHwnd,
            NativeMethods.HWND_TOP,
            0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
    }


    private static readonly HashSet<string> SnipProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "ScreenSketch",
        "SnippingTool",
        "ScreenClippingHost"
    };

    /// <summary>
    /// Win+Shift+S / Snipping Tool UI is excluded from the window list but still needs
    /// to appear above the desk. While any snip process has a visible window (or is
    /// foreground), yield desk Topmost and clear linked TOPMOST; restore when snip ends.
    /// </summary>
    private void CheckSnipToolYield()
    {
        try
        {
            if (WindowState == WindowState.Minimized || _deskMinimized)
            {
                if (_snipYieldActive)
                {
                    _snipYieldActive = false;
                    _snipStablePlaced = false;
                }
                return;
            }

            bool snipUp = IsSnipToolUiPresent();
            if (snipUp && !_snipYieldActive)
            {
                _snipYieldActive = true;
                _snipStablePlaced = false;
                _yieldedForOutsideFocus = true; // SoftRefresh must not re-TOPMOST over snip UI
                try { Topmost = false; } catch { }
                if (_ownHwnd != IntPtr.Zero && NativeMethods.IsWindow(_ownHwnd))
                {
                    try
                    {
                        NativeMethods.ClearExTopmostKeepZOrder(_ownHwnd);
                        NativeMethods.SetWindowPos(
                            _ownHwnd, NativeMethods.HWND_NOTOPMOST, 0, 0, 0, 0,
                            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
                    }
                    catch { }
                }
                // Hands-off Live tiles during snip (no SetWindowPos / ClearLiveTopmost / Ensure).
                // Only desk demoted above; raise Snip UI itself.
                RaiseSnipWindowsTopmost();
                _snipStablePlaced = true;
                try { StatusText.Text = "Snipping Tool"; } catch { }
            }
            else if (!snipUp && _snipYieldActive)
            {
                _snipYieldActive = false;
                _snipStablePlaced = false;
                if (WindowState == WindowState.Minimized || _deskMinimized) return;
                _yieldedForOutsideFocus = false;
                // v1.0.2: only an ACTIVE desk re-takes Topmost (+ full re-pin of every sibling). An inactive
                // desk used to flip Topmost=true -> NOTOPMOST here, landing on top of the normal band above
                // tiles without TOPMOST (pane 2 vanished). Inactive: keep z-order, slide desk under any
                // buried tile; if a Linked tile is foreground, re-pin all siblings around it.
                if (IsActive)
                {
                    try { Topmost = true; } catch { }
                    RaiseDashboardAboveUnlinked();
                    if (_liveDesk)
                        RepairAllTilesZOrderAboveDashboard(keepFocusHwnd: IntPtr.Zero);
                }
                else if (_liveDesk)
                {
                    var fgNow = NativeMethods.GetForegroundWindow();
                    if (IsAssignedLiveHwnd(fgNow))
                        AssertAllAssignedLiveTiles(fgNow, focus: false, allowWhenLiveFocused: true);
                    LowerDeskBelowBuriedTiles();
                }
                try { StatusText.Text = "Snip closed"; } catch { }
            }
            else if (snipUp && _snipYieldActive)
            {
                // Stay yielded. SoftRefresh is noop once stable — only keep desk non-topmost.
                if (Topmost) { try { Topmost = false; } catch { } }
            }
        }
        catch { /* best-effort */ }
    }

    private bool IsSnipToolUiPresent()
    {
        var fg = NativeMethods.GetForegroundWindow();
        if (IsSnipHwnd(fg))
            return true;

        foreach (var name in SnipProcessNames)
        {
            Process[] procs;
            try { procs = Process.GetProcessesByName(name); }
            catch { continue; }
            try
            {
                foreach (var p in procs)
                {
                    bool found = false;
                    int pid = p.Id;
                    NativeMethods.EnumWindows((h, _) =>
                    {
                        NativeMethods.GetWindowThreadProcessId(h, out uint wpid);
                        if (wpid != (uint)pid) return true;
                        if (!NativeMethods.IsWindowVisible(h)) return true;
                        if (NativeMethods.IsIconic(h)) return true;
                        // Require some on-screen size so zombie 0x0 hosts do not stick yield.
                        if (NativeMethods.GetWindowRect(h, out var r) && (r.Width < 8 || r.Height < 8))
                            return true;
                        found = true;
                        return false;
                    }, IntPtr.Zero);
                    if (found) return true;
                }
            }
            finally
            {
                foreach (var p in procs) p.Dispose();
            }
        }

        // Title fallback (snip UI under unexpected process names)
        bool titled = false;
        NativeMethods.EnumWindows((h, _) =>
        {
            if (!NativeMethods.IsWindowVisible(h) || NativeMethods.IsIconic(h)) return true;
            int len = NativeMethods.GetWindowTextLength(h);
            if (len <= 0) return true;
            var sb = new System.Text.StringBuilder(len + 1);
            NativeMethods.GetWindowText(h, sb, sb.Capacity);
            var title = sb.ToString();
            if (title.Contains("Snipping Tool", StringComparison.OrdinalIgnoreCase) ||
                title.Contains("Screen Sketch", StringComparison.OrdinalIgnoreCase) ||
                title.Equals("Snip & Sketch", StringComparison.OrdinalIgnoreCase))
            {
                titled = true;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return titled;
    }

    private static bool IsSnipHwnd(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd)) return false;
        NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0) return false;
        try
        {
            using var p = Process.GetProcessById((int)pid);
            if (SnipProcessNames.Contains(p.ProcessName))
                return true;
        }
        catch { }

        int len = NativeMethods.GetWindowTextLength(hwnd);
        if (len > 0)
        {
            var sb = new System.Text.StringBuilder(len + 1);
            NativeMethods.GetWindowText(hwnd, sb, sb.Capacity);
            var title = sb.ToString();
            if (title.Contains("Snipping Tool", StringComparison.OrdinalIgnoreCase) ||
                title.Contains("Screen Sketch", StringComparison.OrdinalIgnoreCase) ||
                title.Equals("Snip & Sketch", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private void RaiseSnipWindowsTopmost()
    {
        foreach (var name in SnipProcessNames)
        {
            Process[] procs;
            try { procs = Process.GetProcessesByName(name); }
            catch { continue; }
            try
            {
                foreach (var p in procs)
                {
                    int pid = p.Id;
                    NativeMethods.EnumWindows((h, _) =>
                    {
                        NativeMethods.GetWindowThreadProcessId(h, out uint wpid);
                        if (wpid != (uint)pid) return true;
                        if (!NativeMethods.IsWindowVisible(h) || NativeMethods.IsIconic(h)) return true;
                        NativeMethods.SetWindowPos(
                            h,
                            NativeMethods.HWND_TOPMOST,
                            0, 0, 0, 0,
                            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
                        return true;
                    }, IntPtr.Zero);
                }
            }
            finally
            {
                foreach (var p in procs) p.Dispose();
            }
        }

        var fg = NativeMethods.GetForegroundWindow();
        if (IsSnipHwnd(fg))
        {
            NativeMethods.SetWindowPos(
                fg,
                NativeMethods.HWND_TOPMOST,
                0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
        }
    }

    /// <summary>While dragging a Docs file onto a Live pane, let drops pass through TOPMOST tiles to WPF.</summary>
    private void BeginDocsFileDragHitThrough()
    {
        if (!_liveDesk || _docsFileDragClickThru) return;
        _docsFileDragClickThru = true;
        try
        {
            foreach (var w in _tiler.WorkingSet)
            {
                if (w.Parked) continue;
                if (w.ZoneIndex < 0 || w.ZoneIndex >= _paneCount) continue;
                if (!NativeMethods.IsWindow(w.Hwnd)) continue;
                NativeMethods.SetWindowClickThrough(w.Hwnd, through: true);
            }
        }
        catch { }
    }

    private void EndDocsFileDragHitThrough()
    {
        if (!_docsFileDragClickThru) return;
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
        KeepLivePanesAfterDocs();
    }
    /// <summary>
    /// Capture/yield visibility. Desk Topmost=false; each assigned Live tile: Show + classic
    /// SetWindowPos into zone + HWND_TOP (never Hide, never TOPMOST during snip).
    /// healOnly: only if iconic/invisible. SoftRefresh while snip-stable should not call this.
    /// </summary>
    private void EnsureLiveTilesVisibleForCapture(bool healOnly = false)
    {
        if (!_liveDesk) return;
        if (WindowState == WindowState.Minimized || _deskMinimized) return;
        // Snip hands-off: never SetWindowPos / SetLiveTopmost / ClearLiveTopmost on tiles.
        if (_snipYieldActive)
        {
            try { Topmost = false; } catch { }
            return;
        }

        // Snip + outside yield: HWND_TOP above demoted desk — never TOPMOST (fights Snip UI).
        // Desk demote: once at snip start (healOnly=false); SoftRefresh heal skips if already non-topmost.
        bool needDeskDemote = !healOnly;
        if (healOnly)
        {
            try { if (Topmost) needDeskDemote = true; } catch { needDeskDemote = true; }
            if (!needDeskDemote && _ownHwnd != IntPtr.Zero && NativeMethods.IsWindow(_ownHwnd)
                && NativeMethods.IsWindowTopmost(_ownHwnd))
                needDeskDemote = true;
        }
        if (needDeskDemote)
        {
            try { Topmost = false; } catch { }
            if (_ownHwnd != IntPtr.Zero && NativeMethods.IsWindow(_ownHwnd))
            {
                try
                {
                    NativeMethods.ClearExTopmostKeepZOrder(_ownHwnd);
                    NativeMethods.SetWindowPos(
                        _ownHwnd, NativeMethods.HWND_NOTOPMOST, 0, 0, 0, 0,
                        NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
                }
                catch { }
            }
        }

        NativeMethods.Rect[] zones;
        try { zones = ComputePaneScreenZones(); }
        catch { zones = Array.Empty<NativeMethods.Rect>(); }

        try
        {
            for (int i = 0; i < _paneCount; i++)
            {
                var w = _tiler.GetZone(i);
                if (w == null || w.Parked) continue;
                if (w.PreviewProxy) continue;
                if (!NativeMethods.IsWindow(w.Hwnd)) continue;

                bool iconic = NativeMethods.IsIconic(w.Hwnd);
                bool invisible = !NativeMethods.IsWindowVisible(w.Hwnd);

                // healOnly: only act if actually gone — SoftRefresh must not thrash z-order/rects.
                if (healOnly && !iconic && !invisible)
                    continue;

                if (!_docsFileDragClickThru)
                    NativeMethods.SetWindowClickThrough(w.Hwnd, through: false);

                if (iconic || invisible)
                    NativeMethods.ShowWindow(w.Hwnd, NativeMethods.SW_SHOWNOACTIVATE);

                if (i < zones.Length && !zones[i].IsEmpty)
                {
                    // Classic outer fill (ForceFillVisibleFrame → SetWindowPos zone exactly).
                    NativeMethods.EnsureTiledVisibleNoActivate(
                        w.Hwnd, zones[i], FitModeForZone(i), raiseZOrder: true);
                }

                // Always HWND_TOP (not TOPMOST) so all three siblings stay co-visible above desk.
                NativeMethods.ClearLiveTopmost(w.Hwnd);
                _liveTopmostPinned.Remove(w.Hwnd);
                NativeMethods.SetWindowPos(
                    w.Hwnd, NativeMethods.HWND_TOP, 0, 0, 0, 0,
                    NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
            }
        }
        catch { }
    }

    /// <summary>Legacy name — routes to EnsureLiveTilesVisibleForCapture (HWND_TOP, not TOPMOST).</summary>
    private void SoftRaiseLiveTilesAboveDesk() => EnsureLiveTilesVisibleForCapture();

    private void ClearAllLiveTopmost(bool force = false)
    {
        // While Live is on, prefer YieldLiveZOrderForOutsideFocus (keeps panes above desk).
        // force=true for snip yield / minimize / Live-off.
        if (_liveDesk && !force) return;

        foreach (var w in _tiler.WorkingSet)
        {
            if (NativeMethods.IsWindow(w.Hwnd))
                ClearLiveTopmostTracked(w.Hwnd);
        }
        SweepStaleLiveTopmostPins();
    }
    private void EnsureLiveTilesTopmostIfNeeded()
    {
        if (!_liveDesk) return;
        if (DateTime.UtcNow < _chromePopupQuietUntilUtc) return;
        var fg = NativeMethods.GetForegroundWindow();
        bool protect = IsDocsChromeProtecting();
        if (_yieldedForOutsideFocus && !IsActive && !IsAssignedLiveHwnd(fg) && !protect)
            return;
        bool deskOrLive = IsActive || IsAssignedLiveHwnd(fg);
        if (!deskOrLive && !protect) return;

        NativeMethods.Rect[] zones;
        try { zones = ComputePaneScreenZones(); }
        catch { zones = Array.Empty<NativeMethods.Rect>(); }

        bool needHeal = false;
        int assigned = 0;
        for (int i = 0; i < _paneCount; i++)
        {
            var w = _tiler.GetZone(i);
            if (w == null || w.Parked || w.PreviewProxy) continue;
            if (!NativeMethods.IsWindow(w.Hwnd)) continue;
            assigned++;
            if (NativeMethods.IsIconic(w.Hwnd) || !NativeMethods.IsWindowVisible(w.Hwnd)
                || !NativeMethods.IsWindowTopmost(w.Hwnd))
            {
                needHeal = true;
                break;
            }
            if (i < zones.Length && !zones[i].IsEmpty)
            {
                NativeMethods.GetWindowMinTrackSize(w.Hwnd, out int minW, out int minH);
                bool wantFill = string.Equals(FitModeForZone(i), "fill", StringComparison.OrdinalIgnoreCase);
                bool tooThin = wantFill && minW > zones[i].Width;
                bool rectOk = wantFill
                    ? (tooThin
                        ? NativeMethods.IsOuterAt(w.Hwnd, NativeMethods.ComputeFillOrFitWidthRect(zones[i], minW, minH, out _), tol: 3)
                        // v1.0.2: zones are VISIBLE-frame rects; the outer rect carries ~7 px invisible borders,
                        // so IsOuterAt failed for every correctly placed tile = heal Assert every 800 ms.
                        : (NativeMethods.IsVisiblyAt(w.Hwnd, zones[i], tol: 3) && !NativeMethods.VisibleExceedsZone(w.Hwnd, zones[i], tol: 2)))
                    : NativeMethods.IsVisiblyAt(w.Hwnd, zones[i], tol: 2);
                if (!rectOk) { needHeal = true; break; }
            }
        }
        if (assigned == 0) return;
        // All TOPMOST + visible + rects OK → complete noop (no SetWindowPos / forced pin).
        if (!needHeal) return;

        // Heal once via Assert → ShowAndRaiseAllLiveTiles (forced TOPMOST). Not every SoftRefresh tick.
        if (IsAssignedLiveHwnd(fg))
            AssertAllAssignedLiveTiles(fg, zonesOverride: zones.Length > 0 ? zones : null, focus: false, allowWhenLiveFocused: true);
        else if (protect)
            AssertAllAssignedLiveTiles(IntPtr.Zero, zonesOverride: zones.Length > 0 ? zones : null, focus: false, allowWhenLiveFocused: true, forceWhenInactive: true);
        else
            AssertAllAssignedLiveTiles(IntPtr.Zero, zonesOverride: zones.Length > 0 ? zones : null, focus: false);
    }
private void PinLiveTopmost(IntPtr hwnd, bool force = false)
    {
        if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd)) return;
        // Outside yield: never re-TOPMOST over the user's new window.
        if (_yieldedForOutsideFocus && !IsActive && !_snipYieldActive)
            return;
        NativeMethods.SetLiveTopmost(hwnd, topmost: true, force: force);
        _liveTopmostPinned.Add(hwnd);
    }

    private void ClearLiveTopmostTracked(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        if (NativeMethods.IsWindow(hwnd))
            NativeMethods.ClearLiveTopmost(hwnd);
        _liveTopmostPinned.Remove(hwnd);
    }

    /// <summary>Clear TOPMOST on any HWND we previously pinned that is no longer assigned Live.</summary>
    private void SweepStaleLiveTopmostPins()
    {
        if (_liveTopmostPinned.Count == 0) return;
        var assigned = new HashSet<IntPtr>();
        if (_liveDesk)
        {
            foreach (var w in _tiler.WorkingSet)
            {
                if (w.Parked) continue;
                if (w.ZoneIndex < 0 || w.ZoneIndex >= _paneCount) continue;
                assigned.Add(w.Hwnd);
            }
        }
        var stale = _liveTopmostPinned.Where(h => !assigned.Contains(h)).ToList();
        foreach (var h in stale)
            ClearLiveTopmostTracked(h);
    }

    /// <summary>
    /// One-time per launch: if Chrome/Edge/Opera/Firefox still carry HWND_TOPMOST from a prior
    /// Live session and are not currently assigned, clear it so Tim is not stuck behind them.
    /// </summary>
    private void SweepUnassignedBrowserTopmostOnce()
    {
        if (_didLaunchBrowserTopmostSweep) return;
        _didLaunchBrowserTopmostSweep = true;
        try
        {
            var assigned = new HashSet<IntPtr>();
            foreach (var w in _tiler.WorkingSet)
            {
                if (!w.Parked && w.ZoneIndex >= 0 && w.ZoneIndex < _paneCount)
                    assigned.Add(w.Hwnd);
            }

            string[] browsers = { "chrome", "msedge", "opera", "firefox", "brave", "vivaldi" };
            var shell = NativeMethods.GetShellWindow();
            NativeMethods.EnumWindows((h, _) =>
            {
                if (h == IntPtr.Zero || h == shell || h == _ownHwnd) return true;
                if (assigned.Contains(h)) return true;
                if (!NativeMethods.IsWindowVisible(h) && !NativeMethods.IsIconic(h)) return true;
                if (!NativeMethods.IsWindowTopmost(h)) return true;
                NativeMethods.GetWindowThreadProcessId(h, out uint pid);
                if (pid == 0) return true;
                string proc = "";
                try
                {
                    using var p = Process.GetProcessById((int)pid);
                    proc = p.ProcessName ?? "";
                }
                catch { return true; }
                if (!browsers.Any(b => proc.Equals(b, StringComparison.OrdinalIgnoreCase)))
                    return true;
                NativeMethods.ClearLiveTopmost(h);
                _liveTopmostPinned.Remove(h);
                return true;
            }, IntPtr.Zero);
        }
        catch { /* best-effort */ }
    }

    private void AppendDebug(string msg)
    {
#if DEBUG
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WorkflowDashboard");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "debug.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {msg}{Environment.NewLine}");
        }
        catch { }
#else
        _ = msg;
#endif
    }
    private static string DebugLogPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WorkflowDashboard", "debug.log");

    /// <summary>
    /// One-batch z-order raise for Live tiles only: SetWindowPos HWND_TOP + NOMOVE/NOSIZE/NOACTIVATE.
    /// Never ShowWindow hide/show, never geometry retile, never TOPMOST. Cropped stay parked.
    /// </summary>
    private void RaiseAssignedZOrderOnly()
    {
        AssertAllAssignedLiveTiles(IntPtr.Zero, focus: false);
    }

    private void SyncSideCropButtons()
    {
        // Visible Crop/Uncrop side chrome removed; pane-menu Crop stays (disabled while CropPreviewEnabled=false).
    }

    private void LogPaneClickDebug(string source, IntPtr focusHwnd)
    {
#if DEBUG
        try
        {
            var dir = Path.GetDirectoryName(DebugLogPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var zones = ComputePaneScreenZones();
            var lines = new System.Collections.Generic.List<string>
            {
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {source} layout={_layout} activePane={_activePane} live={_liveDesk} focus=0x{focusHwnd.ToInt64():X}"
            };
            for (int i = 0; i < _paneCount; i++)
            {
                var w = _tiler.GetZone(i);
                if (w == null)
                {
                    lines.Add($"  pane{i + 1}: (unassigned)");
                    continue;
                }
                bool vis = NativeMethods.IsWindowVisible(w.Hwnd);
                bool iconic = NativeMethods.IsIconic(w.Hwnd);
                string rectStr = "?";
                if (NativeMethods.GetWindowRect(w.Hwnd, out var r))
                    rectStr = $"{r.Left},{r.Top},{r.Right},{r.Bottom} ({r.Width}x{r.Height})";
                string zoneStr = (i < zones.Length && !zones[i].IsEmpty)
                    ? $"{zones[i].Left},{zones[i].Top},{zones[i].Right},{zones[i].Bottom}"
                    : "no-zone";
                lines.Add($"  pane{i + 1}: hwnd=0x{w.Hwnd.ToInt64():X} title={w.ShortTitle} visible={vis} iconic={iconic} rect=[{rectStr}] zone=[{zoneStr}] parked={w.Parked}");
            }
            File.AppendAllLines(DebugLogPath, lines);
        }
        catch { /* debug only */ }
#else
        _ = source;
        _ = focusHwnd;
#endif
    }

    private int MinTilePxWidth()
    {
        try
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            return Math.Max(NativeMethods.MinTileWidth, (int)Math.Ceiling(ZoneLayout.MinPaneWidth * dpi.DpiScaleX));
        }
        catch { return NativeMethods.MinTileWidth; }
    }

    private int MinTilePxHeight()
    {
        try
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            return Math.Max(NativeMethods.MinTileHeight, (int)Math.Ceiling(ZoneLayout.MinPaneHeight * dpi.DpiScaleY));
        }
        catch { return NativeMethods.MinTileHeight; }
    }

 // / <summary>Grow window minimum so equal/main/focus cannot create sub-280'--'--'--''--'-'--'-'-'--'--'-200 DIP panes.</summary>
    private void UpdateWindowMinSizeForPanes()
    {
        double side = (_distractionFree || _sideCollapsed) ? 48 : 340; // SidePanel ~300 + margin/border/pad
        double chromeW = 40;
        double chromeH = _distractionFree ? 40 : 140;
        int cols = _paneCount switch { 1 => 1, 2 => 2, _ => 3 };
        int rows = 1;
        // Focus/Main thin column still needs MinPaneWidth.
        double deskMinW = cols * ZoneLayout.MinPaneWidth + (_paneCount >= 2 ? 12 : 0);
        double deskMinH = rows * ZoneLayout.MinPaneHeight + 24;
        MinWidth = Math.Max(980, side + chromeW + deskMinW);
        MinHeight = Math.Max(560, chromeH + deskMinH);
    }

        private NativeMethods.Rect[] ComputePaneScreenZones()
    {
        try { PaneGrid?.UpdateLayout(); } catch { }

        var zones = new NativeMethods.Rect[_paneCount];
        int okCount = 0;
        for (int i = 0; i < _paneCount; i++)
        {
            if (TryGetPaneScreenRect(i, out zones[i]))
                okCount++;
            else
                zones[i] = default;
        }

        // Pane 3 (or any) may report ActualWidth<2 mid-layout — synthesize from neighbors / grid.
        if (okCount > 0 && okCount < _paneCount && _paneCount == 3)
            TryRepairMissingThreePaneZones(zones, ref okCount);

        if (okCount == 0) return Array.Empty<NativeMethods.Rect>();
        SnapSharedEdges(zones);
        // Hard clip: Live tiles must never cover the Docs/Windows sidebar (or overflow desk).
        ClipZonesToDeskClient(zones);
        // After snap/clip, ensure no zero-width pane (pane 3 collapse).
        for (int i = 0; i < zones.Length; i++)
        {
            if (!zones[i].IsEmpty && zones[i].Width < 2)
                zones[i] = default;
        }
        EnsureZonesDisjointInsideDesk(zones);
        // Grow skinny Large/Main sides up to assigned HWND min-track (steal from center).
        // Side floor 340 stays as the minimum; Electron/Opera ~512+ need more or HardClamp cannot shrink.
        HonorAssignedMinTracks(zones);
        MaybeDumpLargeZoneRectsOnce(zones);
        return zones;
    }

    /// <summary>
    /// Screen rect of the Live desk host (PaneGrid). Zones are clipped to this so Large
    /// pane 3 (and any overflow from MinWidth) never covers the right sidebar.
    /// </summary>
    private bool TryGetDeskClientScreenRect(out NativeMethods.Rect desk)
    {
        desk = default;
        try
        {
            if (PaneGrid == null || !PaneGrid.IsVisible) return false;
            if (PaneGrid.ActualWidth < 2 || PaneGrid.ActualHeight < 2) return false;
            Point tl = PaneGrid.PointToScreen(new Point(0, 0));
            Point br = PaneGrid.PointToScreen(new Point(PaneGrid.ActualWidth, PaneGrid.ActualHeight));
            int left = (int)Math.Floor(Math.Min(tl.X, br.X) + 1e-6);
            int top = (int)Math.Floor(Math.Min(tl.Y, br.Y) + 1e-6);
            int right = (int)Math.Ceiling(Math.Max(tl.X, br.X) - 1e-6);
            int bottom = (int)Math.Ceiling(Math.Max(tl.Y, br.Y) - 1e-6);
            if (right - left < 2 || bottom - top < 2) return false;
            desk = new NativeMethods.Rect { Left = left, Top = top, Right = right, Bottom = bottom };
            return true;
        }
        catch { return false; }
    }

    /// <summary>Intersect each zone with PaneGrid screen bounds (never cover sidebar).</summary>
    private void ClipZonesToDeskClient(NativeMethods.Rect[] zones)
    {
        if (zones == null || zones.Length == 0) return;
        if (!TryGetDeskClientScreenRect(out var desk)) return;
        for (int i = 0; i < zones.Length; i++)
        {
            if (zones[i].IsEmpty) continue;
            int l = Math.Max(zones[i].Left, desk.Left);
            int t = Math.Max(zones[i].Top, desk.Top);
            int r = Math.Min(zones[i].Right, desk.Right);
            int b = Math.Min(zones[i].Bottom, desk.Bottom);
            if (r - l < 2 || b - t < 2)
                zones[i] = default;
            else
                zones[i] = new NativeMethods.Rect { Left = l, Top = t, Right = r, Bottom = b };
        }
    }

    private bool _dumpedLargeZonesOnce;
    /// <summary>Per-zone effective min widths after HardClamp refusal (actual outer). Cleared when in-zone.</summary>
    private readonly int[] _overflowMinWidth = new int[4];

    /// <summary>
    /// After side-floor geometry, bump each *assigned* Live zone up to the HWND min-track
    /// (or actual outer width if larger) by stealing from center fairly, then from the other
    /// side if needed. Unassigned panes keep side-floor widths and do NOT inflate the budget.
    /// Also syncs PaneGrid column stars so chrome/overlays match the grown zones (first-link align).
    /// MinSideWidth (340) remains the floor � this only raises sides further when needed.
    /// </summary>
    private void HonorAssignedMinTracks(NativeMethods.Rect[] zones)
    {
        if (zones == null || zones.Length == 0 || _paneCount < 2) return;
        if (!TryGetDeskClientScreenRect(out var desk)) return;
        if (_paneCount == 3 && zones.Length >= 3)
        {
            const int gap = 4;
            int usable = Math.Max(1, desk.Width - 2 * gap);
            int w0 = Math.Max(1, zones[0].IsEmpty ? 1 : zones[0].Width);
            int w1 = Math.Max(1, zones[1].IsEmpty ? 1 : zones[1].Width);
            int w2 = Math.Max(1, zones[2].IsEmpty ? 1 : zones[2].Width);

            int m0 = 0, m1 = 0, m2 = 0; // 0 => unassigned (no forced bump)
            for (int i = 0; i < 3; i++)
            {
                var w = _tiler.GetZone(i);
                if (w == null || w.Parked || w.PreviewProxy) continue;
                if (!NativeMethods.IsWindow(w.Hwnd)) continue;
                NativeMethods.GetWindowMinTrackSizeRaw(w.Hwnd, out int mw, out _);
                if (i >= 0 && i < _overflowMinWidth.Length && _overflowMinWidth[i] > mw)
                    mw = _overflowMinWidth[i];
                if (i == 0) m0 = Math.Max(1, mw);
                else if (i == 1) m1 = Math.Max(1, mw);
                else m2 = Math.Max(1, mw);
            }

            bool skinny = string.Equals(_layout, "large", StringComparison.OrdinalIgnoreCase)
                || string.Equals(_layout, "main", StringComparison.OrdinalIgnoreCase);
            int sideFloor = skinny ? ZoneLayout.MinSideWidth : 1;

            ZoneLayout.ApplyAssignedMinTracksThreePane(ref w0, ref w1, ref w2, usable, m0, m1, m2, sideFloor);

            zones[0] = new NativeMethods.Rect { Left = desk.Left, Top = desk.Top, Right = desk.Left + w0, Bottom = desk.Bottom };
            zones[1] = new NativeMethods.Rect { Left = zones[0].Right + gap, Top = desk.Top, Right = zones[0].Right + gap + w1, Bottom = desk.Bottom };
            zones[2] = new NativeMethods.Rect { Left = zones[1].Right + gap, Top = desk.Top, Right = desk.Right, Bottom = desk.Bottom };

            // Sync WPF columns so pane chrome matches grown zones (initial-link alignment).
            try
            {
                if (PaneGrid != null && PaneGrid.ColumnDefinitions.Count >= 3
                    && (m0 > 0 || m1 > 0 || m2 > 0))
                {
                    PaneGrid.ColumnDefinitions[0].Width = new GridLength(Math.Max(1, w0), GridUnitType.Star);
                    PaneGrid.ColumnDefinitions[1].Width = new GridLength(Math.Max(1, w1), GridUnitType.Star);
                    PaneGrid.ColumnDefinitions[2].Width = new GridLength(Math.Max(1, w2), GridUnitType.Star);
                    PaneGrid.ColumnDefinitions[0].MinWidth = 0;
                    PaneGrid.ColumnDefinitions[1].MinWidth = 0;
                    PaneGrid.ColumnDefinitions[2].MinWidth = 0;
                }
            }
            catch { }
            return;
        }
        if (_paneCount == 2 && zones.Length >= 2)
        {
            const int gap2 = 4;
            int usable2 = Math.Max(1, desk.Width - gap2);
            int a = Math.Max(1, zones[0].IsEmpty ? 1 : zones[0].Width);
            int b = Math.Max(1, zones[1].IsEmpty ? 1 : zones[1].Width);
            int ma = 0, mb = 0;
            for (int i = 0; i < 2; i++)
            {
                var w = _tiler.GetZone(i);
                if (w == null || w.Parked || w.PreviewProxy) continue;
                if (!NativeMethods.IsWindow(w.Hwnd)) continue;
                NativeMethods.GetWindowMinTrackSizeRaw(w.Hwnd, out int mw, out _);
                if (i >= 0 && i < _overflowMinWidth.Length && _overflowMinWidth[i] > mw)
                    mw = _overflowMinWidth[i];
                if (i == 0) ma = Math.Max(1, mw); else mb = Math.Max(1, mw);
            }
            if (ma > a)
            {
                int need = ma - a;
                int floorB = mb > 0 ? mb : 1;
                int take = Math.Min(need, Math.Max(0, b - floorB));
                a += take; b -= take;
            }
            if (mb > b)
            {
                int need = mb - b;
                int floorA = ma > 0 ? ma : 1;
                int take = Math.Min(need, Math.Max(0, a - floorA));
                b += take; a -= take;
            }
            a = Math.Max(1, a); b = Math.Max(1, usable2 - a);
            zones[0] = new NativeMethods.Rect { Left = desk.Left, Top = desk.Top, Right = desk.Left + a, Bottom = desk.Bottom };
            zones[1] = new NativeMethods.Rect { Left = zones[0].Right + gap2, Top = desk.Top, Right = desk.Right, Bottom = desk.Bottom };
            try
            {
                if (PaneGrid != null && PaneGrid.ColumnDefinitions.Count >= 2 && (ma > 0 || mb > 0))
                {
                    PaneGrid.ColumnDefinitions[0].Width = new GridLength(Math.Max(1, a), GridUnitType.Star);
                    PaneGrid.ColumnDefinitions[1].Width = new GridLength(Math.Max(1, b), GridUnitType.Star);
                }
            }
            catch { }
        }
    }




    private void EnsureZonesDisjointInsideDesk(NativeMethods.Rect[] zones)
    {
        if (zones == null || zones.Length == 0 || _paneCount < 2) return;
        if (!TryGetDeskClientScreenRect(out var desk)) return;

        static bool Overlap(NativeMethods.Rect a, NativeMethods.Rect b)
        {
            if (a.IsEmpty || b.IsEmpty) return false;
            return a.Left < b.Right && b.Left < a.Right && a.Top < b.Bottom && b.Top < a.Bottom;
        }

        bool forceSkinny = _paneCount >= 2
            && (string.Equals(_layout, "large", StringComparison.OrdinalIgnoreCase)
                || string.Equals(_layout, "main", StringComparison.OrdinalIgnoreCase));
        bool bad = forceSkinny;
        if (!bad)
        {
            for (int i = 0; i < _paneCount && i < zones.Length; i++)
            {
                if (zones[i].IsEmpty) { bad = true; break; }
                if (zones[i].Left < desk.Left - 1 || zones[i].Right > desk.Right + 1 ||
                    zones[i].Top < desk.Top - 1 || zones[i].Bottom > desk.Bottom + 1)
                { bad = true; break; }
            }
        }
        if (!bad)
        {
            for (int i = 0; i < _paneCount && !bad; i++)
                for (int j = i + 1; j < _paneCount; j++)
                    if (j < zones.Length && Overlap(zones[i], zones[j])) { bad = true; break; }
        }
        if (!bad) return;

        // 2-pane Main/Large: rebuild with thin-side floor (main absorbs).
        if (_paneCount == 2 && zones.Length >= 2
            && (string.Equals(_layout, "large", StringComparison.OrdinalIgnoreCase)
                || string.Equals(_layout, "main", StringComparison.OrdinalIgnoreCase)))
        {
            const int gap2 = 4;
            int usable2 = Math.Max(1, desk.Width - gap2);
            double thinShare = string.Equals(_layout, "large", StringComparison.OrdinalIgnoreCase) ? 0.12 : 0.30;
            bool mainLeft = _mainOnLeft;
            int thinW = Math.Max(1, (int)Math.Round(usable2 * thinShare));
            int mainW = Math.Max(1, usable2 - thinW);
            ZoneLayout.ApplyTwoPaneThinFloor(ref mainW, ref thinW, usable2, ZoneLayout.MinSideWidth);
            if (mainLeft)
            {
                zones[0] = new NativeMethods.Rect { Left = desk.Left, Top = desk.Top, Right = desk.Left + mainW, Bottom = desk.Bottom };
                zones[1] = new NativeMethods.Rect { Left = zones[0].Right + gap2, Top = desk.Top, Right = desk.Right, Bottom = desk.Bottom };
            }
            else
            {
                zones[0] = new NativeMethods.Rect { Left = desk.Left, Top = desk.Top, Right = desk.Left + thinW, Bottom = desk.Bottom };
                zones[1] = new NativeMethods.Rect { Left = zones[0].Right + gap2, Top = desk.Top, Right = desk.Right, Bottom = desk.Bottom };
            }
            try
            {
                if (PaneGrid != null && PaneGrid.ColumnDefinitions.Count >= 2)
                {
                    int a = Math.Max(1, zones[0].Width);
                    int b = Math.Max(1, zones[1].Width);
                    PaneGrid.ColumnDefinitions[0].Width = new GridLength(a, GridUnitType.Star);
                    PaneGrid.ColumnDefinitions[1].Width = new GridLength(b, GridUnitType.Star);
                    PaneGrid.ColumnDefinitions[0].MinWidth = 0;
                    PaneGrid.ColumnDefinitions[1].MinWidth = 0;
                }
            }
            catch { }
            return;
        }

        if (_paneCount != 3 || zones.Length < 3) return;

        double s0 = 1, s1 = 1, s2 = 1;
        try
        {
            if (PaneGrid != null && PaneGrid.ColumnDefinitions.Count >= 3)
            {
                s0 = Math.Max(0.01, PaneGrid.ColumnDefinitions[0].Width.Value);
                s1 = Math.Max(0.01, PaneGrid.ColumnDefinitions[1].Width.Value);
                s2 = Math.Max(0.01, PaneGrid.ColumnDefinitions[2].Width.Value);
            }
            else if (_layout == "large") { s0 = 0.12; s1 = 0.76; s2 = 0.12; }
            else if (_layout == "main") { s0 = 0.22; s1 = 0.56; s2 = 0.22; }
        }
        catch { if (_layout == "large") { s0 = 0.12; s1 = 0.76; s2 = 0.12; } }

        const int gap = 4;
        int usable = Math.Max(1, desk.Width - 2 * gap);
        double sum = s0 + s1 + s2;
        int w0 = Math.Max(1, (int)Math.Round(usable * (s0 / sum)));
        int w1 = Math.Max(1, (int)Math.Round(usable * (s1 / sum)));
        int w2 = Math.Max(1, usable - w0 - w1);
        // Side floor (~340px): sides never thinner; center absorbs. Zones stay disjoint.
        if (string.Equals(_layout, "large", StringComparison.OrdinalIgnoreCase)
            || string.Equals(_layout, "main", StringComparison.OrdinalIgnoreCase))
        {
            ZoneLayout.ApplyThreePaneSideFloor(ref w0, ref w1, ref w2, usable, ZoneLayout.MinSideWidth);
        }
        zones[0] = new NativeMethods.Rect { Left = desk.Left, Top = desk.Top, Right = desk.Left + w0, Bottom = desk.Bottom };
        zones[1] = new NativeMethods.Rect { Left = zones[0].Right + gap, Top = desk.Top, Right = zones[0].Right + gap + w1, Bottom = desk.Bottom };
        zones[2] = new NativeMethods.Rect { Left = zones[1].Right + gap, Top = desk.Top, Right = desk.Right, Bottom = desk.Bottom };
    try
        {
            if (PaneGrid != null && PaneGrid.ColumnDefinitions.Count >= 3)
            {
                PaneGrid.ColumnDefinitions[0].Width = new GridLength(Math.Max(1, w0), GridUnitType.Star);
                PaneGrid.ColumnDefinitions[1].Width = new GridLength(Math.Max(1, w1), GridUnitType.Star);
                PaneGrid.ColumnDefinitions[2].Width = new GridLength(Math.Max(1, w2), GridUnitType.Star);
                PaneGrid.ColumnDefinitions[0].MinWidth = 0;
                PaneGrid.ColumnDefinitions[1].MinWidth = 0;
                PaneGrid.ColumnDefinitions[2].MinWidth = 0;
            }
        }
        catch { }
    }

    private void MaybeDumpLargeZoneRectsOnce(NativeMethods.Rect[] zones)
    {
        if (_dumpedLargeZonesOnce) return;
        if (!string.Equals(_layout, "large", StringComparison.OrdinalIgnoreCase) || _paneCount != 3) return;
        if (zones == null || zones.Length < 3) return;
        _dumpedLargeZonesOnce = true;
        DumpLargeZoneVsActual(zones);
    }

    /// <summary>
    /// Release-mode breadcrumb when a linked HWND looks gone (hidden, iconic, cloaked,
    /// off-screen, or covered at its center). Written to debug.log only on a hit.
    /// </summary>
    private void LogLinkedVanishEvents()
    {
        try
        {
            var hits = new System.Collections.Generic.List<string>();
            for (int i = 0; i < _paneCount; i++)
            {
                var w = _tiler.GetZone(i);
                if (w == null || w.Parked || w.Collapsed || w.Floating) continue;
                if (!NativeMethods.IsWindow(w.Hwnd)) continue;

                bool vis = NativeMethods.IsWindowVisible(w.Hwnd);
                bool iconic = NativeMethods.IsIconic(w.Hwnd);
                bool cloaked = NativeMethods.IsCloaked(w.Hwnd);
                bool topmost = NativeMethods.IsWindowTopmost(w.Hwnd);
                string rectStr = "?";
                bool offscreen = false;
                NativeMethods.Rect wr = default;
                if (NativeMethods.GetWindowRect(w.Hwnd, out wr))
                {
                    rectStr = $"{wr.Left},{wr.Top},{wr.Right},{wr.Bottom} ({wr.Width}x{wr.Height})";
                    offscreen = wr.IsEmpty || wr.Right <= -10000 || wr.Bottom <= -10000
                        || wr.Left >= 20000 || wr.Top >= 20000;
                }

                string? coveredBy = null;
                if (vis && !iconic && !wr.IsEmpty)
                {
                    var pt = new NativeMethods.POINT { X = (wr.Left + wr.Right) / 2, Y = (wr.Top + wr.Bottom) / 2 };
                    var hit = NativeMethods.WindowFromPoint(pt);
                    if (hit != IntPtr.Zero)
                    {
                        var root = NativeMethods.GetAncestor(hit, NativeMethods.GA_ROOT);
                        if (root == IntPtr.Zero) root = hit;
                        if (root != w.Hwnd && root != _ownHwnd && !IsAssignedLiveHwnd(root)
                            && !IsDocsChromeProtecting())
                            coveredBy = $"0x{root.ToInt64():X}";
                    }
                }

                var reasons = new System.Collections.Generic.List<string>();
                if (!vis) reasons.Add("hidden");
                if (iconic) reasons.Add("iconic");
                if (cloaked) reasons.Add("cloaked");
                if (offscreen) reasons.Add("offscreen");
                if (coveredBy != null) reasons.Add("covered:" + coveredBy);
                if (reasons.Count == 0) continue;
                hits.Add($"pane{i + 1} hwnd=0x{w.Hwnd.ToInt64():X} title={w.ShortTitle} vis={vis} iconic={iconic} cloaked={cloaked} topmost={topmost} yielded={_yieldedForOutsideFocus} reasons={string.Join(",", reasons)} rect=[{rectStr}]");
            }
            if (hits.Count == 0) return;
            var dir = Path.GetDirectoryName(DebugLogPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.AppendAllText(DebugLogPath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] vanish {string.Join(" | ", hits)}{Environment.NewLine}");
        }
        catch { }
    }

    /// <summary>Status: Large live zones vs actual extended-frame rects (overflow detect).</summary>
    private void VerifyLiveTilesSelfCheck(NativeMethods.Rect[] zones)
    {
        // live-diag always on (was DEBUG-only)
        try
        {
            var dir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WorkflowDashboard");
            System.IO.Directory.CreateDirectory(dir);
            var path = System.IO.Path.Combine(dir, "live-diag.txt");
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] VerifyLiveTiles layout={_layout} live={_liveDesk} panes={_paneCount}");
            int assigned = 0, visible = 0, placed = 0;
            bool overlap = false;
            var rects = new NativeMethods.Rect[_paneCount];
            for (int i = 0; i < _paneCount; i++)
            {
                var w = _tiler.GetZone(i);
                string zoneStr = (i < zones.Length && !zones[i].IsEmpty)
                    ? $"{zones[i].Left},{zones[i].Top},{zones[i].Right},{zones[i].Bottom} ({zones[i].Width}x{zones[i].Height})"
                    : "empty";
                if (w == null)
                {
                    sb.AppendLine($"  zone{i}: unassigned  zone=[{zoneStr}]");
                    continue;
                }
                if (w.Collapsed || w.Floating)
                {
                    // v1.0.1: collapsed (card) / floating windows are intentionally not in their zone.
                    sb.AppendLine($"  zone{i}: hwnd=0x{w.Hwnd.ToInt64():X} {(w.Collapsed ? "collapsed" : "floating")}  zone=[{zoneStr}]");
                    continue;
                }
                assigned++;
                bool vis = NativeMethods.IsWindowVisible(w.Hwnd);
                bool iconic = NativeMethods.IsIconic(w.Hwnd);
                bool topmost = NativeMethods.IsWindowTopmost(w.Hwnd);
                string rectStr = "?";
                NativeMethods.Rect wr = default;
                if (NativeMethods.GetWindowRect(w.Hwnd, out wr))
                {
                    rectStr = $"{wr.Left},{wr.Top},{wr.Right},{wr.Bottom} ({wr.Width}x{wr.Height})";
                    rects[i] = wr;
                }
                bool inZone = i < zones.Length && !zones[i].IsEmpty && !NativeMethods.VisibleExceedsZone(w.Hwnd, zones[i], 2);
                if (vis && !iconic) visible++;
                if (inZone) placed++;
                sb.AppendLine($"  zone{i}: hwnd=0x{w.Hwnd.ToInt64():X} vis={vis} iconic={iconic} topmost={topmost} inZone={inZone} rect=[{rectStr}] zone=[{zoneStr}]");
            }
            for (int i = 0; i < _paneCount; i++)
            for (int j = i + 1; j < _paneCount; j++)
            {
                if (rects[i].IsEmpty || rects[j].IsEmpty) continue;
                bool hit = rects[i].Left < rects[j].Right && rects[j].Left < rects[i].Right
                    && rects[i].Top < rects[j].Bottom && rects[j].Top < rects[i].Bottom;
                if (hit)
                {
                    overlap = true;
                    int iw = Math.Min(rects[i].Right, rects[j].Right) - Math.Max(rects[i].Left, rects[j].Left);
                    int ih = Math.Min(rects[i].Bottom, rects[j].Bottom) - Math.Max(rects[i].Top, rects[j].Top);
                    sb.AppendLine($"  OVERLAP zone{i}xzone{j} area={iw}x{ih}");
                }
            }
            bool zonesDisjoint = true;
            for (int i = 0; i < zones.Length; i++)
            for (int j = i + 1; j < zones.Length; j++)
            {
                if (zones[i].IsEmpty || zones[j].IsEmpty) continue;
                if (zones[i].Left < zones[j].Right && zones[j].Left < zones[i].Right
                    && zones[i].Top < zones[j].Bottom && zones[j].Top < zones[i].Bottom)
                { zonesDisjoint = false; sb.AppendLine($"  ZONE_OVERLAP {i}x{j}"); }
            }
            bool pass = assigned == 0
                || (visible == assigned && !overlap && zonesDisjoint && placed == assigned);
            string verdict = pass ? "PASS" : "FAIL";
            sb.AppendLine($"RESULT {verdict} assigned={assigned} visible={visible} inZone={placed} winOverlap={overlap} zonesDisjoint={zonesDisjoint}");
            System.IO.File.WriteAllText(path, sb.ToString());
            LogLinkedVanishEvents();
            if (_liveDiagVisible) // v1.0.1: hidden from the normal UI (Ctrl+Shift+F12 / WD_LIVEDIAG=1)
                try { StatusText.Text = $"LiveDiag {verdict} v={visible}/{assigned} zOk={zonesDisjoint} winOverlap={overlap}"; } catch { }
        }
        catch (Exception ex)
        {
            try
            {
                var path = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "WorkflowDashboard", "live-diag.txt");
                System.IO.File.WriteAllText(path, "ERROR " + ex);
            }
            catch { }
        }
    }

    private void DumpLargeZoneVsActual(NativeMethods.Rect[] zones)
    {
        // live-diag always on (was DEBUG-only)
        if (!string.Equals(_layout, "large", StringComparison.OrdinalIgnoreCase) || _paneCount != 3) return;
        if (zones == null || zones.Length < 3) return;
        try
        {
            string Fmt(NativeMethods.Rect r) => r.IsEmpty ? "empty" : (r.Width + "x" + r.Height + "@" + r.Left);
            var parts = new System.Collections.Generic.List<string>();
            bool spill = false;
            for (int i = 0; i < 3; i++)
            {
                string z = Fmt(zones[i]);
                string a = "?";
                var w = _tiler.GetZone(i);
                if (w != null && NativeMethods.IsWindow(w.Hwnd) && NativeMethods.TryGetVisibleFrame(w.Hwnd, out var fr) && !fr.IsEmpty)
                {
                    a = Fmt(fr);
                    if (!zones[i].IsEmpty && (fr.Width > zones[i].Width + 4 || fr.Height > zones[i].Height + 4
                        || fr.Left < zones[i].Left - 4 || fr.Right > zones[i].Right + 4))
                        spill = true;
                }
                else if (w == null) a = "none";
                parts.Add("P" + (i + 1) + " z" + z + " a" + a);
            }
            StatusText.Text = (spill ? "Large OVERFLOW " : "Large ok ") + string.Join(" | ", parts);
        }
        catch { }
    }


    /// <summary>
    /// When pane 3 (or mid) fails PointToScreen during resize, fill from LTR neighbors
    /// so Live retile still gets a valid non-zero rect for every visible column.
    /// </summary>
    private void TryRepairMissingThreePaneZones(NativeMethods.Rect[] zones, ref int okCount)
    {
        if (zones.Length < 3) return;

        // Prefer synthesizing from PaneGrid using Equal/Main/Large column shares so
        // zone index 2 (visual pane 3) gets the correct width — not max(neighbor).
        try
        {
            if (PaneGrid != null && PaneGrid.IsVisible
                && PaneGrid.ActualWidth >= 10 && PaneGrid.ActualHeight >= 10
                && PaneGrid.ColumnDefinitions.Count >= 3)
            {
                var tl = PaneGrid.PointToScreen(new Point(0, 0));
                var br = PaneGrid.PointToScreen(new Point(PaneGrid.ActualWidth, PaneGrid.ActualHeight));
                int left = (int)Math.Floor(Math.Min(tl.X, br.X));
                int top = (int)Math.Floor(Math.Min(tl.Y, br.Y));
                int right = (int)Math.Ceiling(Math.Max(tl.X, br.X));
                int bottom = (int)Math.Ceiling(Math.Max(tl.Y, br.Y));
                int totalW = Math.Max(1, right - left);
                double s0 = Math.Max(0.01, PaneGrid.ColumnDefinitions[0].Width.Value);
                double s1 = Math.Max(0.01, PaneGrid.ColumnDefinitions[1].Width.Value);
                double s2 = Math.Max(0.01, PaneGrid.ColumnDefinitions[2].Width.Value);
                double sum = s0 + s1 + s2;
                int gap = 4;
                int usable = Math.Max(1, totalW - 2 * gap);
                int w0 = Math.Max(1, (int)Math.Round(usable * (s0 / sum)));
                int w1 = Math.Max(1, (int)Math.Round(usable * (s1 / sum)));
                int w2 = Math.Max(1, usable - w0 - w1);
                int[] widths = { w0, w1, w2 };
                int x = left;
                for (int i = 0; i < 3; i++)
                {
                    int l = x;
                    int r = (i == 2) ? right : (l + widths[i]);
                    // Never extend past desk right (sidebar protect).
                    if (r > right) r = right;
                    if (l < left) l = left;
                    if (zones[i].IsEmpty && r > l)
                    {
                        zones[i] = new NativeMethods.Rect { Left = l, Top = top, Right = r, Bottom = bottom };
                        if (!zones[i].IsEmpty) okCount++;
                    }
                    x = r + gap;
                    if (x > right) x = right;
                }
                return;
            }
        }
        catch { }

        // Fallback: if 0 and 1 ok but 2 missing, fill remaining desk width to the right of pane 1.
        if (!zones[0].IsEmpty && !zones[1].IsEmpty && zones[2].IsEmpty)
        {
            int gap = Math.Max(0, zones[1].Left - zones[0].Right);
            int left2 = zones[1].Right + gap;
            int right2 = left2 + Math.Max(1, zones[1].Width);
            if (TryGetDeskClientScreenRect(out var desk))
                right2 = Math.Max(left2 + 1, desk.Right);
            zones[2] = new NativeMethods.Rect
            {
                Left = left2,
                Top = zones[1].Top,
                Right = right2,
                Bottom = zones[1].Bottom
            };
            if (!zones[2].IsEmpty) okCount++;
        }
    }
/// <summary>
    /// Physical screen pixels for a pane. Full pane TileHost (no bottom Crop chrome).
    /// Zone index == visual pane (stable LTR; no Focus/Flip remapping).
    /// </summary>
    private bool TryGetPaneScreenRect(int index, out NativeMethods.Rect rect)
    {
        rect = default;
        try
        {
            if (index < 0 || index >= _slots.Length) return false;
            var root = _slots[index].Root;
            // Full pane frame (no bottom Crop chrome). Prefer TileHost/ContentGrid; fall back to Root.
            // Stable LTR: zone index == visual pane number - 1 (no Focus/Flip remapping).
            FrameworkElement host = _slots[index].TileHost;
            if (host == null) host = _slots[index].ContentGrid;
            if (host == null) host = root;
            if (!root.IsVisible) return false;

            // Prefer host; if unmeasured mid-layout (common for pane 3 / zone 2), fall back to Root.
            if (host.ActualWidth < 2 || host.ActualHeight < 2)
                host = root;
            if (host.ActualWidth < 2 || host.ActualHeight < 2)
                return false;

            double w = host.ActualWidth;
            double h = host.ActualHeight;

 // PointToScreen includes LayoutTransform (UI zoom on RootDock) '--'-'--'-'-'--'--'- live tiles track scaled panes.
            Point tl = host.PointToScreen(new Point(0, 0));
            Point br = host.PointToScreen(new Point(w, h));

            // PerMonitorV2: PointToScreen returns device pixels. Floor/Ceil avoids gaps.
            int left = (int)Math.Floor(Math.Min(tl.X, br.X) + 1e-6);
            int top = (int)Math.Floor(Math.Min(tl.Y, br.Y) + 1e-6);
            int right = (int)Math.Ceiling(Math.Max(tl.X, br.X) - 1e-6);
            int bottom = (int)Math.Ceiling(Math.Max(tl.Y, br.Y) - 1e-6);

            if (right <= left || bottom <= top) return false;

            rect = new NativeMethods.Rect
            {
                Left = left,
                Top = top,
                Right = right,
                Bottom = bottom
            };
            return !rect.IsEmpty;
        }
        catch
        {
            return false;
        }
    }


    /// <summary>If two pane rects nearly share an edge, snap to the same integer pixel.</summary>
    private static void SnapSharedEdges(NativeMethods.Rect[] zones)
    {
        const int tol = 3;
        for (int a = 0; a < zones.Length; a++)
        {
            if (zones[a].IsEmpty) continue;
            for (int b = a + 1; b < zones.Length; b++)
            {
                if (zones[b].IsEmpty) continue;
                // Vertical shared edge: a.Right ~ b.Left or vice versa
                if (Math.Abs(zones[a].Right - zones[b].Left) <= tol)
                {
                    int edge = zones[a].Right; // prefer left pane's right
                    zones[b].Left = edge;
                }
                else if (Math.Abs(zones[b].Right - zones[a].Left) <= tol)
                {
                    int edge = zones[b].Right;
                    zones[a].Left = edge;
                }

                // Horizontal shared edge
                if (Math.Abs(zones[a].Bottom - zones[b].Top) <= tol)
                {
                    int edge = zones[a].Bottom;
                    zones[b].Top = edge;
                }
                else if (Math.Abs(zones[b].Bottom - zones[a].Top) <= tol)
                {
                    int edge = zones[b].Bottom;
                    zones[a].Top = edge;
                }
            }
        }
    }


    /// <summary>
    /// Move assignment from one pane to another (unlink old / link new). If target occupied, swap.
    /// Single-HWND invariant via WindowTiler.Assign.
    /// </summary>
    private void MoveOrSwapPaneAssignment(int fromZone, int toZone)
    {
        fromZone = Math.Clamp(fromZone, 0, _paneCount - 1);
        toZone = Math.Clamp(toZone, 0, _paneCount - 1);
        if (fromZone == toZone) return;
        var src = _tiler.GetZone(fromZone);
        if (src == null || src.Parked)
        {
            try { StatusText.Text = "No assignment on the selected pane to move."; } catch { }
            return;
        }
        var entry = ToWindowEntry(src);
        bool targetOccupied = _tiler.GetZone(toZone) != null;
        AssignToPane(entry, toZone, swapIfOccupied: targetOccupied);
        int vnFrom = VisualPaneNumber(fromZone);
        int vnTo = VisualPaneNumber(toZone);
        try
        {
            StatusText.Text = targetOccupied
                ? $"Swapped Pane {vnFrom} <-> Pane {vnTo}"
                : $"Moved {src.ShortTitle} -> Pane {vnTo}";
        }
        catch { }
    }

    /// <summary>
    /// Arrow move: push linked window to an adjacent pane. If target is occupied, unlink it first (no swap).
    /// </summary>
    private void PushPaneWindowAdjacent(int fromZone, int delta)
    {
        fromZone = Math.Clamp(fromZone, 0, _paneCount - 1);
        int toZone = fromZone + delta;
        if (toZone < 0 || toZone >= _paneCount) return;
        var src = _tiler.GetZone(fromZone);
        if (src == null || src.Parked)
        {
            try { StatusText.Text = "Nothing linked in this pane to move."; } catch { }
            return;
        }
        // Snapshot before quiet-unlinking the target (full UnlinkWorking was double-retiling and blinking frames).
        var entry = ToWindowEntry(src);
        var dest = _tiler.GetZone(toZone);
        if (dest != null && !dest.Parked && dest.Hwnd != entry.Hwnd)
        {
            try
            {
                string destName = dest.ShortTitle;
                _liveTopmostPinned.Remove(dest.Hwnd);
                _tiler.Unassign(dest, restore: true, minimize: true);
                SyncLinkedFlagsInList();
                try { StatusText.Text = $"Unlinked {destName} to make room (minimized)"; } catch { }
            }
            catch { }
        }
        AssignToPane(entry, toZone, swapIfOccupied: false);
        try { StatusText.Text = $"Moved to pane {VisualPaneNumber(toZone)}"; } catch { }
        SyncVisualPaneLabels();
    }

    private void PaneMoveLeft_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        _suppressPaneClick = true;
        if (sender is Button b && b.Tag is int zone)
            PushPaneWindowAdjacent(zone, -1);
    }

    private void PaneMoveRight_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        _suppressPaneClick = true;
        if (sender is Button b && b.Tag is int zone)
            PushPaneWindowAdjacent(zone, +1);
    }

    private void PaneMoveLeft_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // One-shot: handle on press so pane host drag / Pane_Click cannot eat the first click.
        e.Handled = true;
        _suppressPaneClick = true;
        if (sender is Button b && b.Tag is int zone)
            PushPaneWindowAdjacent(zone, -1);
    }

    private void PaneMoveRight_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _suppressPaneClick = true;
        if (sender is Button b && b.Tag is int zone)
            PushPaneWindowAdjacent(zone, +1);
    }
        private void ChipArrow_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not int paneNum) return;
        int zone = paneNum - 1;
        string? tag = null;
        if (e.OriginalSource is DependencyObject start)
        {
            for (DependencyObject? d = start; d != null; d = VisualTreeHelper.GetParent(d))
            {
                if (ReferenceEquals(d, btn)) break;
                if (d is FrameworkElement x && x.Tag is string s)
                {
                    tag = s;
                    break;
                }
            }
        }
        if (tag == "move-left")
        {
            e.Handled = true;
            _suppressPaneClick = true;
            PushPaneWindowAdjacent(zone, -1);
        }
        else if (tag == "move-right")
        {
            e.Handled = true;
            _suppressPaneClick = true;
            PushPaneWindowAdjacent(zone, +1);
        }
    }
private Button MakeChipMoveButton(string glyph, int zone, int delta, bool enabled)
    {
        var b = new Button
        {
            Content = glyph,
            Width = 20,
            Height = 24,
            FontSize = 9,
            Padding = new Thickness(0),
            Margin = new Thickness(0),
            Tag = zone,
            IsEnabled = enabled,
            Opacity = enabled ? 0.95 : 0.35,
            ToolTip = delta < 0
                ? "Move this pane's window left (unlinks target if occupied)"
                : "Move this pane's window right (unlinks target if occupied)",
            Cursor = Cursors.Hand,
            Foreground = (Brush)FindResource("AccentBrush"),
            BorderBrush = (Brush)FindResource("BorderBrushQuiet"),
            BorderThickness = new Thickness(1)
        };
        b.SetResourceReference(Control.BackgroundProperty, "PanelAltBrush");
        b.SetResourceReference(Control.BorderBrushProperty, "BorderBrushQuiet");
        b.SetResourceReference(Control.ForegroundProperty, "AccentBrush");
        if (delta < 0)
            b.PreviewMouseLeftButtonDown += PaneMoveLeft_PreviewMouseLeftButtonDown;
        else
            b.PreviewMouseLeftButtonDown += PaneMoveRight_PreviewMouseLeftButtonDown;
        return b;
    }

    private static readonly (string Name, string Hex)[] AccentPresets =
    {
        ("Canary", "#FFE566"),
        ("Mint", "#7DFFB3"),
        ("Sky", "#7EC8FF"),
        ("Coral", "#FF8F7A"),
        ("Lavender", "#C5A6FF"),
        ("Amber", "#FFB347"),
        ("Rose", "#FF6B9D"),
        ("Ice", "#A8E6FF"),
        // Dark accents for light backgrounds (AccentTextBrush / AccentOnDimTextBrush invert when aLum < 0.55)
        ("Black", "#111111"),
        ("Dark grey", "#2B2B2B"),
        ("Midnight blue", "#0F172A"),
        ("Maroon", "#5C1A2E"),
    };
    private int _accentPresetIndex;

    /// <summary>Toggle the in-desk accent strip (no ContextMenu - menus deactivate the desk and blank Live panes).</summary>
    private void BtnAccentColor_Click(object sender, RoutedEventArgs e)
    {
        // Quiet Yield only - never ClearLiveTopmost / raise desk over tiles (that blanks panes).
        _chromePopupQuietUntilUtc = DateTime.UtcNow.AddSeconds(8);
        try
        {
            if (ColorStrip == null) return;
            bool open = ColorStrip.Visibility != Visibility.Visible;
            var zonesBefore = _liveDesk ? ComputePaneScreenZones() : Array.Empty<NativeMethods.Rect>();
            if (open) PositionColorStripLayer();
            ColorStrip.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            _colorStripOpen = open;
            if (open)
                RefreshColorStripSelection();

            // Strip changes TopChrome height once — retile into new TileHost rects, then force-pin.
            // Avoid desk Raise + double Assert (historically blinked/vanished Linked tiles).
            Dispatcher.BeginInvoke(() =>
            {
                try
                {
                    UpdateLayout();
                    TopChrome?.UpdateLayout();
                    PaneGrid?.UpdateLayout();
                }
                catch { }
                try
                {
                    if (!_liveDesk) return;
                    // v1.0.2: the strip overlays the header, so pane geometry normally does not change ->
                    // no retile (retiling resized every Linked window = blank/flashing panes). Only if the
                    // header really grew (very narrow window: strip wrapped) retile, then re-pin.
                    var zonesAfter = ComputePaneScreenZones();
                    if (!SameZones(zonesBefore, zonesAfter))
                    {
                        ApplyLiveTiling();
                        ReassertLiveTilesAfterColorChrome();
                    }
                    else
                        ReassertLiveTilesIfBuried();
                }
                catch { }
            }, DispatcherPriority.Loaded);
        }
        catch { }
    }

    /// <summary>
    /// Color strip helper: force-pin Live tiles only. Never raise desk above tiles / never retile here.
    /// (Strip open/close already retires via BtnAccentColor_Click; Deactivated must not bury panes.)
    /// </summary>
    private void RaiseDeskAboveLiveForChrome()
    {
        // Do NOT put the desk above Live tiles - that paints opaque chrome over panes (gray/black).
        // Do NOT ApplyLiveTiling here — extra retile on every deactivate blinks/vanishes Linked tiles.
        ReassertLiveTilesIfBuried();
    }

    /// <summary>
    /// After accent/bg apply or color-strip chrome: ShowAndRaiseAllLiveTiles + PinLiveTopmost(force:true)
    /// for every assigned Live HWND. Bypasses chrome-quiet early returns on EnsureLiveTilesTopmostIfNeeded.
    /// Never RaiseDashboardAboveUnlinked (opaque desk bury).
    /// </summary>
    private void ReassertLiveTilesAfterColorChrome()
    {
        if (!_liveDesk) return;
        if (!IsLoaded || _ownHwnd == IntPtr.Zero) return;
        if (WindowState == WindowState.Minimized || _deskMinimized) return;
        if (_snipYieldActive) return;
        try
        {
            var zones = ComputePaneScreenZones();
            if (zones.Length == 0) return;
            ShowAndRaiseAllLiveTiles(zones);
            SweepStaleLiveTopmostPins();
        }
        catch { }
    }

    private static readonly (string Name, string Hex)[] BackgroundPresets =
    {
        ("Black", "#000000"),
        ("Dark", "#1A1A1A"),
        ("Grey", "#6B6B6B"),
        ("Light", "#D0D0D0"),
        ("White", "#F4F4F4"),
    };

    // Mid-grey outline reads on both light accents and black desk / dark swatches.
    private static readonly Brush ColorSwatchOutlineBrush = CreateFrozenBrush(Color.FromRgb(0xA8, 0xA8, 0xA8));
    private static readonly Brush ColorSwatchSelectedBrush = CreateFrozenBrush(Colors.White);

    private static Brush CreateFrozenBrush(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private void BuildColorStrip()
    {
        if (ColorStripHost != null)
        {
            ColorStripHost.Children.Clear();
            for (int i = 0; i < AccentPresets.Length; i++)
            {
                int idx = i;
                var p = AccentPresets[i];
                var col = (Color)ColorConverter.ConvertFromString(p.Hex);
                var swatch = new InvokableBorder
                {
                    Width = 22,
                    Height = 22,
                    CornerRadius = new CornerRadius(4),
                    Margin = new Thickness(0, 0, 6, 0),
                    Background = new SolidColorBrush(col),
                    BorderThickness = new Thickness(1),
                    BorderBrush = ColorSwatchOutlineBrush,
                    Cursor = Cursors.Hand,
                    ToolTip = "Accent: " + p.Name + "  " + p.Hex,
                    Tag = "a:" + p.Hex
                };
                void PickAccent()
                {
                    _chromePopupQuietUntilUtc = DateTime.UtcNow.AddSeconds(4);
                    _accentPresetIndex = idx;
                    ApplyAccentColor(p.Hex, persist: true);
                    RefreshColorStripSelection();
                    try { StatusText.Text = "Accent: " + p.Name; } catch { }
                }
                swatch.MouseLeftButtonUp += (_, __) => PickAccent();
                swatch.Invoked += (_, __) => PickAccent();
                ColorStripHost.Children.Add(swatch);
            }
        }

        if (BgStripHost != null)
        {
            BgStripHost.Children.Clear();
            for (int i = 0; i < BackgroundPresets.Length; i++)
            {
                var p = BackgroundPresets[i];
                var col = (Color)ColorConverter.ConvertFromString(p.Hex);
                var swatch = new InvokableBorder
                {
                    Width = 22,
                    Height = 22,
                    CornerRadius = new CornerRadius(4),
                    Margin = new Thickness(0, 0, 6, 0),
                    Background = new SolidColorBrush(col),
                    BorderThickness = new Thickness(1),
                    BorderBrush = ColorSwatchOutlineBrush,
                    Cursor = Cursors.Hand,
                    ToolTip = "Background: " + p.Name + "  " + p.Hex,
                    Tag = "b:" + p.Hex
                };
                void PickBackground()
                {
                    _chromePopupQuietUntilUtc = DateTime.UtcNow.AddSeconds(4);
                    ApplyBackgroundColor(p.Hex, persist: true);
                    RefreshColorStripSelection();
                    try { StatusText.Text = "Background: " + p.Name; } catch { }
                }
                swatch.MouseLeftButtonUp += (_, __) => PickBackground();
                swatch.Invoked += (_, __) => PickBackground();
                BgStripHost.Children.Add(swatch);
            }
        }

        RefreshColorStripSelection();
    }

    private void RefreshColorStripSelection()
    {
        void Mark(UIElementCollection? kids, string prefix, string current)
        {
            if (kids == null) return;
            foreach (var child in kids)
            {
                if (child is not Border b || b.Tag is not string tag || !tag.StartsWith(prefix, StringComparison.Ordinal)) continue;
                var hex = tag.Substring(prefix.Length);
                bool on = hex.Equals(current, StringComparison.OrdinalIgnoreCase);
                // Selected: thicker white ring; unselected: subtle mid-grey (visible on black desk + dark accents).
                b.BorderBrush = on ? ColorSwatchSelectedBrush : ColorSwatchOutlineBrush;
                b.BorderThickness = on ? new Thickness(2) : new Thickness(1);
                b.Opacity = on ? 1.0 : 0.85;
            }
        }
        Mark(ColorStripHost?.Children, "a:", _accentHex);
        Mark(BgStripHost?.Children, "b:", _backgroundHex);
    }

    private void BtnAccentUseDefault_Click(object sender, RoutedEventArgs e)
    {
        _chromePopupQuietUntilUtc = DateTime.UtcNow.AddSeconds(2);
        _suppressLiveRetileUntilUtc = DateTime.UtcNow.AddMilliseconds(500);
        var a = string.IsNullOrWhiteSpace(_accentDefaultHex) ? "#FFE566" : _accentDefaultHex;
        var b = string.IsNullOrWhiteSpace(_backgroundDefaultHex) ? "#000000" : _backgroundDefaultHex;
        ApplyAccentColor(a, persist: false);
        ApplyBackgroundColor(b, persist: true);
        RefreshColorStripSelection();
        try { StatusText.Text = "Defaults: accent " + a + " / bg " + b; } catch { }
    }

    private void BtnAccentSetDefault_Click(object sender, RoutedEventArgs e)
    {
        _chromePopupQuietUntilUtc = DateTime.UtcNow.AddSeconds(2);
        _accentDefaultHex = _accentHex;
        _backgroundDefaultHex = _backgroundHex;
        try { Persist(); } catch { }
        try { StatusText.Text = "Defaults saved: accent " + _accentDefaultHex + " / bg " + _backgroundDefaultHex; } catch { }
    }

    /// <summary>Re-bind chrome that was painted with frozen SolidColorBrushes so Bg/Accent swaps stick.</summary>
    private void RefreshThemedChrome()
    {
        try { RefreshPaneButtons(); } catch { }
        try { HighlightActivePane(); } catch { }


        try
        {
            foreach (var slot in _slots)
            {
                if (slot?.MenuBtn != null)
                {
                    slot.MenuBtn.SetResourceReference(Control.BackgroundProperty, "PanelAltBrush");
                    slot.MenuBtn.SetResourceReference(Control.BorderBrushProperty, "BorderBrushQuiet");
                    slot.MenuBtn.SetResourceReference(Control.ForegroundProperty, "TextBrush");
                }
                if (slot?.MoveLeftBtn != null)
                {
                    slot.MoveLeftBtn.SetResourceReference(Control.BackgroundProperty, "PanelAltBrush");
                    slot.MoveLeftBtn.SetResourceReference(Control.BorderBrushProperty, "BorderBrushQuiet");
                    slot.MoveLeftBtn.SetResourceReference(Control.ForegroundProperty, "AccentBrush");
                }
                if (slot?.MoveRightBtn != null)
                {
                    slot.MoveRightBtn.SetResourceReference(Control.BackgroundProperty, "PanelAltBrush");
                    slot.MoveRightBtn.SetResourceReference(Control.BorderBrushProperty, "BorderBrushQuiet");
                    slot.MoveRightBtn.SetResourceReference(Control.ForegroundProperty, "AccentBrush");
                }
            }
        }
        catch { }
    }

    private void ApplyBackgroundColor(string hex, bool persist)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(hex)) hex = "#000000";
            hex = hex.Trim();
            var color = (Color)ColorConverter.ConvertFromString(hex);
            _backgroundHex = hex;

            // Lighten dark desks; darken light desks so panels still separate from the window bg.
            double lum = (0.2126 * color.R + 0.7152 * color.G + 0.0722 * color.B) / 255.0;
            int Shift(byte ch, int amount)
            {
                int v = lum < 0.55 ? ch + amount : ch - amount;
                return Math.Clamp(v, 0, 255);
            }
            var panel = Color.FromRgb((byte)Shift(color.R, 12), (byte)Shift(color.G, 12), (byte)Shift(color.B, 12));
            var alt = Color.FromRgb((byte)Shift(color.R, 20), (byte)Shift(color.G, 20), (byte)Shift(color.B, 20));

            void Put(string key, Color col)
            {
                var brush = new SolidColorBrush(col);
                brush.Freeze();
                Application.Current.Resources[key] = brush;
                Resources[key] = brush;
            }
            Put("BgBrush", color);
            Put("PanelBrush", panel);
            Put("PanelAltBrush", alt);
            // Gradient / highlight / glow polish re-derived from the new background.
            try { RebuildThemeDerivedVisuals(); } catch { }

            _suppressLiveRetileUntilUtc = DateTime.UtcNow.AddMilliseconds(400);
            try { RefreshThemedChrome(); } catch { }
            // v1.0.2: purely visual - touch Linked windows only if one is actually buried under the desk.
            ReassertLiveTilesIfBuried();

            if (persist)
            {
                try { Persist(); } catch { }
                try { StatusText.Text = "Background: " + hex; } catch { }
            }
        }
        catch (Exception ex)
        {
            try { StatusText.Text = "Background failed: " + ex.Message; } catch { }
        }
    }

    private void ApplyAccentColor(string hex, bool persist)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(hex)) hex = "#FFE566";
            hex = hex.Trim();
            var color = (Color)ColorConverter.ConvertFromString(hex);
            _accentHex = hex;
            for (int i = 0; i < AccentPresets.Length; i++)
                if (AccentPresets[i].Hex.Equals(hex, StringComparison.OrdinalIgnoreCase))
                { _accentPresetIndex = i; break; }

            byte Dim(byte ch) => (byte)Math.Max(0, ch * 35 / 100);
            byte Mute(byte ch) => (byte)Math.Max(0, Math.Min(255, ch * 72 / 100));
            byte Quiet(byte ch) => (byte)Math.Max(0, Math.Min(255, 0x2A + ch * 12 / 100));
            var dim = Color.FromRgb(Dim(color.R), Dim(color.G), Dim(color.B));
            var muted = Color.FromRgb(Mute(color.R), Mute(color.G), Mute(color.B));
            var quiet = Color.FromRgb(Quiet(color.R), Quiet(color.G), Quiet(color.B));

            void Put(string key, Color col)
            {
                var brush = new SolidColorBrush(col);
                brush.Freeze();
                Application.Current.Resources[key] = brush;
                Resources[key] = brush;
            }
            Put("AccentBrush", color);
            Put("TextBrush", color);
            Put("MutedTextBrush", muted);
            Put("AccentDimBrush", dim);
            Put("DangerBrush", color);
            Put("DangerDimBrush", dim);
            Put("BorderBrushQuiet", quiet);
            // Light text on dark accents so filled chips stay readable
            double aLum = (0.2126 * color.R + 0.7152 * color.G + 0.0722 * color.B) / 255.0;
            Put("AccentTextBrush", aLum < 0.55 ? Colors.White : Colors.Black);
            // Text on AccentDimBrush fills: white for dark accents, accent color for light ones
            Put("AccentOnDimTextBrush", aLum < 0.55 ? Colors.White : color);
            Put("SuccessBrush", color);

            // Clipboard paper well: manila tinted by accent so Color strip recolors Preview.
            Color Mix(Color baseC, Color accent, double t)
            {
                byte Lerp(byte a, byte b) => (byte)Math.Max(0, Math.Min(255, (int)(a + (b - a) * t)));
                return Color.FromRgb(Lerp(baseC.R, accent.R), Lerp(baseC.G, accent.G), Lerp(baseC.B, accent.B));
            }
            var manila = (Color)ColorConverter.ConvertFromString("#E8DFC8");
            var surface = (Color)ColorConverter.ConvertFromString("#F3ECD8");
            var rule = (Color)ColorConverter.ConvertFromString("#C4B898");
            // Stronger accent wash into manila so Color strip visibly retints Preview paper
            // (surface sits opaque over the well wash in DocsOverlayWindow).
            Put("PaperWellBrush", Mix(manila, color, 0.42));
            Put("PaperSurfaceBrush", Mix(surface, color, 0.36));
            Put("PaperBorderBrush", Mix(rule, color, 0.45));
            Put("PaperRuleBrush", Mix(rule, color, 0.38));
            // Ink: dark on light paper; lighten slightly for very dark accents so wash stays readable.
            double paperLum = (0.2126 * Mix(manila, color, 0.42).R + 0.7152 * Mix(manila, color, 0.42).G + 0.0722 * Mix(manila, color, 0.42).B) / 255.0;
            Put("PaperInkBrush", paperLum > 0.55 ? Color.FromRgb(0x2A, 0x26, 0x1C) : Color.FromRgb(0x1A, 0x18, 0x12));
            Put("PaperMutedBrush", Mix(Color.FromRgb(0x6A, 0x64, 0x58), color, 0.28));
            // Gradient / highlight / accent-glow polish re-derived from the new accent.
            try { RebuildThemeDerivedVisuals(); } catch { }

            // Recolor only ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬Ãƒâ€šÃ‚Â never SyncToggle / SyncVisual / Live retile (those were vanishing panes).
            try { SetResourceReference(ForegroundProperty, "TextBrush"); } catch { }
            try
            {
                if (UnlinkDropZone != null)
                {
                    UnlinkDropZone.SetResourceReference(Border.BorderBrushProperty, "AccentBrush");
                    if (UnlinkDropZone.Child is TextBlock tb)
                        tb.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
                }
            }
            catch { }

            // Theme swaps can invalidate layout; don't let that retile/TOPMOST-thrash Live panes.
            _suppressLiveRetileUntilUtc = DateTime.UtcNow.AddMilliseconds(400);
            try { RefreshThemedChrome(); } catch { }
            // v1.0.2: purely visual - touch Linked windows only if one is actually buried under the desk.
            ReassertLiveTilesIfBuried();

            if (persist)
            {
                try { Persist(); } catch { }
                try { StatusText.Text = "Accent: " + hex; } catch { }
            }
        }
        catch (Exception ex)
        {
            try { StatusText.Text = "Color failed: " + ex.Message; } catch { }
        }
    }

    private static WindowEntry ToWindowEntry(WorkingWindow w) => new()
    {
        Hwnd = w.Hwnd,
        Title = w.Title,
        ProcessName = w.ProcessName,
        Icon = w.Icon
    };


    private WindowEntry? CaptureWindowEntry(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd)) return null;
        try
        {
            var list = WindowEnumerator.GetTopLevelWindows(_ownHwnd, Process.GetCurrentProcess().Id);
            return list.FirstOrDefault(w => w.Hwnd == hwnd);
        }
        catch { return null; }
    }

    private void DropAssignOntoZone(DragEventArgs e, int index)
    {
        WindowEntry? entry = null;
        if (e.Data.GetDataPresent(typeof(WorkingWindow)) && e.Data.GetData(typeof(WorkingWindow)) is WorkingWindow ww)
            entry = ToWindowEntry(ww);
        else if (e.Data.GetDataPresent(typeof(WindowEntry)) && e.Data.GetData(typeof(WindowEntry)) is WindowEntry we)
            entry = we;
        if (entry == null || entry.Hwnd == IntPtr.Zero) return;

        bool already = _tiler.WorkingSet.Any(x => x.Hwnd == entry.Hwnd && !x.Parked);
        bool targetOccupied = _tiler.GetZone(index) != null;
        // Move if already assigned elsewhere; swap when target occupied; else assign.
        // Prefer swap whenever both sides are assigned so pane-to-pane drag never drops a link.
        // Pane-to-pane / chip drops: swap when target filled so links never vanish.
        AssignToPane(entry, index, swapIfOccupied: targetOccupied && (already || e.Data.GetDataPresent(typeof(WorkingWindow))));
        if (_distractionFree) SyncShowUiOverlay();
    }

    // ---- Drag-and-drop assign from window list onto panes ----

    private void WindowList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(null);
        _dragPending = true;
        // Hover / press-to-link phase: do not yield Live TOPMOST if the desk deactivates.
        _linkInteractionQuietUntilUtc = DateTime.UtcNow.AddMilliseconds(2500);
    }

    private void WindowList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragPending || e.LeftButton != MouseButtonState.Pressed) return;
        var pos = e.GetPosition(null);
        if (Math.Abs(pos.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        var entry = Selected;
        if (entry == null) return;
        _dragPending = false;
        // Cover the whole blocking DoDragDrop, then a short settle after drop/cancel.
        _linkInteractionQuietUntilUtc = DateTime.UtcNow.AddSeconds(30);
        BeginDocsFileDragHitThrough();
        try
        {
            DragDrop.DoDragDrop(WindowList, entry, DragDropEffects.Copy | DragDropEffects.Move);
        }
        catch { /* ignore drag cancel */ }
        finally
        {
            EndDocsFileDragHitThrough();
            _linkInteractionQuietUntilUtc = DateTime.UtcNow.AddMilliseconds(1500);
        }
    }

    private void WindowList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        // Click (no drag): open window in Preview with Assign-to-pane (keeps sibling Live panes filled).
        bool wasClick = _dragPending;
        _dragPending = false;
        if (!wasClick) return;
        if (e.OriginalSource is DependencyObject src)
        {
            for (var d = src; d != null; d = VisualTreeHelper.GetParent(d) as DependencyObject)
            {
                if (d is Border b && b.Tag is WindowEntry)
                    return; // link chip
            }
        }
        var entry = Selected;
        if (entry == null || entry.Hwnd == IntPtr.Zero) return;
        OfferCenteredAssign(entry);
        e.Handled = true;
    }

    private void SeedAutoPreviewSeenHwnds()
    {
        try
        {
            foreach (var w in WindowEnumerator.GetTopLevelWindows(_ownHwnd, Process.GetCurrentProcess().Id))
                _autoPreviewSeenHwnds.Add(w.Hwnd);
        }
        catch { }
    }

    /// <summary>
    /// When a new top-level window appears, open it in Files Preview with Assign-to-pane.
    /// </summary>
    private void MaybeAutoPreviewNewWindows()
    {
        // Auto-open Preview was blinking and confusing; open only from Windows click / Docs double-click.
        // Dead body removed (FIRST cleanup). Keep stub so SoftRefresh call sites stay valid.
    }
    private void OfferCenteredAssign(WindowEntry entry)
    {
        try
        {
            if (entry == null || entry.Hwnd == IntPtr.Zero) return;

            // Context menu / focus handoff must not blank sibling Live panes.
            _chromePopupQuietUntilUtc = DateTime.UtcNow.AddSeconds(4);
            // Preview-only: never raise/center/focus the real unlinked window.
            _yieldedForOutsideFocus = false;
            try
            {
                if (_liveDesk)
                    AssertAllAssignedLiveTiles(IntPtr.Zero, focus: false, allowWhenLiveFocused: true, forceWhenInactive: true);
            }
            catch { }

            // Preview is the assign UI (replaces buried context menu).
            OpenWindowInPreview(entry);
        }
        catch (Exception ex)
        {
            try { StatusText.Text = "Assign preview failed: " + ex.Message; } catch { }
        }
    }

    private void OpenWindowInPreview(WindowEntry entry)
    {
        if (entry == null || entry.Hwnd == IntPtr.Zero) return;
        MarkDocsChromeActive();
        var beforeWin = SnapshotAssignments(except: -1);
        _filesOverlayOpen = true;
        _yieldedForOutsideFocus = false;
        _suppressAutoWindowPreviewUntilUtc = DateTime.UtcNow.AddSeconds(2);
        DocsOverlayWindow.ZoneFilledProbe = IsPaneFilled;
        DocsOverlayWindow.ShowWindowPreview(
            this,
            entry.Hwnd,
            entry.Title ?? entry.ProcessName ?? "Window",
            onClosed: () =>
            {
                _filesOverlayOpen = false;
                try
                {
                    if (_liveDesk)
                        AssertAllAssignedLiveTiles(IntPtr.Zero, focus: false, allowWhenLiveFocused: true, forceWhenInactive: true);
                }
                catch { }
            },
            onAssignWindow: (hwnd, zone) =>
            {
                try
                {
                    var e2 = CaptureWindowEntry(hwnd) ?? EntryFromHwnd(hwnd) ?? entry;
                    if (e2 == null) return;
                    // Snapshot sibling zones BEFORE assign; Assign already replaces target only.
                    var beforeOther = SnapshotAssignments(except: zone);
                    AssignToPane(e2, zone, swapIfOccupied: false);
                    EnsureAssignmentsIntact(beforeOther);
                    _yieldedForOutsideFocus = false;
                    StatusText.Text = "Assigned to pane " + (zone + 1);
                    try
                    {
                        if (_liveDesk)
                            AssertAllAssignedLiveTiles(IntPtr.Zero, focus: false, allowWhenLiveFocused: true, forceWhenInactive: true);
                    }
                    catch { }
                    // Assign â†’ heal â†’ Assert tiles â†’ Preview LAST (middle-pane Assert was burying overlay).
                    try { DocsOverlayWindow.RaiseOpenInstanceIfAny(activate: false, forceAboveTiles: true); } catch { }
                }
                catch (Exception ex) { try { StatusText.Text = "Assign failed: " + ex.Message; } catch { } }
            },
            paneCount: Math.Max(1, _paneCount),
            onCycle: delta => CycleWindowsInPreview(delta),
            getCycleCount: () =>
            {
                try
                {
                    if (WindowList.ItemsSource is System.Collections.IEnumerable list)
                        return list.Cast<WindowEntry>().Count();
                }
                catch { }
                return WindowList.Items.Count;
            },
            onSwitchMode: mode => SwitchPreviewMode(mode));
        try { EnsureAssignmentsIntact(beforeWin); KeepLivePanesAfterDocs(); } catch { }
    }

    /// <summary>Cycle Preview to next/prev Windows list entry (wrap-around). Same overlay; no RaiseUnlinked.
    /// Prefers an unassigned neighbor when available (e.g. after assign-from-Preview); else next list neighbor.</summary>
    private void CycleWindowsInPreview(int delta)
    {
        try
        {
            var windows = (WindowList.ItemsSource as IEnumerable<WindowEntry>)?.ToList()
                          ?? WindowList.Items.Cast<WindowEntry>().ToList();
            if (windows.Count == 0) return;
            var cur = DocsOverlayWindow.GetPreviewHwnd();
            int idx = -1;
            if (cur != IntPtr.Zero)
            {
                for (int i = 0; i < windows.Count; i++)
                {
                    if (windows[i].Hwnd == cur) { idx = i; break; }
                }
            }
            if (idx < 0) idx = 0;
            int n = windows.Count;
            int step = delta >= 0 ? 1 : -1;
            int next = ((idx + step) % n + n) % n;
            // Prefer first unassigned window walking in cycle direction (skip already linked).
            int pick = next;
            for (int k = 0; k < n; k++)
            {
                int j = ((idx + step * (k + 1)) % n + n) % n;
                var cand = windows[j];
                if (cand == null || cand.Hwnd == IntPtr.Zero) continue;
                if (!IsAssignedLiveHwnd(cand.Hwnd))
                {
                    pick = j;
                    break;
                }
            }
            var entry = windows[pick];
            try { WindowList.SelectedItem = entry; } catch { }
            OpenWindowInPreview(entry);
        }
        catch (Exception ex)
        {
            try { StatusText.Text = "Cycle failed: " + ex.Message; } catch { }
        }
    }

    private void CenterWindowOverDesk(IntPtr hwnd)
    {
        try
        {
            if (!NativeMethods.IsWindow(hwnd)) return;
            Point tl = PointToScreen(new Point(0, 0));
            Point br = PointToScreen(new Point(ActualWidth, ActualHeight));
            int deskW = Math.Max(200, (int)(br.X - tl.X));
            int deskH = Math.Max(200, (int)(br.Y - tl.Y));
            if (!NativeMethods.GetWindowRect(hwnd, out var wr)) return;
            int w = Math.Max(320, Math.Min(wr.Right - wr.Left, (int)(deskW * 0.72)));
            int h = Math.Max(240, Math.Min(wr.Bottom - wr.Top, (int)(deskH * 0.72)));
            int x = (int)tl.X + (deskW - w) / 2;
            int y = (int)tl.Y + (deskH - h) / 2;
            NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);
            NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOP,
                x, y, w, h,
                NativeMethods.SWP_SHOWWINDOW | NativeMethods.SWP_NOACTIVATE);
        }
        catch { }
    }


    /// <summary>
    /// Raise an unlinked window above the opaque desk (and above yielded Live tiles).
    /// Used when a new app would otherwise open behind Topmost desk / Live panes.
    /// </summary>
    private void RaiseUnlinkedWindowAboveDesk(IntPtr hwnd, bool center = false)
    {
        try
        {
            if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd)) return;
            if (hwnd == _ownHwnd || IsAssignedLiveHwnd(hwnd)) return;

            // Mark yielded FIRST so SoftRefresh/Assert cannot re-TOPMOST Live over this window.
            _yieldedForOutsideFocus = true;
            try { Topmost = false; } catch { }

            if (center)
                CenterWindowOverDesk(hwnd);

            if (_ownHwnd != IntPtr.Zero && NativeMethods.IsWindow(_ownHwnd))
            {
                NativeMethods.ClearExTopmostKeepZOrder(_ownHwnd);
                NativeMethods.SetWindowPos(
                    _ownHwnd, NativeMethods.HWND_NOTOPMOST, 0, 0, 0, 0,
                    NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
            }

            // Live tiles: drop TOPMOST, keep HWND_TOP above opaque desk (visible, not blank).
            if (_liveDesk)
            {
                for (int i = 0; i < _paneCount; i++)
                {
                    var w = _tiler.GetZone(i);
                    if (w == null || w.Parked) continue;
                    if (!NativeMethods.IsWindow(w.Hwnd)) continue;
                    NativeMethods.ClearLiveTopmost(w.Hwnd);
                    _liveTopmostPinned.Remove(w.Hwnd);
                    NativeMethods.SetWindowPos(
                        w.Hwnd, NativeMethods.HWND_TOP, 0, 0, 0, 0,
                        NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
                }
            }

            // New window ABOVE Live tiles: temporary TOPMOST (was landing between desk and panes).
            NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);
            NativeMethods.SetWindowPos(
                hwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_SHOWWINDOW);
            try { NativeMethods.FocusWindow(hwnd); } catch { }
            // Drop TOPMOST flag but keep z-order (stays above yielded Live HWND_TOP peers).
            try { NativeMethods.ClearExTopmostKeepZOrder(hwnd); } catch { }
        }
        catch { }
    }
    /// <summary>After Deactivated, FG may still be the desk for a beat ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬Ãƒâ€šÃ‚Â retry raising the real outside window.</summary>
    private void ScheduleRaiseOutsideForeground()
    {
        for (int delayMs = 50; delayMs <= 400; delayMs += 50)
        {
            int captured = delayMs;
            _ = Dispatcher.BeginInvoke(async () =>
            {
                try
                {
                    await System.Threading.Tasks.Task.Delay(captured);
                    if (IsActive || _snipYieldActive) return;
                    if (WindowState == WindowState.Minimized || _deskMinimized) return;
                    var fg = NativeMethods.GetForegroundWindow();
                    if (fg == IntPtr.Zero || fg == _ownHwnd || IsAssignedLiveHwnd(fg)) return;
                    try
                    {
                        NativeMethods.GetWindowThreadProcessId(fg, out uint fgPid);
                        if (fgPid == (uint)Environment.ProcessId) return;
                    }
                    catch { }
                    RaiseUnlinkedWindowAboveDesk(fg, center: false);
                }
                catch { }
            }, DispatcherPriority.Background);
        }
    }
    private void Pane_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(typeof(WindowEntry)) || e.Data.GetDataPresent(typeof(WorkingWindow)))
        {
            e.Effects = DragDropEffects.Move;
            if (sender is Border b)
                b.BorderBrush = (Brush)FindResource("AccentBrush");
        }
        else if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            if (sender is Border b2)
                b2.BorderBrush = (Brush)FindResource("AccentBrush");
        }
        else e.Effects = DragDropEffects.None;
        e.Handled = true;
    }

    private void Pane_DragLeave(object sender, DragEventArgs e)
    {
        HighlightActivePane();
    }

    private void Pane_Drop(object sender, DragEventArgs e)
    {
        _suppressPaneClick = true;
        HighlightActivePane();
        if (sender is not Border border || border.Tag is not int index) return;
        if (index < 0 || index >= _paneCount) return;
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            DropFileOntoPane(e, index);
            e.Handled = true;
            return;
        }
        DropAssignOntoZone(e, index);
        e.Handled = true;
    }

                    // Extra WMP HWND hiding disabled ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬Ãƒâ€¦Ã‚Â¡ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â it was hiding the controls so pause/click failed.
    private void CloseOrphanWmplayers(IntPtr keepHwnd) { _ = keepHwnd; }
    private void ScheduleHideExtraWmplayers(IntPtr keepHwnd) { _ = keepHwnd; }
private static void EnsureWmpFitPrefs(int suggestW = 0, int suggestH = 0)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(
                @"Software\Microsoft\MediaPlayer\Preferences");
            if (key == null) return;
            key.SetValue("StretchToFit", 0, Microsoft.Win32.RegistryValueKind.DWord);
            key.SetValue("ShrinkToFit", 1, Microsoft.Win32.RegistryValueKind.DWord);
            key.SetValue("VideoZoom", 100, Microsoft.Win32.RegistryValueKind.DWord);
            key.SetValue("Maximized", 0, Microsoft.Win32.RegistryValueKind.DWord);
            try { key.SetValue("SameAsVideoSize", 0, Microsoft.Win32.RegistryValueKind.DWord); } catch { }
            if (suggestW >= 200) key.SetValue("WidthV11", suggestW, Microsoft.Win32.RegistryValueKind.DWord);
            if (suggestH >= 200) key.SetValue("HeightV11", suggestH, Microsoft.Win32.RegistryValueKind.DWord);
        }
        catch { }
    }
    /// <summary>Open a file into a Live pane (same path as Docs/file drop).</summary>
    private void AssignFilePathToPane(string path, int index)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
        if (index < 0 || index >= _paneCount) return;
        if (_fileDropBusy) return;
        _fileDropBusy = true;
        _lastFileDropPath = path;
        _lastFileDropUtc = DateTime.UtcNow;
        var nameHint = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path).ToLowerInvariant();
        var beforeHwnds = SnapshotVisibleHwnds();
        var beforePids = SnapshotPlayerProcessIds();
        try
        {
            try
            {
                var zones = ComputePaneScreenZones();
                if (index < zones.Length && !zones[index].IsEmpty)
                {
                    _wmpSuggestW = Math.Max(200, zones[index].Width);
                    _wmpSuggestH = Math.Max(200, zones[index].Height);
                }
            }
            catch { }
            LaunchFileForPane(path, ext);
            StatusText.Text = "Linking to pane " + (index + 1) + ": " + Path.GetFileName(path);
            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                var (hwnd, via) = await WaitForOpenedFileHwndAsync(beforeHwnds, beforePids, nameHint, ext);
                await Dispatcher.InvokeAsync(() =>
                {
                    try
                    {
                        EndDocsFileDragHitThrough();
                        if (hwnd != IntPtr.Zero)
                        {
                            var entry = CaptureWindowEntry(hwnd) ?? EntryFromHwnd(hwnd);
                            if (entry != null)
                            {
                                bool isVideo = ext is ".mp4" or ".mkv" or ".avi" or ".mov" or ".wmv" or ".webm" or ".m4v";
                                if (isVideo && index < _paneFitMode.Length)
                                    _paneFitMode[index] = "fill";
                                AssignToPane(entry, index, swapIfOccupied: true);
                                ScheduleLiveRetile();
                                StartHoldFillEnforce(index, isVideo ? 2400 : 2000);
                                if (isVideo)
                                    NudgeWmpVideoReflow(hwnd, index);
                                StatusText.Text = "Linked to pane " + (index + 1) + " (" + via + ")";
                            }
                        }
                        else
                            StatusText.Text = "Opened but window not found yet - pick it in Windows list";
                    }
                    catch (Exception ex) { StatusText.Text = "Link failed: " + ex.Message; }
                    finally { _fileDropBusy = false; }
                });
            });
        }
        catch
        {
            _fileDropBusy = false;
            throw;
        }
    }
private void DropFileOntoPane(DragEventArgs e, int index)
    {
        if (index < 0 || index >= _paneCount) return;
        if (_fileDropBusy) return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0 || !File.Exists(files[0]))
            return;
        var path = files[0];
        if (_lastFileDropPath != null
            && string.Equals(_lastFileDropPath, path, StringComparison.OrdinalIgnoreCase)
            && (DateTime.UtcNow - _lastFileDropUtc).TotalSeconds < 4.0)
            return;
        _fileDropBusy = true;
        _lastFileDropPath = path;
        _lastFileDropUtc = DateTime.UtcNow;
        var nameHint = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path).ToLowerInvariant();
        var beforeHwnds = SnapshotVisibleHwnds();
        var beforePids = SnapshotPlayerProcessIds();
        try
        {
            try
            {
                var zones = ComputePaneScreenZones();
                if (index >= 0 && index < zones.Length && !zones[index].IsEmpty)
                {
                    _wmpSuggestW = Math.Max(200, zones[index].Width);
                    _wmpSuggestH = Math.Max(200, zones[index].Height);
                }
            }
            catch { }
            LaunchFileForPane(path, ext);
            StatusText.Text = "Opening on pane " + (index + 1) + ": " + Path.GetFileName(path);
            try { AppendDebug("drop-open path=" + path + " pane=" + (index + 1)); } catch { }
            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                var (hwnd, via) = await WaitForOpenedFileHwndAsync(beforeHwnds, beforePids, nameHint, ext);
                await Dispatcher.InvokeAsync(() =>
                {
                    try
                    {
                        EndDocsFileDragHitThrough();
                        if (hwnd != IntPtr.Zero)
                        {
                            var entry = CaptureWindowEntry(hwnd) ?? EntryFromHwnd(hwnd);
                            try { AppendDebug("drop-open found via=" + via + " hwnd=0x" + hwnd.ToString("X") + " title=" + (entry?.Title ?? "")); } catch { }
                            if (entry != null)
                            {
                                bool isVideo = ext is ".mp4" or ".mkv" or ".avi" or ".mov" or ".wmv" or ".webm" or ".m4v";
                                // WMP fills the pane; video letterboxes landscape via ShrinkToFit (set on launch).
                                if (isVideo && index >= 0 && index < _paneFitMode.Length)
                                    _paneFitMode[index] = "fill";
                                AssignToPane(entry, index, swapIfOccupied: true);
                                ScheduleLiveRetile();
                                StartHoldFillEnforce(index, isVideo ? 2400 : 2000);
                                if (isVideo)
                                    NudgeWmpVideoReflow(hwnd, index);
                                    ScheduleHideExtraWmplayers(hwnd);
                                Dispatcher.BeginInvoke(() =>
                                {
                                    try
                                    {
                                        if (_liveDesk) ApplyLiveTiling(activateZone: index);
                                    }
                                    catch { }
                                }, DispatcherPriority.ApplicationIdle);
                            }
                            else
                                StatusText.Text = "Opened " + Path.GetFileName(path) + " (no window entry)";
                        }
                        else
                        {
                            try { AppendDebug("drop-open NOT FOUND path=" + path); } catch { }
                            StatusText.Text = "Opened " + Path.GetFileName(path) + " (window not found to assign)";
                        }
                        KeepLivePanesAfterDocs();
                    }
                    catch (Exception ex)
                    {
                        try { StatusText.Text = "Assign failed: " + ex.Message; } catch { }
                    }
                    finally { _fileDropBusy = false; }
                });
            });
        }
        catch (Exception ex)
        {
            EndDocsFileDragHitThrough();
            _fileDropBusy = false;
            StatusText.Text = "Open failed: " + ex.Message;
        }
    }

    /// <summary>
    /// WMP remembers last window size (WidthV11/HeightV11) and keeps video zoomed to that.
    /// Reset letterbox prefs + clear saved size, and close any running player so a fresh
    /// window picks up the pane size instead of a huge prior frame.
    /// </summary>
        private void TryConfigureWmpLetterboxVideo(int suggestW = 0, int suggestH = 0)
    {
        EnsureWmpFitPrefs(suggestW, suggestH);
    }
private void LaunchFileForPane(string path, string ext)
    {
        static string? FirstExisting(params string[] candidates)
        {
            foreach (var p in candidates)
                if (!string.IsNullOrEmpty(p) && File.Exists(p)) return p;
            return null;
        }
        string? exe = ext switch
        {
            ".mp4" or ".mkv" or ".avi" or ".mov" or ".wmv" or ".webm" or ".m4v" =>
                FirstExisting(
                    @"C:\Program Files\VideoLAN\VLC\vlc.exe",
                    @"C:\Program Files (x86)\VideoLAN\VLC\vlc.exe",
                    @"C:\Program Files (x86)\Windows Media Player\wmplayer.exe"),
            ".pdf" =>
                FirstExisting(@"C:\Program Files\ONLYOFFICE\DesktopEditors\DesktopEditors.exe"),
            _ => null
        };
        if (exe != null)
        {
            bool isWmp = exe.EndsWith("wmplayer.exe", StringComparison.OrdinalIgnoreCase);
            bool isVlc = exe.EndsWith("vlc.exe", StringComparison.OrdinalIgnoreCase);
            if (isWmp)
                TryConfigureWmpLetterboxVideo(_wmpSuggestW, _wmpSuggestH);
            // VLC: open file only (no playlist chrome extras).
            string args = isVlc
                ? "--started-from-file \"" + path + "\""
                : "\"" + path + "\"";
            Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = args,
                UseShellExecute = !isWmp && !isVlc
            });
            return;
        }
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    private HashSet<IntPtr> SnapshotVisibleHwnds()
    {
        var set = new HashSet<IntPtr>();
        try
        {
            foreach (var w in WindowEnumerator.GetTopLevelWindows(_ownHwnd, Process.GetCurrentProcess().Id))
                set.Add(w.Hwnd);
        }
        catch { }
        return set;
    }

    private HashSet<uint> SnapshotPlayerProcessIds()
    {
        var set = new HashSet<uint>();
        foreach (var name in new[] { "vlc", "wmplayer", "DesktopEditors", "ApplicationFrameHost", "MicrosoftPhotos", "Photos", "chrome", "msedge" })
        {
            try
            {
                foreach (var p in Process.GetProcessesByName(name))
                {
                    try { set.Add((uint)p.Id); } catch { }
                    p.Dispose();
                }
            }
            catch { }
        }
        return set;
    }

    private static string DescribeHwndSize(IntPtr hwnd)
    {
        try
        {
            if (NativeMethods.GetWindowRect(hwnd, out var r) && !r.IsEmpty)
                return r.Width + "x" + r.Height;
        }
        catch { }
        return "?";
    }

    private static WindowEntry? PreferLargestWindow(IEnumerable<WindowEntry> candidates)
    {
        return candidates
            .Where(w =>
            {
                try
                {
                    if (!NativeMethods.GetWindowRect(w.Hwnd, out var r) || r.IsEmpty) return false;
                    return r.Width >= 200 && r.Height >= 150;
                }
                catch { return false; }
            })
            .OrderByDescending(w =>
            {
                try
                {
                    NativeMethods.GetWindowRect(w.Hwnd, out var r);
                    return (long)r.Width * r.Height;
                }
                catch { return 0L; }
            })
            .FirstOrDefault();
    }

    private async System.Threading.Tasks.Task<(IntPtr hwnd, string via)> WaitForOpenedFileHwndAsync(
        HashSet<IntPtr> beforeHwnds, HashSet<uint> beforePids, string nameHint, string ext)
    {
        var playerNames = ext switch
        {
            ".pdf" => new[] { "desktopeditors", "onlyoffice" },
            ".mp4" or ".mkv" or ".avi" or ".mov" or ".wmv" or ".webm" or ".m4v" => new[] { "vlc", "wmplayer", "applicationframehost", "microsoftphotos", "photos" },
            _ => new[] { "vlc", "wmplayer", "desktopeditors", "applicationframehost", "microsoftphotos", "photos", "chrome", "msedge" }
        };

        for (int attempt = 0; attempt < 40; attempt++)
        {
            if (attempt > 0)
                await System.Threading.Tasks.Task.Delay(50);
            try
            {
                var list = WindowEnumerator.GetTopLevelWindows(_ownHwnd, Process.GetCurrentProcess().Id);

                if (!string.IsNullOrWhiteSpace(nameHint) && nameHint.Length >= 2)
                {
                    var byName = PreferLargestWindow(list.Where(w =>
                        w.Title.IndexOf(nameHint, StringComparison.OrdinalIgnoreCase) >= 0));
                    if (byName != null)
                        return (byName.Hwnd, "title:" + byName.Title);
                }

                var newPlayers = list.Where(w =>
                    !beforeHwnds.Contains(w.Hwnd) &&
                    playerNames.Any(n => w.ProcessName.Equals(n, StringComparison.OrdinalIgnoreCase)
                                      || w.ProcessName.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0));
                var bestPlayer = PreferLargestWindow(newPlayers);
                if (bestPlayer != null)
                    return (bestPlayer.Hwnd, "new-player:" + bestPlayer.ProcessName + ":" + bestPlayer.Title + " " + DescribeHwndSize(bestPlayer.Hwnd));

                var anyPlayers = PreferLargestWindow(list.Where(w =>
                    playerNames.Any(n => w.ProcessName.Equals(n, StringComparison.OrdinalIgnoreCase)
                                      || w.ProcessName.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0)));
                if (anyPlayers != null && !beforeHwnds.Contains(anyPlayers.Hwnd))
                    return (anyPlayers.Hwnd, "player-largest:" + anyPlayers.ProcessName + " " + DescribeHwndSize(anyPlayers.Hwnd));

                var fresh = PreferLargestWindow(list.Where(w => !beforeHwnds.Contains(w.Hwnd)));
                if (fresh != null)
                    return (fresh.Hwnd, "fresh:" + fresh.ProcessName + ":" + fresh.Title + " " + DescribeHwndSize(fresh.Hwnd));

                foreach (var name in new[] { "vlc", "wmplayer", "DesktopEditors", "ApplicationFrameHost", "MicrosoftPhotos" })
                {
                    Process[] procs;
                    try { procs = Process.GetProcessesByName(name); }
                    catch { continue; }
                    try
                    {
                        foreach (var p in procs)
                        {
                            try
                            {
                                p.Refresh();
                                var h = p.MainWindowHandle;
                                if (h == IntPtr.Zero) continue;
                                var fg = NativeMethods.GetForegroundWindow();
                                bool titleMatch = !string.IsNullOrWhiteSpace(nameHint)
                                    && GetWindowTitle(h).IndexOf(nameHint, StringComparison.OrdinalIgnoreCase) >= 0;
                                bool newPid = !beforePids.Contains((uint)p.Id);
                                if (h == fg || titleMatch || newPid)
                                    return (h, "proc:" + name + " pid=" + p.Id);
                            }
                            finally { p.Dispose(); }
                        }
                    }
                    finally { foreach (var p in procs) try { p.Dispose(); } catch { } }
                }

                var fg2 = NativeMethods.GetForegroundWindow();
                if (fg2 != IntPtr.Zero && fg2 != _ownHwnd && NativeMethods.IsWindowVisible(fg2) && !beforeHwnds.Contains(fg2))
                {
                    NativeMethods.GetWindowThreadProcessId(fg2, out uint pid);
                    try
                    {
                        using var p = Process.GetProcessById((int)pid);
                        if (playerNames.Any(n => p.ProcessName.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0)
                            || p.ProcessName.Equals("vlc", StringComparison.OrdinalIgnoreCase) || p.ProcessName.Equals("wmplayer", StringComparison.OrdinalIgnoreCase)
                            || p.ProcessName.Equals("DesktopEditors", StringComparison.OrdinalIgnoreCase))
                            return (fg2, "fg:" + p.ProcessName);
                    }
                    catch { }
                }
            }
            catch { }
        }
        return (IntPtr.Zero, "timeout");
    }

    private static string GetWindowTitle(IntPtr hwnd)
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

    private WindowEntry? EntryFromHwnd(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd)) return null;
        try
        {
            NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
            string title = GetWindowTitle(hwnd);
            string procName = "";
            try { using var p = Process.GetProcessById((int)pid); procName = p.ProcessName; } catch { }
            return new WindowEntry
            {
                Hwnd = hwnd,
                Title = string.IsNullOrWhiteSpace(title) ? procName : title,
                ProcessId = pid,
                ProcessName = procName,
                Icon = WindowIconLoader.ForProcess(pid)
            };
        }
        catch { return null; }
    }

    private void PaneHost_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border border || border.Tag is not int index) return;
        if (e.OriginalSource is DependencyObject d)
        {
            var btn = FindAncestor<Button>(d);
            if (btn != null) { _paneDragFromZone = -1; return; } // arrows / menu ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬Ãƒâ€¦Ã‚Â¡ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â not a pane drag
        }
        if (_tiler.GetZone(index) == null) { _paneDragFromZone = -1; return; }
        _paneDragFromZone = index;
        _paneDragStart = e.GetPosition(null);
    }

    private void PaneHost_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_paneDragFromZone < 0 || e.LeftButton != MouseButtonState.Pressed) return;
        var pos = e.GetPosition(null);
        if (Math.Abs(pos.X - _paneDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y - _paneDragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;
        var w = _tiler.GetZone(_paneDragFromZone);
        if (w == null || w.Parked) { _paneDragFromZone = -1; return; }
        int from = _paneDragFromZone;
        _paneDragFromZone = -1;
        BeginDocsFileDragHitThrough();
        try
        {
            DragDrop.DoDragDrop(sender as DependencyObject ?? this, w, DragDropEffects.Move);
            _ = from;
        }
        catch { }
        finally
        {
            EndDocsFileDragHitThrough();
        }
    }

    private void PaneHost_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _paneDragFromZone = -1;
    }

// ---- Lock height (replaces Full H): Height+Top fixed; width free ----

    private const int WmSizing = 0x0214;
    private HwndSource? _hwndSource;

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        try
        {
            _hwndSource = PresentationSource.FromVisual(this) as HwndSource;
            _hwndSource?.AddHook(WndProc);
            _hwndSource?.AddHook(DeskZWndProc);   // v1.0.2: desk raises stay under Linked tiles (MainWindow.DeskZ.cs)
        }
        catch { }
        // v1.0.2: deterministic launch placement - primary monitor work area, centered (MainWindow.Startup.cs).
        PlaceCenteredOnPrimary("SourceInitialized");
        // v1.0.1 collapse: WM_ENTERSIZEMOVE / WM_EXITSIZEMOVE tracking (MainWindow.Collapse.cs).
        InitCollapseFeature();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmSizing && _lockHeight && WindowState != WindowState.Maximized && lParam != IntPtr.Zero)
        {
            try
            {
 // RECT: left, top, right, bottom '--'--'--'-'--'-'--'--'-'--'-'--'-'--'-'-'--'- keep top and height; allow left/right width change.
                var rect = System.Runtime.InteropServices.Marshal.PtrToStructure<NativeMethods.Rect>(lParam);
                int heightPx = rect.Bottom - rect.Top;
                var dpi = VisualTreeHelper.GetDpi(this);
                double sy = dpi.DpiScaleY <= 0 ? 1 : dpi.DpiScaleY;
                int lockedHpx = (int)Math.Round(_lockedHeight * sy);
                if (lockedHpx < 1) lockedHpx = heightPx;
                // Preserve top edge from locked Top when possible.
                int lockedTopPx = (int)Math.Round(_lockedTop * sy);
                rect.Top = lockedTopPx;
                rect.Bottom = rect.Top + lockedHpx;
                System.Runtime.InteropServices.Marshal.StructureToPtr(rect, lParam, false);
                handled = true;
            }
            catch { }
        }
        return IntPtr.Zero;
    }

    private void LockHeight_Click(object sender, RoutedEventArgs e)
    {
        bool want = TbLockHeight?.IsChecked == true;
        if (want == _lockHeight && want)
        {
            // Re-assert lock from UI click.
            ApplyLockHeight(fromToggle: true);
            return;
        }
        _lockHeight = want;
        if (_lockHeight)
            ApplyLockHeight(fromToggle: true);
        else
        {
            SyncSizeUi();
            Persist();
            StatusText.Text = "Lock height OFF";

        }
    }

    /// <summary>
    /// When turning Lock H ON: expand Height to the monitor work area and pin Top.
 // / Never shrink Width '- if leaving Maximize, keep work-area width (not restore bounds).
    /// While locked: Height and Top stay; user may still resize width (sides).
    /// </summary>
    private void ApplyLockHeight(bool fromToggle)
    {
        if (!_lockHeight) return;

        _applyingLockHeight = true;
        _applyingSizePreset = true;
        try
        {
            if (!TryGetWorkAreaDip(out double workLeft, out double workTop, out double workW, out double workH))
            {
                if (WindowState == WindowState.Maximized)
                    WindowState = WindowState.Normal;
                _lockedHeight = Math.Max(MinHeight, Height);
                _lockedTop = Top;
                SyncSizeUi();
                Persist();
                StatusText.Text = "Lock height ON (current height; work area unavailable)";
                return;
            }

            bool wasMaximized = WindowState == WindowState.Maximized;
            if (wasMaximized)
                WindowState = WindowState.Normal;

            // Expand height to work area on toggle / first lock. Keep width.
            bool applyFullH = fromToggle || _lockedHeight < MinHeight || double.IsNaN(_lockedHeight) || _lockedHeight <= 0;
            if (applyFullH)
            {
                double w;
                double left;
                if (wasMaximized || string.Equals(_sizePreset, "full", StringComparison.OrdinalIgnoreCase))
                {
                    // Leaving Full/Maximize: adopt work-area width so we never collapse to tiny restore bounds.
                    w = Math.Max(MinWidth, workW);
                    left = workLeft;
                    if (_sizePreset == "full") _sizePreset = "";
                }
                else
                {
                    // Prefer on-screen size; only clamp if overflowing the work area.
                    w = ActualWidth > MinWidth ? ActualWidth : Width;
                    if (double.IsNaN(w) || w < MinWidth) w = Math.Min(Math.Max(MinWidth, workW * 0.8), workW);
                    w = Math.Min(Math.Max(w, MinWidth), workW);
                    left = Left;
                    if (double.IsNaN(left)) left = workLeft + (workW - w) / 2.0;
                    if (left < workLeft) left = workLeft;
                    if (left + w > workLeft + workW) left = Math.Max(workLeft, workLeft + workW - w);
                }

                Left = left;
                Width = w;
                Top = workTop;
                Height = Math.Max(MinHeight, workH);
            }
            else
            {
 // Re-assert stored lock '- height/top only.
                Top = _lockedTop;
                Height = Math.Max(MinHeight, _lockedHeight);
            }

            _lockedHeight = Height;
            _lockedTop = Top;
            if (_sizePreset == "tall") _sizePreset = "";
            SyncSizeUi();
            Persist();
            AfterDashboardSizeApplied();
            StatusText.Text = "Lock height ON";

        }
        finally
        {
            _applyingSizePreset = false;
            _applyingLockHeight = false;
        }
    }
    private void EnforceLockHeightOnSizeChanged(SizeChangedEventArgs e)
    {
        if (!_lockHeight || _applyingLockHeight || _applyingSizePreset) return;
        if (WindowState == WindowState.Maximized || WindowState == WindowState.Minimized) return;
        if (_lockedHeight < MinHeight) _lockedHeight = Math.Max(MinHeight, Height);

        bool heightChanged = e.HeightChanged && Math.Abs(Height - _lockedHeight) > 0.5;
        bool topDrift = Math.Abs(Top - _lockedTop) > 0.5;
        if (!heightChanged && !topDrift) return;

        _applyingLockHeight = true;
        try
        {
            if (heightChanged) Height = _lockedHeight;
            if (topDrift) Top = _lockedTop;
        }
        finally
        {
            _applyingLockHeight = false;
        }
    }
    // ---- Dashboard size presets (work-area relative; no aspect lock) ----

    private void SyncSizeUi()
    {
        if (TbSizeFull != null) TbSizeFull.IsChecked = _sizePreset == "full";
        if (TbSize90 != null) TbSize90.IsChecked = _sizePreset == "90";
        if (TbSize80 != null) TbSize80.IsChecked = _sizePreset == "80";
        if (TbSize70 != null) TbSize70.IsChecked = _sizePreset == "70";
        if (TbLockHeight != null) TbLockHeight.IsChecked = _lockHeight;
    }

    private void SizePreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not string tag) return;
        ApplyDashboardSizePreset(tag);
    }

    /// <summary>
    /// Resize Dashboard relative to the current monitor work area.
    /// Full = Maximize (or full width when Lock H is on).
 // / With Lock H on, 90%/80%/70% change width only '- height/top stay locked.
    /// </summary>
    private void ApplyDashboardSizePreset(string preset)
    {
        if (string.IsNullOrWhiteSpace(preset)) return;
        preset = preset.Trim().ToLowerInvariant();
        if (preset is not ("full" or "90" or "80" or "70")) return;

        _sizePreset = preset;
        // Only unlock Lock H when applying a size that must redefine height without lock intent.
        // 90/80/70 + Lock H => width-only. Full + Lock H => full work-area width, height stays.
        bool keepLock = _lockHeight && preset is ("full" or "90" or "80" or "70");
        if (!keepLock)
            _lockHeight = false;
        SyncSizeUi();
        Persist();

        _applyingSizePreset = true;
        try
        {
            if (preset == "full" && !_lockHeight)
            {
                WindowState = WindowState.Maximized;
                StatusText.Text = "Size: Full (work area)";
            }
            else
            {
                if (!TryGetWorkAreaDip(out double workLeft, out double workTop, out double workW, out double workH))
                {
                    StatusText.Text = "Size: could not read monitor work area";
                    return;
                }

                if (WindowState == WindowState.Maximized)
                    WindowState = WindowState.Normal;

                if (_lockHeight)
                {
                    if (_lockedHeight < MinHeight) _lockedHeight = Math.Max(MinHeight, Height);
                    double w;
                    if (preset == "full")
                        w = Math.Max(MinWidth, workW);
                    else
                    {
                        double pct = preset switch
                        {
                            "90" => 0.90,
                            "80" => 0.80,
                            _ => 0.70
                        };
                        w = Math.Max(MinWidth, Math.Min(workW * pct, workW));
                    }
                    double left = workLeft + (workW - w) / 2.0;
                    Left = left;
                    Width = w;
                    Top = _lockedTop;
                    Height = Math.Max(MinHeight, _lockedHeight);
                    StatusText.Text = preset == "full"
                        ? "Size: Full width (height locked)"
                        : $"Size: {preset}% width (height locked)";
                }
                else
                {
                    double pct = preset switch
                    {
                        "90" => 0.90,
                        "80" => 0.80,
                        _ => 0.70
                    };

                    double w = Math.Max(MinWidth, Math.Min(workW * pct, workW));
                    double h = Math.Max(MinHeight, Math.Min(workH * pct, workH));
                    double left = workLeft + (workW - w) / 2.0;
                    double top = workTop + (workH - h) / 2.0;

                    Left = left;
                    Top = top;
                    Width = w;
                    Height = h;

                    StatusText.Text = $"Size: {preset}% of work area";
                }
            }

            AfterDashboardSizeApplied();
        }
        finally
        {
            _applyingSizePreset = false;
        }
    }
    private bool TryGetWorkAreaDip(out double left, out double top, out double width, out double height)
    {
        left = top = width = height = 0;
        try
        {
            if (_ownHwnd == IntPtr.Zero)
                _ownHwnd = new WindowInteropHelper(this).Handle;
            if (!NativeMethods.TryGetMonitorWorkArea(_ownHwnd, out var workPx, out _))
                return false;
            var dpi = VisualTreeHelper.GetDpi(this);
            double sx = dpi.DpiScaleX <= 0 ? 1 : dpi.DpiScaleX;
            double sy = dpi.DpiScaleY <= 0 ? 1 : dpi.DpiScaleY;
            left = workPx.Left / sx;
            top = workPx.Top / sy;
            width = workPx.Width / sx;
            height = workPx.Height / sy;
            return width > 50 && height > 50;
        }
        catch
        {
            return false;
        }
    }

    private void AfterDashboardSizeApplied()
    {
        try
        {
            UpdateLayout();
            PaneGrid?.UpdateLayout();
        }
        catch { }

        if (_liveDesk)
        {
 // Size/Lock changes often leave Live tiles blank until click '- heal immediately.
            if (_fitMode == "fill")
            {
                for (int i = 0; i < _paneCount; i++)
                {
                    var w = _tiler.GetZone(i);
                    if (w != null && w.PreviewProxy && !w.Parked && !w.HasCrop && !w.Collapsed && !w.Floating)
                        EnterLiveFill(w);
                }
            }
            ApplyLiveTiling();
            AssertAllAssignedLiveTiles(IntPtr.Zero, focus: false);
        }
        RepairAllTilesZOrderAboveDashboard(keepFocusHwnd: IntPtr.Zero);

        Dispatcher.BeginInvoke(() =>
        {
            try
            {
                UpdateLayout();
                PaneGrid?.UpdateLayout();
            }
            catch { }
            if (_liveDesk)
            {
                ApplyLiveTiling();
                AssertAllAssignedLiveTiles(IntPtr.Zero, focus: false);
            }
            RepairAllTilesZOrderAboveDashboard(keepFocusHwnd: IntPtr.Zero);
            PostLayoutLiveHeal();
        }, DispatcherPriority.Loaded);
    }
    // ---- UI zoom (optional; LayoutTransform on root). Pane-follow resize is primary. ----

    private const double ZoomMin = 0.8;
    private const double ZoomMax = 1.5;
    private const double ZoomStep = 0.1;

    private void ZoomIn_Click(object sender, RoutedEventArgs e) => SetUiZoom(_uiZoom + ZoomStep);
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => SetUiZoom(_uiZoom - ZoomStep);
    private void ZoomReset_Click(object sender, RoutedEventArgs e) => SetUiZoom(1.0);

    private void SetUiZoom(double zoom)
    {
        _uiZoom = Math.Clamp(Math.Round(zoom, 2), ZoomMin, ZoomMax);
        ApplyUiZoom(retile: true);
        Persist();
        StatusText.Text = $"UI zoom {(_uiZoom * 100):0}% (desk chrome only)";

    }

    private void ApplyUiZoom(bool retile)
    {
        if (RootDock != null)
            RootDock.LayoutTransform = Math.Abs(_uiZoom - 1.0) < 0.001
                ? Transform.Identity
                : new ScaleTransform(_uiZoom, _uiZoom);

        if (BtnZoomLabel != null)
            BtnZoomLabel.Content = $"{(_uiZoom * 100):0}%";

        if (BtnZoomOut != null)
            BtnZoomOut.IsEnabled = _uiZoom > ZoomMin + 0.001;
        if (BtnZoomIn != null)
            BtnZoomIn.IsEnabled = _uiZoom < ZoomMax - 0.001;

        if (retile && IsLoaded)
        {
            Dispatcher.BeginInvoke(() =>
            {
                try { RootDock?.UpdateLayout(); } catch { }
                RefreshAllThumbnails();
                if (_liveDesk) ApplyLiveTiling();
            }, DispatcherPriority.Loaded);
        }
    }
    // ---- Unlink drop zone (side panel) ----

    private void UnlinkZone_DragOver(object sender, DragEventArgs e)
    {
        bool ok = e.Data.GetDataPresent(typeof(WindowEntry)) || e.Data.GetDataPresent(typeof(WorkingWindow));
        e.Effects = ok ? DragDropEffects.Move : DragDropEffects.None;
        if (ok && sender is Border b)
            b.BorderBrush = (Brush)FindResource("AccentBrush");
        e.Handled = true;
    }

    private void UnlinkZone_DragLeave(object sender, DragEventArgs e)
    {
        if (UnlinkDropZone != null)
            UnlinkDropZone.BorderBrush = (Brush)FindResource("AccentBrush");
    }

    private void UnlinkZone_Drop(object sender, DragEventArgs e)
    {
        if (UnlinkDropZone != null)
            UnlinkDropZone.BorderBrush = (Brush)FindResource("AccentBrush");

        if (e.Data.GetDataPresent(typeof(WorkingWindow)) && e.Data.GetData(typeof(WorkingWindow)) is WorkingWindow ww)
        {
            UnlinkWorking(ww);
            e.Handled = true;
            return;
        }

        if (e.Data.GetDataPresent(typeof(WindowEntry)) && e.Data.GetData(typeof(WindowEntry)) is WindowEntry entry)
        {
            var existing = _tiler.WorkingSet.FirstOrDefault(w => w.Hwnd == entry.Hwnd);
            if (existing != null)
                UnlinkWorking(existing);
            else
                StatusText.Text = $"{entry.ShortTitle} is not linked to a pane.";
            e.Handled = true;
        }
    }

}













