using System.Runtime.InteropServices;
using System.Text;

namespace WorkflowDashboard;

internal static class NativeMethods
{
    public const int GWL_EXSTYLE = -20;
    public const int GWL_STYLE = -16;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_APPWINDOW = 0x00040000;
    public const int WS_EX_TOPMOST = 0x00000008;
    public const int WS_EX_TRANSPARENT = 0x00000020;
    public const int WS_CHILD = 0x40000000;
    public const int WS_VISIBLE = 0x10000000;
    public const int WS_MAXIMIZE = 0x01000000;
    public const int WS_MINIMIZE = 0x20000000;

    public const uint SW_HIDE = 0;
    public const uint SW_SHOWNORMAL = 1;
    public const uint SW_SHOWMINIMIZED = 2;
    public const uint SW_MAXIMIZE = 3;
    public const uint SW_SHOWMAXIMIZED = 3;
    public const uint SW_SHOWNOACTIVATE = 4;
    public const uint SW_SHOWMINNOACTIVE = 7;
    public const uint SW_SHOW = 5;
    public const uint SW_MINIMIZE = 6;
    public const uint SW_RESTORE = 9;
    public const uint WM_CLOSE = 0x0010;

    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOREDRAW = 0x0008;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_FRAMECHANGED = 0x0020;
    public const uint SWP_SHOWWINDOW = 0x0040;
    public const uint SWP_ASYNCWINDOWPOS = 0x4000;

    /// <summary>Places the window at the top of the Z order (above non-topmost peers).</summary>
    public static readonly IntPtr HWND_TOP = IntPtr.Zero;
    public static readonly IntPtr HWND_BOTTOM = new(1);
    public static readonly IntPtr HWND_TOPMOST = new(-1);
    public static readonly IntPtr HWND_NOTOPMOST = new(-2);

    /// <summary>Minimum tiled visible size (physical px floor; callers may pass DIP-scaled values).</summary>
    public const int MinTileWidth = 280;
    public const int MinTileHeight = 200;

    public const uint WM_GETMINMAXINFO = 0x0024;
    public const int SM_CXMINTRACK = 34;
    public const int SM_CYMINTRACK = 35;
    public const int SM_CXSCREEN = 0;
    public const int SM_CYSCREEN = 1;

    public const int DWMWA_CLOAKED = 14;
    public const int DWMWA_CLOAK = 13;
    public const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    public const uint GA_ROOT = 2;
    public const uint MONITOR_DEFAULTTONEAREST = 2;

    public const uint DWM_TNP_RECTDESTINATION = 0x00000001;
    public const uint DWM_TNP_RECTSOURCE = 0x00000002;
    public const uint DWM_TNP_VISIBLE = 0x00000008;
    public const uint DWM_TNP_OPACITY = 0x00000004;
    public const uint DWM_TNP_SOURCECLIENTAREAONLY = 0x00000010;

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    public static extern IntPtr GetShellWindow();

    [DllImport("user32.dll")]
    public static extern IntPtr WindowFromPoint(POINT pt);

    [DllImport("user32.dll")]
    public static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool IsZoomed(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, uint nCmdShow);

    [DllImport("user32.dll")]
    public static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    public static extern bool AllowSetForegroundWindow(uint dwProcessId);

    public const uint ASFW_ANY = 0xFFFFFFFF;

    [DllImport("user32.dll")]
    public static extern bool FlashWindow(IntPtr hWnd, bool bInvert);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    private const byte VK_MENU = 0x12;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    /// <summary>Which ForceForeground path last succeeded (diagnostics).</summary>
    public static string LastForegroundTechnique { get; private set; } = "";

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern IntPtr GetWindowLong32(IntPtr hWnd, int nIndex);

    public static IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex)
        => IntPtr.Size == 8 ? GetWindowLongPtr64(hWnd, nIndex) : GetWindowLong32(hWnd, nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern IntPtr SetWindowLong32(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    public static IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong)
        => IntPtr.Size == 8 ? SetWindowLongPtr64(hWnd, nIndex, dwNewLong) : SetWindowLong32(hWnd, nIndex, dwNewLong);

    [DllImport("user32.dll")]
    public static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

    [DllImport("user32.dll")]
    public static extern bool ScreenToClient(IntPtr hWnd, ref POINT lpPoint);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out Rect lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter,
        int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, ref MINMAXINFO lParam);

    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfoEx lpmi);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateWindowEx(
        int dwExStyle,
        string lpClassName,
        string lpWindowName,
        int dwStyle,
        int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent,
        IntPtr hMenu,
        IntPtr hInstance,
        IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool MoveWindow(IntPtr hWnd, int x, int y, int nWidth, int nHeight, bool bRepaint);

    [DllImport("dwmapi.dll")]
    public static extern int DwmRegisterThumbnail(IntPtr dest, IntPtr src, out IntPtr thumb);

    [DllImport("dwmapi.dll")]
    public static extern int DwmUnregisterThumbnail(IntPtr thumb);

    [DllImport("dwmapi.dll")]
    public static extern int DwmUpdateThumbnailProperties(IntPtr thumb, ref DwmThumbnailProperties props);

    [DllImport("dwmapi.dll")]
    public static extern int DwmQueryThumbnailSourceSize(IntPtr thumb, out SIZE pSize);

    [DllImport("dwmapi.dll")]
    public static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    public static extern int DwmGetWindowAttributeRect(IntPtr hwnd, int dwAttribute, out Rect pvAttribute, int cbAttribute);

    [DllImport("dwmapi.dll")]
    public static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SIZE
    {
        public int cx;
        public int cy;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        public int Left, Top, Right, Bottom;

        public int Width => Right - Left;
        public int Height => Bottom - Top;

        public bool IsEmpty => Width <= 0 || Height <= 0;

        public override string ToString() => $"{Left},{Top} {Width}x{Height}";
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MonitorInfoEx
    {
        public int cbSize;
        public Rect rcMonitor;
        public Rect rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DwmThumbnailProperties
    {
        public uint Flags;
        public Rect Destination;
        public Rect Source;
        public byte Opacity;
        [MarshalAs(UnmanagedType.Bool)]
        public bool Visible;
        [MarshalAs(UnmanagedType.Bool)]
        public bool SourceClientAreaOnly;
    }

    /// <summary>
    /// Query the window's minimum tracking size via WM_GETMINMAXINFO.
    /// Falls back to <see cref="MinTileWidth"/>Ãƒâ€”<see cref="MinTileHeight"/> when the app
    /// reports nothing useful. Effective min is at least the Live desk floor.
    /// </summary>
    /// <summary>
    /// Query min/max tracking size via WM_GETMINMAXINFO.
    /// Min falls back to Live desk floor. Max of 0 means "unbounded / not reported".
    /// </summary>
    public static void GetWindowTrackSize(IntPtr hwnd, out int minWidth, out int minHeight, out int maxWidth, out int maxHeight)
    {
        minWidth = MinTileWidth;
        minHeight = MinTileHeight;
        maxWidth = 0;
        maxHeight = 0;
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd)) return;

        int sysW = GetSystemMetrics(SM_CXMINTRACK);
        int sysH = GetSystemMetrics(SM_CYMINTRACK);
        var mmi = new MINMAXINFO
        {
            ptMinTrackSize = new POINT { X = Math.Max(1, sysW), Y = Math.Max(1, sysH) }
        };

        try
        {
            SendMessage(hwnd, WM_GETMINMAXINFO, IntPtr.Zero, ref mmi);
        }
        catch
        {
            return;
        }

        int w = mmi.ptMinTrackSize.X;
        int h = mmi.ptMinTrackSize.Y;
        if (w > 0 && w < 8000)
            minWidth = Math.Max(MinTileWidth, w);
        if (h > 0 && h < 8000)
            minHeight = Math.Max(MinTileHeight, h);

        int mw = mmi.ptMaxTrackSize.X;
        int mh = mmi.ptMaxTrackSize.Y;
        // Treat 0 / huge values as unbounded.
        if (mw > 0 && mw < 20000) maxWidth = mw;
        if (mh > 0 && mh < 20000) maxHeight = mh;
    }

    public static void GetWindowMinTrackSize(IntPtr hwnd, out int minWidth, out int minHeight)
    {
        GetWindowTrackSize(hwnd, out minWidth, out minHeight, out _, out _);
    }

    /// <summary>
    /// OS-reported min track WITHOUT Live desk MinTileWidth floor.
    /// Used to grow zones so HardClamp can succeed (Electron/Opera often refuse below ~500px).
    /// </summary>
    public static void GetWindowMinTrackSizeRaw(IntPtr hwnd, out int minWidth, out int minHeight)
    {
        minWidth = Math.Max(1, GetSystemMetrics(SM_CXMINTRACK));
        minHeight = Math.Max(1, GetSystemMetrics(SM_CYMINTRACK));
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd)) return;
        var mmi = new MINMAXINFO
        {
            ptMinTrackSize = new POINT { X = minWidth, Y = minHeight }
        };
        try { SendMessage(hwnd, WM_GETMINMAXINFO, IntPtr.Zero, ref mmi); }
        catch { return; }
        int w = mmi.ptMinTrackSize.X;
        int h = mmi.ptMinTrackSize.Y;
        if (w > 0 && w < 8000) minWidth = Math.Max(1, w);
        if (h > 0 && h < 8000) minHeight = Math.Max(1, h);
    }

    /// <summary>
    /// True when min track is close to max track (fixed / nearly-fixed size apps like Movavi).
    /// Also true when max is set and within 48px of min on both axes.
    /// </summary>
    public static bool IsFixedSizeWindow(IntPtr hwnd)
    {
        GetWindowTrackSize(hwnd, out int minW, out int minH, out int maxW, out int maxH);
        bool wFixed = maxW > 0 && maxW - minW <= 48;
        bool hFixed = maxH > 0 && maxH - minH <= 48;
        // Some apps report only one axis fixed; treat as fixed if either axis is locked
        // and the other is also bounded tightly OR both bounded.
        if (wFixed && hFixed) return true;
        if (wFixed && maxH > 0 && maxH - minH <= 120) return true;
        if (hFixed && maxW > 0 && maxW - minW <= 120) return true;
        return false;
    }

    /// <summary>
    /// Center (width x height) inside pane. HARD CLAMP: size never exceeds pane; result always subset of pane.
    /// When desired is larger than pane, shrink to pane (FitWidth/FitHeight) - never spill into neighbors.
    /// </summary>
    public static Rect CenterRectInPane(Rect pane, int width, int height)
    {
        if (pane.IsEmpty || pane.Width < 1 || pane.Height < 1)
            return pane;
        width = Math.Max(1, Math.Min(width, pane.Width));
        height = Math.Max(1, Math.Min(height, pane.Height));
        int x = pane.Left + (pane.Width - width) / 2;
        int y = pane.Top + (pane.Height - height) / 2;
        if (x < pane.Left) x = pane.Left;
        if (y < pane.Top) y = pane.Top;
        if (x + width > pane.Right) x = pane.Right - width;
        if (y + height > pane.Bottom) y = pane.Bottom - height;
        if (x < pane.Left) x = pane.Left;
        if (y < pane.Top) y = pane.Top;
        return new Rect
        {
            Left = x,
            Top = y,
            Right = x + width,
            Bottom = y + height
        };
    }

    /// <summary>Intersect/clamp place so it lies entirely inside zone (hard clamp geometry).</summary>
    public static Rect ClampRectInsideZone(Rect place, Rect zone)
    {
        if (zone.IsEmpty || zone.Width < 1 || zone.Height < 1) return zone;
        if (place.IsEmpty) return zone;
        int w = Math.Max(1, Math.Min(place.Width, zone.Width));
        int h = Math.Max(1, Math.Min(place.Height, zone.Height));
        int x = place.Left;
        int y = place.Top;
        if (x < zone.Left) x = zone.Left;
        if (y < zone.Top) y = zone.Top;
        if (x + w > zone.Right) x = zone.Right - w;
        if (y + h > zone.Bottom) y = zone.Bottom - h;
        if (x < zone.Left) x = zone.Left;
        if (y < zone.Top) y = zone.Top;
        return new Rect { Left = x, Top = y, Right = x + w, Bottom = y + h };
    }

    public static bool PaneFitsMin(Rect pane, int minWidth, int minHeight)
        => !pane.IsEmpty && pane.Width >= minWidth && pane.Height >= minHeight;

    /// <summary>
    /// Fill when min-track fits the zone; otherwise FitWidth and/or FitHeight —
    /// place rect is ALWAYS a subset of the zone (hard clamp). Centered inside zone.
    /// Pure geometry (no HWND) for smoke tests and placement.
    /// Modes: fill | fitwidth | fitheight | fitboth.
    /// </summary>
    public static Rect ComputeFillOrFitWidthRect(Rect zone, int minW, int minH, out string mode)
    {
        mode = "fill";
        if (zone.IsEmpty || zone.Width < 1 || zone.Height < 1)
            return zone;
        minW = Math.Max(1, minW);
        minH = Math.Max(1, minH);
        bool tooThin = zone.Width < minW;
        bool tooShort = zone.Height < minH;
        if (!tooThin && !tooShort)
        {
            mode = "fill";
            return zone;
        }

        if (tooThin && tooShort)
        {
            // Both axes too small for min-track: request full zone (OS may still grow; Expand recovers).
            mode = "fitboth";
            return new Rect
            {
                Left = zone.Left,
                Top = zone.Top,
                Right = zone.Right,
                Bottom = zone.Bottom
            };
        }

        if (tooThin)
        {
            mode = "fitwidth";
            int placeW = Math.Max(1, zone.Width);
            // Use full zone height when min-width cannot fit (maximize vertical use inside zone).
            int placeH = Math.Max(1, zone.Height);
            return CenterRectInPane(zone, placeW, placeH);
        }

        // tooShort only
        mode = "fitheight";
        int placeH2 = Math.Max(1, zone.Height);
        int placeW2 = Math.Max(1, zone.Width);
        return CenterRectInPane(zone, placeW2, placeH2);
    }

    /// <summary>Apply ComputeFillOrFitWidthRect via SetWindowPos (outer). Hard-clamps after place.</summary>
    public static bool PlaceFillOrFitWidth(IntPtr hwnd, Rect zone, out string mode)
    {
        mode = "fill";
        if (hwnd == IntPtr.Zero || zone.IsEmpty || !IsWindow(hwnd)) return false;
        GetWindowMinTrackSize(hwnd, out int minW, out int minH);
        var place = ComputeFillOrFitWidthRect(zone, minW, minH, out mode);
        // Absolute: never request a rect that crosses zone edges.
        place = ClampRectInsideZone(place, zone);

        if (string.Equals(mode, "fill", StringComparison.OrdinalIgnoreCase))
        {
            bool filled = ForceFillVisibleFrame(hwnd, zone);
            if (VisibleExceedsZone(hwnd, zone, tol: 2))
                HardClampOuterToZone(hwnd, zone);
            return filled || !VisibleExceedsZone(hwnd, zone, tol: 2);
        }

        if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);
        if (IsZoomed(hwnd)) ShowWindow(hwnd, SW_RESTORE);
        long style = GetWindowLongPtr(hwnd, GWL_STYLE).ToInt64();
        if ((style & WS_MAXIMIZE) != 0)
        {
            style &= ~WS_MAXIMIZE;
            SetWindowLongPtr(hwnd, GWL_STYLE, new IntPtr(style));
        }
        if (!IsOuterAt(hwnd, place, tol: 2))
        {
            SetWindowPos(hwnd, HWND_TOP, place.Left, place.Top,
                Math.Max(1, place.Width), Math.Max(1, place.Height),
                SWP_NOACTIVATE | SWP_SHOWWINDOW);
        }
        // OS min-track may grow the window past the zone — re-clamp into zone only.
        if (VisibleExceedsZone(hwnd, zone, tol: 2))
            HardClampOuterToZone(hwnd, zone);
        return true;
    }

    /// <summary>
    /// After SetWindowPos, force outer HWND into zone. Prefers ForceFill (size==zone).
    /// Linked rect must not cover neighbors/sidebar; Expand pane N recovers when min &gt; zone.
    /// </summary>

    public static bool HardClampOuterToZone(IntPtr hwnd, Rect zone)
    {
        if (hwnd == IntPtr.Zero || zone.IsEmpty || !IsWindow(hwnd)) return false;
        if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);
        if (IsZoomed(hwnd)) ShowWindow(hwnd, SW_RESTORE);
        long style = GetWindowLongPtr(hwnd, GWL_STYLE).ToInt64();
        if ((style & WS_MAXIMIZE) != 0)
        {
            style &= ~WS_MAXIMIZE;
            SetWindowLongPtr(hwnd, GWL_STYLE, new IntPtr(style));
        }

        int zw = Math.Max(1, zone.Width);
        int zh = Math.Max(1, zone.Height);

        // Pass 1: classic outer = zone.
        ForceFillVisibleFrame(hwnd, zone);
        if (!VisibleExceedsZone(hwnd, zone, tol: 2))
            return true;

        // Pass 2: FRAMECHANGED (some Chromium builds ignore the first pos).
        SetWindowPos(hwnd, HWND_TOP, zone.Left, zone.Top, zw, zh,
            SWP_NOACTIVATE | SWP_SHOWWINDOW | SWP_FRAMECHANGED);
        if (!VisibleExceedsZone(hwnd, zone, tol: 2))
            return true;

        // Pass 3: MoveWindow (different user32 path).
        MoveWindow(hwnd, zone.Left, zone.Top, zw, zh, true);
        if (!VisibleExceedsZone(hwnd, zone, tol: 2))
            return true;

        // Pass 4: strip WS_THICKFRAME briefly to defeat stubborn min-track, then restore.
        const long WS_THICKFRAME = 0x00040000L;
        long st = GetWindowLongPtr(hwnd, GWL_STYLE).ToInt64();
        bool stripped = (st & WS_THICKFRAME) != 0;
        if (stripped)
            SetWindowLongPtr(hwnd, GWL_STYLE, new IntPtr(st & ~WS_THICKFRAME));
        SetWindowPos(hwnd, HWND_TOP, zone.Left, zone.Top, zw, zh,
            SWP_NOACTIVATE | SWP_SHOWWINDOW | SWP_FRAMECHANGED);
        if (stripped)
        {
            SetWindowLongPtr(hwnd, GWL_STYLE, new IntPtr(st));
            SetWindowPos(hwnd, HWND_TOP, zone.Left, zone.Top, zw, zh,
                SWP_NOACTIVATE | SWP_SHOWWINDOW | SWP_FRAMECHANGED);
        }

        if (!VisibleExceedsZone(hwnd, zone, tol: 2))
            return true;

        // Pass 5: if size still refuses, pin left/top inside zone and request zone size again
        // (some Chromium builds accept the second geometry after FRAMECHANGED).
        if (GetWindowRect(hwnd, out var still) && !still.IsEmpty)
        {
            int x = Math.Max(zone.Left, Math.Min(still.Left, zone.Right - Math.Max(1, Math.Min(still.Width, zone.Width))));
            int y = Math.Max(zone.Top, Math.Min(still.Top, zone.Bottom - Math.Max(1, Math.Min(still.Height, zone.Height))));
            SetWindowPos(hwnd, HWND_TOP, x, y, zw, zh,
                SWP_NOACTIVATE | SWP_SHOWWINDOW | SWP_FRAMECHANGED);
            if (!VisibleExceedsZone(hwnd, zone, tol: 2))
                return true;
            // Last resort: request exact zone outer again after a tiny MoveWindow nudge.
            MoveWindow(hwnd, zone.Left, zone.Top, zw, zh, true);
            SetWindowPos(hwnd, HWND_TOP, zone.Left, zone.Top, zw, zh,
                SWP_NOACTIVATE | SWP_SHOWWINDOW | SWP_FRAMECHANGED);
        }
        return !VisibleExceedsZone(hwnd, zone, tol: 2);
    }

    public static bool IsCloaked(IntPtr hwnd)
    {
        if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) != 0)
            return false;
        return cloaked != 0;
    }

    public static bool TryGetMonitorWorkArea(IntPtr hwnd, out Rect work, out Rect monitor)
    {
        work = default;
        monitor = default;
        var hMon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (hMon == IntPtr.Zero) return false;
        var info = new MonitorInfoEx { cbSize = Marshal.SizeOf<MonitorInfoEx>() };
        if (!GetMonitorInfo(hMon, ref info)) return false;
        work = info.rcWork;
        monitor = info.rcMonitor;
        return true;
    }

    public static bool TryGetVisibleFrame(IntPtr hwnd, out Rect frame)
    {
        frame = default;
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd)) return false;
        if (DwmGetWindowAttributeRect(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out frame, Marshal.SizeOf<Rect>()) == 0
            && !frame.IsEmpty)
            return true;
        return GetWindowRect(hwnd, out frame) && !frame.IsEmpty;
    }

    /// <summary>True when visible DWM frame already matches target within a few pixels.</summary>
    public static bool IsVisiblyAt(IntPtr hwnd, Rect targetVisible, int tol = 2)
    {
        if (targetVisible.IsEmpty || !TryGetVisibleFrame(hwnd, out var frame)) return false;
        return Math.Abs(frame.Left - targetVisible.Left) <= tol
            && Math.Abs(frame.Top - targetVisible.Top) <= tol
            && Math.Abs(frame.Right - targetVisible.Right) <= tol
            && Math.Abs(frame.Bottom - targetVisible.Bottom) <= tol;
    }

    /// <summary>
    /// Place a top-level window so its *visible* frame (DWM extended bounds) matches
    /// the target rect in physical pixels. Compensates for invisible resize borders.
    /// Skips SetWindowPos when already correct (avoids DWM black flashes).
    /// Never hides or minimizes the window. Raises above peers via HWND_TOP without
    /// activating unless <paramref name="activate"/> is true.
    /// </summary>
    /// <summary>
    /// Place a top-level window into a pane.
    /// fitMode "fill" = stretch to pane (stretchable apps); "fit" = aspect-preserving bounds
    /// inside the pane, centered (OBS-style window fit Ã¢â‚¬â€ not content/DPI magnify).
    /// Fixed-size apps always use Fit-style centering (never stretched beyond natural size).
    /// </summary>

    /// <summary>
    /// <summary>
    /// Flush fill: place so the *visible* DWM frame matches the pane (compensates
    /// invisible Win10/11 resize borders). Outer may extend slightly into the
    /// inter-pane gutter; spill checks use the visible frame, not outer.
    /// </summary>
    public static bool ForceFillVisibleFrame(IntPtr hwnd, Rect targetVisible)
    {
        if (hwnd == IntPtr.Zero || targetVisible.IsEmpty || !IsWindow(hwnd)) return false;
        if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);
        if (IsZoomed(hwnd)) ShowWindow(hwnd, SW_RESTORE);
        long style = GetWindowLongPtr(hwnd, GWL_STYLE).ToInt64();
        if ((style & WS_MAXIMIZE) != 0)
        {
            style &= ~WS_MAXIMIZE;
            SetWindowLongPtr(hwnd, GWL_STYLE, new IntPtr(style));
        }
        if (IsVisiblyAt(hwnd, targetVisible, tol: 2))
            return true;
        return SetVisibleFrame(hwnd, targetVisible, activate: false);
    }

    /// <summary>True when GetWindowRect (outer) matches target within tol.</summary>
    public static bool IsOuterAt(IntPtr hwnd, Rect target, int tol = 2)
    {
        if (hwnd == IntPtr.Zero || target.IsEmpty || !GetWindowRect(hwnd, out var r)) return false;
        return Math.Abs(r.Left - target.Left) <= tol
            && Math.Abs(r.Top - target.Top) <= tol
            && Math.Abs(r.Right - target.Right) <= tol
            && Math.Abs(r.Bottom - target.Bottom) <= tol;
    }

    /// <summary>True if outer rect spills past zone (sibling overlap risk).</summary>
    public static bool OuterExceedsZone(IntPtr hwnd, Rect zone, int tol = 2)
    {
        if (hwnd == IntPtr.Zero || zone.IsEmpty || !GetWindowRect(hwnd, out var r)) return false;
        return r.Left < zone.Left - tol || r.Top < zone.Top - tol
            || r.Right > zone.Right + tol || r.Bottom > zone.Bottom + tol;
    }
    /// <summary>True if the *visible* DWM frame spills past the zone (sibling overlap risk).</summary>
    public static bool VisibleExceedsZone(IntPtr hwnd, Rect zone, int tol = 2)
    {
        if (hwnd == IntPtr.Zero || zone.IsEmpty) return false;
        if (TryGetVisibleFrame(hwnd, out var frame) && !frame.IsEmpty)
            return frame.Left < zone.Left - tol || frame.Top < zone.Top - tol
                || frame.Right > zone.Right + tol || frame.Bottom > zone.Bottom + tol;
        return OuterExceedsZone(hwnd, zone, tol);
    }

    public static bool TileWindow(IntPtr hwnd, Rect targetVisible, bool activate, string fitMode = "fill")
    {
        if (hwnd == IntPtr.Zero || targetVisible.IsEmpty) return false;
        if (!IsWindow(hwnd)) return false;

        bool wantFill = string.Equals(fitMode, "fill", StringComparison.OrdinalIgnoreCase);

        GetWindowTrackSize(hwnd, out int winMinW, out int winMinH, out int winMaxW, out int winMaxH);
        // Fit: aspect-fit even when pane < app min (tall side panes / WMP). Letterbox preferred over Fill crop.

        if (IsIconic(hwnd))
            ShowWindow(hwnd, SW_RESTORE);

        if (IsZoomed(hwnd))
            ShowWindow(hwnd, SW_RESTORE);

        long style = GetWindowLongPtr(hwnd, GWL_STYLE).ToInt64();
        if ((style & WS_MAXIMIZE) != 0)
        {
            style &= ~WS_MAXIMIZE;
            SetWindowLongPtr(hwnd, GWL_STYLE, new IntPtr(style));
        }

        // Fill: Tim wants force-stretch into pane (WMP/Legacy etc. often report fixed track sizes).
        // Flush fill: DWM-compensate so visible frame matches pane; spill checks use visible bounds.
        if (wantFill)
        {
            bool filled = ForceFillVisibleFrame(hwnd, targetVisible);
            if (VisibleExceedsZone(hwnd, targetVisible, tol: 2))
                filled = HardClampOuterToZone(hwnd, targetVisible) || filled;
            RaiseAbovePeersNoActivate(hwnd);
            if (activate) FocusWindow(hwnd);
            return filled && !VisibleExceedsZone(hwnd, targetVisible, tol: 2);
        }

        bool fixedSize = !wantFill && IsFixedSizeWindow(hwnd);
        Rect place = targetVisible;

        if (targetVisible.Width < 1 || targetVisible.Height < 1)
            return false;

        if (fixedSize || !wantFill)
        {
            // Fit (or fixed-size): scale window bounds to fit inside pane, preserve aspect, center.
            // Never exceed pane. Not a pannable viewport Ã¢â‚¬â€ sized-to-fit centered only.
            int useW = Math.Max(1, winMinW);
            int useH = Math.Max(1, winMinH);
            // Prefer visible frame for aspect (stable vs shadow/resize border jitter).
            if (TryGetVisibleFrame(hwnd, out var vf) && vf.Width >= 50 && vf.Height >= 50)
            {
                useW = vf.Width;
                useH = vf.Height;
            }
            else if (GetWindowRect(hwnd, out var cur) && cur.Width >= 50 && cur.Height >= 50)
            {
                useW = cur.Width;
                useH = cur.Height;
            }
            if (winMaxW > 0) useW = Math.Min(useW, winMaxW);
            if (winMaxH > 0) useH = Math.Min(useH, winMaxH);
            useW = Math.Max(1, useW);
            useH = Math.Max(1, useH);

            if (fixedSize)
            {
                // Prefer min track as the true fixed size when max~min.
                if (winMaxW > 0 && winMaxW - winMinW <= 48) useW = winMinW;
                if (winMaxH > 0 && winMaxH - winMinH <= 48) useH = winMinH;
                useW = Math.Max(winMinW, Math.Min(useW, targetVisible.Width));
                useH = Math.Max(winMinH, Math.Min(useH, targetVisible.Height));
                if (useW < 1 || useH < 1) return false;
                place = CenterRectInPane(targetVisible, useW, useH);
            }
            else
            {
                // Aspect-fit: uniform scale so both axes fit in pane; never exceed pane.
                double scale = Math.Min(
                    (double)targetVisible.Width / useW,
                    (double)targetVisible.Height / useH);
                if (scale <= 0 || double.IsNaN(scale) || double.IsInfinity(scale))
                    scale = 1.0;

                int fitW = (int)Math.Round(useW * scale);
                int fitH = (int)Math.Round(useH * scale);
                if (winMaxW > 0) fitW = Math.Min(fitW, winMaxW);
                if (winMaxH > 0) fitH = Math.Min(fitH, winMaxH);
                fitW = Math.Min(Math.Max(1, fitW), targetVisible.Width);
                fitH = Math.Min(Math.Max(1, fitH), targetVisible.Height);

                // Re-assert aspect after min/max/pane clamps (shrink only Ã¢â‚¬â€ never exceed pane).
                if (useW > 0 && useH > 0 && fitW > 0 && fitH > 0)
                {
                    double targetAspect = (double)useW / useH;
                    double gotAspect = (double)fitW / fitH;
                    if (Math.Abs(gotAspect - targetAspect) > 0.02)
                    {
                        if (gotAspect > targetAspect)
                            fitW = Math.Max(1, (int)Math.Round(fitH * targetAspect));
                        else
                            fitH = Math.Max(1, (int)Math.Round(fitW / targetAspect));
                        fitW = Math.Min(fitW, targetVisible.Width);
                        fitH = Math.Min(fitH, targetVisible.Height);
                    }
                }

                if (fitW < 1 || fitH < 1) return false;
                place = CenterRectInPane(targetVisible, fitW, fitH);
            }
        }
        else
        {
            // Fill: stretch to pane; honor max track by centering at max if pane larger.
            int tw = targetVisible.Width;
            int th = targetVisible.Height;
            bool capped = false;
            if (winMaxW > 0 && tw > winMaxW) { tw = winMaxW; capped = true; }
            if (winMaxH > 0 && th > winMaxH) { th = winMaxH; capped = true; }
            if (tw < 1 || th < 1) return false;
            if (capped)
                place = CenterRectInPane(targetVisible, tw, th);
        }
        if (IsVisiblyAt(hwnd, place, tol: 2))
        {
            RaiseAbovePeersNoActivate(hwnd);
            if (activate) FocusWindow(hwnd);
            return true;
        }

        bool ok = SetVisibleFrame(hwnd, place, activate: false);
        if (ok && !IsVisiblyAt(hwnd, place, tol: 2))
            ok = SetVisibleFrame(hwnd, place, activate: false) || ok;

        // After tile: Fit/fixed recenter if smaller; Fill re-force full pane if still oversized.
        if (TryGetVisibleFrame(hwnd, out var actual) && !actual.IsEmpty)
        {
            if (wantFill)
            {
                if (actual.Width > targetVisible.Width + 4 || actual.Height > targetVisible.Height + 4
                    || actual.Width + 4 < targetVisible.Width || actual.Height + 4 < targetVisible.Height
                    || !IsVisiblyAt(hwnd, targetVisible, tol: 3))
                {
                    ForceFillVisibleFrame(hwnd, targetVisible);
                }
            }
            else if (actual.Width + 4 < targetVisible.Width || actual.Height + 4 < targetVisible.Height)
            {
                var centered = CenterRectInPane(targetVisible, actual.Width, actual.Height);
                if (!IsVisiblyAt(hwnd, centered, tol: 2))
                    SetVisibleFrame(hwnd, centered, activate: false);
            }
        }

        RaiseAbovePeersNoActivate(hwnd);
        if (activate)
            FocusWindow(hwnd);

        return ok;
    }

    public static void RaiseAbovePeersNoActivate(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd)) return;

        // Always SHOWNOACTIVATE so previously assigned Live panes cannot stay cloaked/hidden.
        if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);
        else ShowWindow(hwnd, SW_SHOWNOACTIVATE);

        SetWindowPos(hwnd, HWND_TOP, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }


    /// <summary>Hit-test click-through so drops reach the desk under a Live TOPMOST tile.</summary>
    public static void SetWindowClickThrough(IntPtr hwnd, bool through)
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd)) return;
        long ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        long next = through ? (ex | WS_EX_TRANSPARENT) : (ex & ~WS_EX_TRANSPARENT);
        if (next == ex) return;
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(next));
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED | SWP_NOREDRAW);
    }
    /// <summary>
    /// Nuclear Live z-order: pin tile TOPMOST so Dashboard chrome can never cover it.
    /// Cleared on Unlink / Live-off via <see cref="ClearLiveTopmost"/>.
    /// </summary>
    /// <param name="force">
    /// When true, always SetWindowPos HWND_TOPMOST/NOTOPMOST even if WS_EX_TOPMOST already matches.
    /// Required after desk RaiseDashboardAboveUnlinked: TOPMOST bit can stay set while tile is
    /// buried under the opaque desk in the topmost band. SoftRefresh must pass force:false
    /// (or skip entirely) so the 800ms tick never blinks.
    /// </param>
    public static void SetLiveTopmost(IntPtr hwnd, bool topmost, bool force = false)
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd)) return;
        // SoftRefresh thrash fix: when TOPMOST bit already matches and !force, do NOT re-SetWindowPos
        // (blink loop fought Snip UI). Assert/focus/Apply/Yield-end pass force:true to unbury.
        if (!force && IsWindowTopmost(hwnd) == topmost)
        {
            if (!topmost) return;
            if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);
            else if (!IsWindowVisible(hwnd)) ShowWindow(hwnd, SW_SHOWNOACTIVATE);
            return;
        }
        uint flags = SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE;
        if (topmost)
        {
            if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);
            else ShowWindow(hwnd, SW_SHOWNOACTIVATE);
            flags |= SWP_SHOWWINDOW;
        }
        SetWindowPos(
            hwnd,
            topmost ? HWND_TOPMOST : HWND_NOTOPMOST,
            0, 0, 0, 0,
            flags);
    }

    public static void ClearLiveTopmost(IntPtr hwnd) => SetLiveTopmost(hwnd, topmost: false);

    [StructLayout(LayoutKind.Sequential)]
    public struct WINDOWPLACEMENT
    {
        public int length;
        public int flags;
        public int showCmd;
        public POINT ptMinPosition;
        public POINT ptMaxPosition;
        public Rect rcNormalPosition;
    }

    public const int WPF_RESTORETOMAXIMIZED = 0x0002;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool GetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);
    public const uint MONITOR_DEFAULTTOPRIMARY = 1;

    /// <summary>Primary monitor work area (screen coords) and its workspace offset (work - monitor origin).</summary>
    public static bool TryGetPrimaryWorkArea(out Rect work, out int offX, out int offY)
    {
        work = default; offX = 0; offY = 0;
        var hMon = MonitorFromPoint(new POINT { X = 0, Y = 0 }, MONITOR_DEFAULTTOPRIMARY);
        if (hMon == IntPtr.Zero) return false;
        var info = new MonitorInfoEx { cbSize = Marshal.SizeOf<MonitorInfoEx>() };
        if (!GetMonitorInfo(hMon, ref info)) return false;
        work = info.rcWork;
        offX = info.rcWork.Left - info.rcMonitor.Left;
        offY = info.rcWork.Top - info.rcMonitor.Top;
        return true;
    }

    /// <summary>
    /// Uniform release size (Tim): h = 75% of work height, w = min(60% work width, 1.6 h), centered, cascaded 32 px per window
    /// (slot wraps every 8 so it never leaves the work area). Screen coordinates (outer window rect).
    /// </summary>
    public static Rect UniformReleaseRect(int slot)
    {
        if (!TryGetPrimaryWorkArea(out var work, out _, out _) || work.IsEmpty)
            work = new Rect { Left = 0, Top = 0, Right = 1600, Bottom = 900 };
        // Height 75% of the work area; width min(60% of work width, 1.6 x height) so ultrawides
        // (3840x1152) get ~1380x830 instead of a 2304-wide letterbox (Tim).
        int h = (int)Math.Round(work.Height * 0.75);
        int w = (int)Math.Round(Math.Min(work.Width * 0.60, h * 1.6));
        int k = ((slot % 8) + 8) % 8;
        int left = work.Left + (work.Width - w) / 2 + k * 32;
        int top = work.Top + (work.Height - h) / 2 + k * 32;
        // Keep inside the work area even on small screens.
        left = Math.Min(left, work.Right - w);
        top = Math.Min(top, work.Bottom - h);
        return new Rect { Left = left, Top = top, Right = left + w, Bottom = top + h };
    }

    /// <summary>
    /// Hand a released window back via its placement: restored (normal) bounds = <paramref name="normalScreen"/>
    /// (primary-monitor screen coords), re-opens maximized if <paramref name="restoreMaximized"/>.
    /// <paramref name="minimize"/>: minimize FIRST at its current spot (animation starts from the pane), then
    /// write the placement while iconic, so the window never moves while visible (no "jump left").
    /// Otherwise (dashboard exit) show it at the uniform size without activation. Never closes.
    /// TOPMOST is dropped while iconic (minimize) or before showing (exit).
    /// </summary>
    public static bool ReleaseWithPlacement(IntPtr hwnd, Rect normalScreen, bool restoreMaximized, bool minimize)
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd)) return false;
        const uint flagsNoMove = SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE;
        if (minimize)
        {
            if (!IsIconic(hwnd))
                ShowWindow(hwnd, SW_SHOWMINNOACTIVE);
            SetWindowPos(hwnd, HWND_NOTOPMOST, 0, 0, 0, 0, flagsNoMove);
        }
        else
        {
            SetWindowPos(hwnd, HWND_NOTOPMOST, 0, 0, 0, 0, flagsNoMove);
        }

        var wp = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
        if (!GetWindowPlacement(hwnd, ref wp)) return false;
        int dx = 0, dy = 0;
        long ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        // rcNormalPosition is in workspace coordinates (work area origin) for non-toolwindows.
        if ((ex & WS_EX_TOOLWINDOW) == 0)
            TryGetPrimaryWorkArea(out _, out dx, out dy);
        wp.rcNormalPosition = new Rect
        {
            Left = normalScreen.Left - dx,
            Top = normalScreen.Top - dy,
            Right = normalScreen.Right - dx,
            Bottom = normalScreen.Bottom - dy
        };
        if (minimize)
        {
            wp.flags = restoreMaximized ? WPF_RESTORETOMAXIMIZED : 0;
            wp.showCmd = (int)SW_SHOWMINNOACTIVE; // already iconic: only the stored bounds change
        }
        else
        {
            wp.flags = 0;
            wp.showCmd = restoreMaximized ? (int)SW_SHOWMAXIMIZED : (int)SW_SHOWNOACTIVATE;
        }
        return SetWindowPlacement(hwnd, ref wp);
    }

    /// <summary>
    /// Drop WS_EX_TOPMOST without raising the window in z-order.
    /// Used when yielding to an outside app so that app stays on top of linked panes.
    /// </summary>
    public static void ClearExTopmostKeepZOrder(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd)) return;
        long ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        if ((ex & WS_EX_TOPMOST) == 0) return;
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(ex & ~WS_EX_TOPMOST));
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
    }

    /// <summary>True if WS_EX_TOPMOST is set on the HWND.</summary>
    public static bool IsWindowTopmost(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd)) return false;
        long ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        return (ex & WS_EX_TOPMOST) != 0;
    }

    public static bool TrySetCloaked(IntPtr hwnd, bool cloak)
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd)) return false;
        int v = cloak ? 1 : 0;
        return DwmSetWindowAttribute(hwnd, DWMWA_CLOAK, ref v, sizeof(int)) == 0;
    }

    /// <summary>
    /// Park cropped/preview source so it cannot cover in-pane WPF (Uncrop / menu).
    /// Prefer DWM cloak (thumbnails keep working); fall back to SW_HIDE.
    /// </summary>
    public static void ParkForCropPreview(IntPtr hwnd)
    {
        // STABILIZE BUILD: NO-OP â€” do not cloak or SW_HIDE for crop/preview.
        _ = hwnd;
    }

    /// <summary>Undo ParkForCropPreview before Live Fill or Focus.</summary>
    public static void UnparkFromCropPreview(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd)) return;
        TrySetCloaked(hwnd, false);
        if (!IsWindowVisible(hwnd) || IsIconic(hwnd))
            ShowWindow(hwnd, IsIconic(hwnd) ? SW_RESTORE : SW_SHOWNOACTIVATE);
    }

    public const uint RDW_INVALIDATE = 0x0001;
    public const uint RDW_INTERNALPAINT = 0x0002;
    public const uint RDW_ERASE = 0x0004;
    public const uint RDW_FRAME = 0x0400;
    public const uint RDW_ALLCHILDREN = 0x0080;
    public const uint RDW_UPDATENOW = 0x0100;

    [DllImport("user32.dll")]
    public static extern bool RedrawWindow(IntPtr hWnd, IntPtr lprcUpdate, IntPtr hrgnUpdate, uint flags);

    [DllImport("user32.dll")]
    public static extern bool InvalidateRect(IntPtr hWnd, IntPtr lpRect, bool bErase);

    /// <summary>Force immediate show + z-order + paint so Live tiles are not blank until click.</summary>
    public static void ForceShowAndRedraw(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd)) return;
        if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);
        else ShowWindow(hwnd, SW_SHOWNOACTIVATE);
        SetWindowPos(hwnd, HWND_TOP, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
        InvalidateRect(hwnd, IntPtr.Zero, true);
        RedrawWindow(hwnd, IntPtr.Zero, IntPtr.Zero,
            RDW_INVALIDATE | RDW_ERASE | RDW_FRAME | RDW_ALLCHILDREN | RDW_UPDATENOW | RDW_INTERNALPAINT);
    }

    /// <summary>
    /// Place tile above the Dashboard HWND without activating either.
    /// Uses TOPMOST while linked (reliable); falls back to insert-after Dashboard.
    /// </summary>
    public static void RaiseAboveDashboardNoActivate(IntPtr hwnd, IntPtr dashboardHwnd, bool useTopmost)
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd)) return;
        // useTopmost=true pins Live tiles above desk; false = HWND_TOP peers only.
        if (useTopmost)
        {
            SetLiveTopmost(hwnd, topmost: true);
            return;
        }
        RaiseAbovePeersNoActivate(hwnd);
    }

    /// <summary>
    /// Soft repair: ensure visible + correct rect + above peers, never steal focus.
    /// Used after pane click / Focus so sibling Live tiles do not vanish under the Dashboard.
    /// Always ShowWindow(SW_SHOWNOACTIVATE) then SetWindowPos to the pane rect if off-rect.
    /// </summary>
    public static bool EnsureTiledVisibleNoActivate(IntPtr hwnd, Rect targetVisible, string fitMode = "fill", bool raiseZOrder = true)
    {
        if (hwnd == IntPtr.Zero || targetVisible.IsEmpty || !IsWindow(hwnd)) return false;
        if (targetVisible.Width < 32 || targetVisible.Height < 32)
            return false;

        bool wantFill = string.Equals(fitMode, "fill", StringComparison.OrdinalIgnoreCase);

        if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);
        else if (!IsWindowVisible(hwnd)) ShowWindow(hwnd, SW_SHOWNOACTIVATE);

        bool ok = true;
        GetWindowMinTrackSize(hwnd, out int ensMinW, out int ensMinH);
        bool tooThin = wantFill && ensMinW > targetVisible.Width;
        // Fill when min fits; FitWidth when too thin. Avoid DWM IsVisiblyAt thrash.
        bool needPlace = IsIconic(hwnd) || (wantFill
            ? (tooThin
                ? !IsOuterAt(hwnd, ComputeFillOrFitWidthRect(targetVisible, ensMinW, ensMinH, out _), tol: 3)
                : (!IsVisiblyAt(hwnd, targetVisible, tol: 3) || VisibleExceedsZone(hwnd, targetVisible, tol: 2)))
            : !IsVisiblyAt(hwnd, targetVisible, tol: 2));
        if (!needPlace && !wantFill && TryGetVisibleFrame(hwnd, out var cur) && !cur.IsEmpty)
        {
            if (cur.Width > targetVisible.Width + 4 || cur.Height > targetVisible.Height + 4)
                needPlace = true;
        }
        bool didPlace = false;
        if (needPlace)
        {
            if (wantFill)
            {
                ok = PlaceFillOrFitWidth(hwnd, targetVisible, out _);
                if (VisibleExceedsZone(hwnd, targetVisible, tol: 2))
                    ok = HardClampOuterToZone(hwnd, targetVisible) || ok;
            }
            else
                ok = TileWindow(hwnd, targetVisible, activate: false, fitMode);
            didPlace = true;
        }
        else if (!wantFill && IsFixedSizeWindow(hwnd) && TryGetVisibleFrame(hwnd, out var frame) && !frame.IsEmpty
                 && (frame.Width + 4 < targetVisible.Width || frame.Height + 4 < targetVisible.Height))
        {
            var centered = CenterRectInPane(targetVisible, frame.Width, frame.Height);
            if (!IsVisiblyAt(hwnd, centered, tol: 2))
            {
                ok = SetVisibleFrame(hwnd, centered, activate: false);
                didPlace = true;
            }
        }

        // Skip HWND_TOP when already correct — SoftRefresh/snip heal was thrashing z-order vs Snip UI.
        if (raiseZOrder && didPlace)
            RaiseAbovePeersNoActivate(hwnd);
        return ok;
    }

    private static bool SetVisibleFrame(IntPtr hwnd, Rect targetVisible, bool activate)
    {
        int left = targetVisible.Left;
        int top = targetVisible.Top;
        int width = Math.Max(1, targetVisible.Width);
        int height = Math.Max(1, targetVisible.Height);

        int x = left, y = top, cx = width, cy = height;
        if (GetWindowRect(hwnd, out var outer) &&
            DwmGetWindowAttributeRect(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out var frame, Marshal.SizeOf<Rect>()) == 0 &&
            !frame.IsEmpty)
        {
            int ml = frame.Left - outer.Left;
            int mt = frame.Top - outer.Top;
            int mr = outer.Right - frame.Right;
            int mb = outer.Bottom - frame.Bottom;
            if (ml >= 0 && mt >= 0 && mr >= 0 && mb >= 0 && ml + mr < 64 && mt + mb < 64)
            {
                x = left - ml;
                y = top - mt;
                cx = width + ml + mr;
                cy = height + mt + mb;
            }
        }

        // HWND_TOP for geometry; caller pins HWND_TOPMOST after. Always SWP_SHOWWINDOW.
        uint flags = SWP_NOACTIVATE | SWP_SHOWWINDOW;
        if (activate)
            flags = SWP_SHOWWINDOW;

        bool ok = SetWindowPos(hwnd, HWND_TOP, x, y, cx, cy, flags);
        // If visible frame still spills past the zone (min-track / bad margin), shrink outer by excess.
        if (TryGetVisibleFrame(hwnd, out var got) && !got.IsEmpty)
        {
            int overL = targetVisible.Left - got.Left;
            int overT = targetVisible.Top - got.Top;
            int overR = got.Right - targetVisible.Right;
            int overB = got.Bottom - targetVisible.Bottom;
            if (overL > 2 || overT > 2 || overR > 2 || overB > 2)
            {
                if (GetWindowRect(hwnd, out var o2))
                {
                    int nx = o2.Left + Math.Max(0, overL);
                    int ny = o2.Top + Math.Max(0, overT);
                    int ncx = Math.Max(1, o2.Width - Math.Max(0, overL) - Math.Max(0, overR));
                    int ncy = Math.Max(1, o2.Height - Math.Max(0, overT) - Math.Max(0, overB));
                    ok = SetWindowPos(hwnd, HWND_TOP, nx, ny, ncx, ncy, flags) || ok;
                }
            }
        }
        return ok;
    }

    public static void FocusWindow(IntPtr hwnd) => ForceForegroundWindow(hwnd);

    /// <summary>
    /// Aggressive foreground activation (AttachThreadInput + HWND_TOP + SetForegroundWindow,
    /// temporary TOPMOST pulse, optional Alt unlock). Caller may keep WPF Topmost=true while restored.
    /// </summary>
    public static bool ForceForegroundWindow(IntPtr hwnd)
    {
        LastForegroundTechnique = "fail";
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd)) return false;

        if (IsIconic(hwnd))
            ShowWindow(hwnd, SW_RESTORE);
        else if (!IsWindowVisible(hwnd))
            ShowWindow(hwnd, SW_SHOW);
        else
            ShowWindow(hwnd, SW_SHOW);

        uint targetThread = GetWindowThreadProcessId(hwnd, out uint targetPid);
        try { AllowSetForegroundWindow(targetPid); } catch { }
        try { AllowSetForegroundWindow(ASFW_ANY); } catch { }

        var fg = GetForegroundWindow();
        if (fg == hwnd)
        {
            SetWindowPos(hwnd, HWND_TOP, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
            LastForegroundTechnique = "already-fg+HWND_TOP";
            return true;
        }

        uint fgThread = fg != IntPtr.Zero ? GetWindowThreadProcessId(fg, out _) : 0;
        uint thisThread = GetCurrentThreadId();

        bool attachedFg = false;
        bool attachedTarget = false;
        if (fgThread != 0 && fgThread != thisThread)
            attachedFg = AttachThreadInput(thisThread, fgThread, true);
        if (targetThread != 0 && targetThread != thisThread && targetThread != fgThread)
            attachedTarget = AttachThreadInput(thisThread, targetThread, true);

        try
        {
            BringWindowToTop(hwnd);
            SetWindowPos(hwnd, HWND_TOP, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
            SetForegroundWindow(hwnd);
        }
        finally
        {
            if (attachedTarget) AttachThreadInput(thisThread, targetThread, false);
            if (attachedFg) AttachThreadInput(thisThread, fgThread, false);
        }

        if (GetForegroundWindow() == hwnd)
        {
            LastForegroundTechnique = "AttachThreadInput+SetForegroundWindow";
            return true;
        }

        // STABILIZE: no temporary TOPMOST pulse (blink/vanish wars with Live tiles).
        BringWindowToTop(hwnd);
        SetWindowPos(hwnd, HWND_TOP, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
        SetForegroundWindow(hwnd);

        if (GetForegroundWindow() == hwnd)
        {
            LastForegroundTechnique = "HWND_TOP+SetForegroundWindow";
            return true;
        }

        // Last resort: synthetic Alt unlocks the foreground restriction on many builds.
        try
        {
            keybd_event(VK_MENU, 0, 0, UIntPtr.Zero);
            keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        }
        catch { }
        BringWindowToTop(hwnd);
        SetWindowPos(hwnd, HWND_TOP, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
        SetForegroundWindow(hwnd);

        if (GetForegroundWindow() == hwnd)
        {
            LastForegroundTechnique = "Alt-unlock+SetForegroundWindow";
            return true;
        }

        try { FlashWindow(hwnd, true); } catch { }
        LastForegroundTechnique = "flash-only";
        return false;
    }

    /// <summary>
    /// Second-instance helper: find another WorkflowDashboard main window and force it front
    /// while this process still holds shortcut-launch foreground permission.
    /// </summary>
    public static bool ActivateExistingDashboardWindow()
    {
        int self = System.Diagnostics.Process.GetCurrentProcess().Id;
        foreach (var p in System.Diagnostics.Process.GetProcessesByName("WorkflowDashboard"))
        {
            try
            {
                if (p.Id == self) continue;
                IntPtr hwnd = p.MainWindowHandle;
                if (hwnd == IntPtr.Zero)
                    hwnd = FindTopLevelWindowForPid((uint)p.Id);
                if (hwnd == IntPtr.Zero || !IsWindow(hwnd)) continue;

                try { AllowSetForegroundWindow((uint)p.Id); } catch { }
                // Prefer activating FROM this process (has FG rights from the user click).
                if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);
                ShowWindow(hwnd, SW_SHOW);
                // Keep desk Topmost while restored so it surfaces above ordinary apps.
                SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
                BringWindowToTop(hwnd);
                SetForegroundWindow(hwnd);
                ForceForegroundWindow(hwnd);
                // Re-assert TOPMOST after ForceForeground's temp pulse may have cleared it.
                SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
                return true;
            }
            catch { }
            finally
            {
                try { p.Dispose(); } catch { }
            }
        }
        return false;
    }

    private static IntPtr FindTopLevelWindowForPid(uint pid)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out uint wpid);
            if (wpid != pid) return true;
            if (!IsWindowVisible(h) && !IsIconic(h)) return true;
            long style = GetWindowLongPtr(h, GWL_STYLE).ToInt64();
            if ((style & WS_CHILD) != 0) return true;
            long ex = GetWindowLongPtr(h, GWL_EXSTYLE).ToInt64();
            if ((ex & WS_EX_TOOLWINDOW) != 0) return true;
            int len = GetWindowTextLength(h);
            if (len <= 0) return true;
            found = h;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    public static void ToggleMaximize(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        if (IsZoomed(hwnd))
            ShowWindow(hwnd, SW_RESTORE);
        else
            ShowWindow(hwnd, SW_MAXIMIZE);
    }
    public const byte VK_RETURN = 0x0D;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
    private const uint MOUSEEVENTF_MOVE = 0x0001;

    [DllImport("user32.dll")]
    public static extern bool GetClientRect(IntPtr hWnd, out Rect lpRect);

    [DllImport("user32.dll")]
    public static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);

    /// <summary>Send a plain virtual key (keydown/keyup) — used by Skip Ad Enter fallback.</summary>
    public static void SendVirtualKey(byte vk)
    {
        keybd_event(vk, 0, 0, UIntPtr.Zero);
        keybd_event(vk, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        System.Threading.Thread.Sleep(30);
    }

    /// <summary>Absolute screen click via mouse_event (best-effort Skip Ad fallback).</summary>
    public static void ClickScreenPoint(int x, int y)
    {
        int sw = GetSystemMetrics(SM_CXSCREEN);
        int sh = GetSystemMetrics(SM_CYSCREEN);
        if (sw <= 0 || sh <= 0) return;
        uint ax = (uint)Math.Clamp((x * 65535L) / Math.Max(sw - 1, 1), 0, 65535);
        uint ay = (uint)Math.Clamp((y * 65535L) / Math.Max(sh - 1, 1), 0, 65535);
        mouse_event(MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE, ax, ay, 0, UIntPtr.Zero);
        mouse_event(MOUSEEVENTF_LEFTDOWN | MOUSEEVENTF_ABSOLUTE, ax, ay, 0, UIntPtr.Zero);
        mouse_event(MOUSEEVENTF_LEFTUP | MOUSEEVENTF_ABSOLUTE, ax, ay, 0, UIntPtr.Zero);
    }
}
