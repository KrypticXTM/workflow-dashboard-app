using System.Runtime.InteropServices;
using System.IO;
using System.Text;
using WorkflowDashboard;

internal static class Program
{
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(uint dwExStyle, string lpClassName, string lpWindowName,
        uint dwStyle, int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern ushort RegisterClassW(ref WNDCLASS lpWndClass);
    [DllImport("kernel32.dll")] private static extern IntPtr GetModuleHandleW(string? lpModuleName);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
    [DllImport("user32.dll")] private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASS
    {
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved, ptMaxSize, ptMaxPosition, ptMinTrackSize, ptMaxTrackSize;
    }

    private static readonly Dictionary<IntPtr, POINT> MinTracks = new();
    private static readonly IntPtr WndProcPtr = Marshal.GetFunctionPointerForDelegate<WndProc>(StaticWndProc);
    private delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private const uint WM_GETMINMAXINFO = 0x0024;
    private const uint WS_OVERLAPPEDWINDOW = 0x00CF0000;
    private const uint WS_VISIBLE = 0x10000000;

    private static IntPtr StaticWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_GETMINMAXINFO && MinTracks.TryGetValue(hWnd, out var min))
        {
            var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
            mmi.ptMinTrackSize = min;
            Marshal.StructureToPtr(mmi, lParam, false);
            return IntPtr.Zero;
        }
        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(POINT pt);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);
    private const uint GA_ROOT = 2;
    private static bool IsDescendantOf(IntPtr child, IntPtr root)
    {
        if (child == IntPtr.Zero || root == IntPtr.Zero) return false;
        if (child == root) return true;
        return GetAncestor(child, GA_ROOT) == root;
    }

    private static int Main()
    {
        var hInst = GetModuleHandleW(null);
        var wc = new WNDCLASS
        {
            lpfnWndProc = WndProcPtr,
            hInstance = hInst,
            lpszClassName = "WdLivePlaceSmokeClass"
        };
        RegisterClassW(ref wc);

        var sb = new StringBuilder();
        int fails = 0;

        fails += BasicTopmostFillSmoke(hInst, sb);
        fails += ThinFitWidthGeometrySmoke(sb);
        fails += Large70HardClampSmoke(hInst, sb);

        bool pass = fails == 0;
        sb.AppendLine($"RESULT {(pass ? "PASS" : "FAIL")} fails={fails}");
        Console.WriteLine(sb.ToString());

        var outPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WorkflowDashboard", "live-place-smoke.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        File.WriteAllText(outPath, sb.ToString());
        return pass ? 0 : 1;
    }

    private static int BasicTopmostFillSmoke(IntPtr hInst, StringBuilder sb)
    {
        var desk = new NativeMethods.Rect { Left = 200, Top = 100, Right = 3340, Bottom = 1100 };
        var zones = ZoneLayout.Compute(desk, paneCount: 3, layout: "large", activePane1Based: 2);
        sb.AppendLine($"desk={desk.Left},{desk.Top},{desk.Right},{desk.Bottom}");
        for (int i = 0; i < 3; i++)
            sb.AppendLine($"zone{i}={zones[i].Left},{zones[i].Top},{zones[i].Right},{zones[i].Bottom} ({zones[i].Width}x{zones[i].Height})");

        bool zonesDisjoint = true;
        for (int i = 0; i < 3; i++)
        for (int j = i + 1; j < 3; j++)
        {
            if (zones[i].Left < zones[j].Right && zones[j].Left < zones[i].Right
                && zones[i].Top < zones[j].Bottom && zones[j].Top < zones[i].Bottom)
                zonesDisjoint = false;
        }

        var hwnds = new IntPtr[3];
        for (int i = 0; i < 3; i++)
        {
            hwnds[i] = CreateWindowExW(0, "WdLivePlaceSmokeClass", $"SmokePane{i}",
                WS_OVERLAPPEDWINDOW | WS_VISIBLE,
                50 + i * 40, 50 + i * 40, 800, 600, IntPtr.Zero, IntPtr.Zero, hInst, IntPtr.Zero);
            NativeMethods.ForceFillVisibleFrame(hwnds[i], zones[i]);
            NativeMethods.SetLiveTopmost(hwnds[i], topmost: true);
        }

        var deskHwnd = CreateWindowExW(0, "WdLivePlaceSmokeClass", "SmokeDesk",
            WS_OVERLAPPEDWINDOW | WS_VISIBLE,
            desk.Left, desk.Top, desk.Width, desk.Height, IntPtr.Zero, IntPtr.Zero, hInst, IntPtr.Zero);
        NativeMethods.SetLiveTopmost(deskHwnd, topmost: true);
        for (int i = 0; i < 3; i++)
            NativeMethods.SetLiveTopmost(hwnds[i], topmost: true, force: true);

        int visible = 0, inZone = 0, overlaps = 0, aboveDesk = 0, topmostCount = 0;
        var rects = new NativeMethods.Rect[3];
        for (int i = 0; i < 3; i++)
        {
            bool vis = NativeMethods.IsWindowVisible(hwnds[i]) && !NativeMethods.IsIconic(hwnds[i]);
            bool top = NativeMethods.IsWindowTopmost(hwnds[i]);
            NativeMethods.GetWindowRect(hwnds[i], out rects[i]);
            bool oz = NativeMethods.OuterExceedsZone(hwnds[i], zones[i], 8);
            int cx = (zones[i].Left + zones[i].Right) / 2;
            int cy = (zones[i].Top + zones[i].Bottom) / 2;
            var hit = WindowFromPoint(new POINT { X = cx, Y = cy });
            bool onTile = hit == hwnds[i] || IsDescendantOf(hit, hwnds[i]);
            if (vis) visible++;
            if (!oz) inZone++;
            if (top) topmostCount++;
            if (onTile) aboveDesk++;
            sb.AppendLine($"tile{i} hwnd=0x{hwnds[i].ToInt64():X} vis={vis} topmost={top} exceeds={oz} aboveDesk={onTile} hit=0x{hit.ToInt64():X} rect={rects[i].Left},{rects[i].Top},{rects[i].Right},{rects[i].Bottom}");
        }
        for (int i = 0; i < 3; i++)
        for (int j = i + 1; j < 3; j++)
        {
            bool hit = rects[i].Left < rects[j].Right && rects[j].Left < rects[i].Right
                && rects[i].Top < rects[j].Bottom && rects[j].Top < rects[i].Bottom;
            if (hit) overlaps++;
        }

        DestroyWindow(deskHwnd);
        for (int i = 0; i < 3; i++) DestroyWindow(hwnds[i]);

        bool ok = zonesDisjoint && visible == 3 && inZone == 3 && overlaps == 0 && topmostCount == 3 && aboveDesk == 3;
        sb.AppendLine($"BasicFill {(ok ? "PASS" : "FAIL")} zonesDisjoint={zonesDisjoint} visible={visible}/3 inZone={inZone}/3 topmost={topmostCount}/3 aboveDesk={aboveDesk}/3 winOverlaps={overlaps}");
        return ok ? 0 : 1;
    }

    private static int ThinFitWidthGeometrySmoke(StringBuilder sb)
    {
        var thinZone = new NativeMethods.Rect { Left = 100, Top = 200, Right = 100 + 300, Bottom = 200 + 900 };
        int minW = 520, minH = 400;
        var place = NativeMethods.ComputeFillOrFitWidthRect(thinZone, minW, minH, out string thinMode);
        var sibling = new NativeMethods.Rect { Left = thinZone.Right + 4, Top = thinZone.Top, Right = thinZone.Right + 4 + 800, Bottom = thinZone.Bottom };
        bool thinOk = string.Equals(thinMode, "fitwidth", StringComparison.OrdinalIgnoreCase)
            && place.Width == thinZone.Width
            && place.Height <= thinZone.Height
            && place.Left >= thinZone.Left && place.Right <= thinZone.Right
            && place.Top >= thinZone.Top && place.Bottom <= thinZone.Bottom
            && !(place.Left < sibling.Right && sibling.Left < place.Right && place.Top < sibling.Bottom && sibling.Top < place.Bottom);
        int leftPad = place.Left - thinZone.Left;
        int rightPad = thinZone.Right - place.Right;
        int topPad = place.Top - thinZone.Top;
        int botPad = thinZone.Bottom - place.Bottom;
        bool centered = Math.Abs(leftPad - rightPad) <= 1 && Math.Abs(topPad - botPad) <= 1;
        thinOk = thinOk && centered;
        var wideZone = new NativeMethods.Rect { Left = 0, Top = 0, Right = 800, Bottom = 600 };
        var fillPlace = NativeMethods.ComputeFillOrFitWidthRect(wideZone, 400, 300, out string fillMode);
        bool fillOk = string.Equals(fillMode, "fill", StringComparison.OrdinalIgnoreCase)
            && fillPlace.Left == wideZone.Left && fillPlace.Right == wideZone.Right
            && fillPlace.Top == wideZone.Top && fillPlace.Bottom == wideZone.Bottom;
        sb.AppendLine($"ThinFitWidth mode={thinMode} place={place.Left},{place.Top},{place.Right},{place.Bottom} ({place.Width}x{place.Height}) centered={centered} ok={thinOk}");
        sb.AppendLine($"FillWhenFits mode={fillMode} ok={fillOk}");
        return (thinOk && fillOk) ? 0 : 1;
    }

    /// <summary>
    /// Large + ~70% desk width with Electron-like min-tracks (512/400/536).
    /// HonorAssigned fair-grow, then PlaceFillOrFitWidth+HardClamp; assert outer subset and no overlap.
    /// </summary>
    private static int Large70HardClampSmoke(IntPtr hInst, StringBuilder sb)
    {
        // Match prior live-diag desk at Large+70%: ~2344px wide client.
        var desk = new NativeMethods.Rect { Left = 593, Top = 255, Right = 2937, Bottom = 943 };
        var zones = ZoneLayout.Compute(desk, paneCount: 3, layout: "large", activePane1Based: 2);
        const int gap = 4;
        int usable = Math.Max(1, desk.Width - 2 * gap);
        int w0 = zones[0].Width, w1 = zones[1].Width, w2 = zones[2].Width;
        int m0 = 512, m1 = 400, m2 = 536; // Grok / center / ONLYOFFICE-ish
        int before0 = w0, before1 = w1, before2 = w2;
        ZoneLayout.ApplyAssignedMinTracksThreePane(ref w0, ref w1, ref w2, usable, m0, m1, m2, ZoneLayout.MinSideWidth);
        zones[0] = new NativeMethods.Rect { Left = desk.Left, Top = desk.Top, Right = desk.Left + w0, Bottom = desk.Bottom };
        zones[1] = new NativeMethods.Rect { Left = zones[0].Right + gap, Top = desk.Top, Right = zones[0].Right + gap + w1, Bottom = desk.Bottom };
        zones[2] = new NativeMethods.Rect { Left = zones[1].Right + gap, Top = desk.Top, Right = desk.Right, Bottom = desk.Bottom };

        sb.AppendLine($"Large70 beforeSides={before0},{before2} after={w0},{w1},{w2} mins={m0},{m1},{m2} usable={usable}");
        bool honorOk = w0 >= m0 && w2 >= m2 && w1 >= 1 && (w0 + w1 + w2 == usable)
            && zones[0].Right <= zones[1].Left && zones[1].Right <= zones[2].Left;
        // Fair: both sides should reach min when surplus allows (512+536+400=1448 < usable~2336).
        if (!honorOk)
            sb.AppendLine($"Large70 HONOR FAIL w0={w0} (need>={m0}) w1={w1} w2={w2} (need>={m2})");

        int[] mins = { m0, m1, m2 };
        var hwnds = new IntPtr[3];
        for (int i = 0; i < 3; i++)
        {
            hwnds[i] = CreateWindowExW(0, "WdLivePlaceSmokeClass", $"Stubborn{i}",
                WS_OVERLAPPEDWINDOW | WS_VISIBLE,
                20 + i * 30, 20 + i * 30, 900, 700, IntPtr.Zero, IntPtr.Zero, hInst, IntPtr.Zero);
            MinTracks[hwnds[i]] = new POINT { X = mins[i], Y = 400 };
            // Start oversized to simulate pre-link size.
            NativeMethods.SetWindowPos(hwnds[i], NativeMethods.HWND_TOP, 50, 50, 1200, 800,
                NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
        }

        int inZone = 0, overlaps = 0;
        var rects = new NativeMethods.Rect[3];
        for (int i = 0; i < 3; i++)
        {
            NativeMethods.PlaceFillOrFitWidth(hwnds[i], zones[i], out string mode);
            if (NativeMethods.OuterExceedsZone(hwnds[i], zones[i], tol: 2))
                NativeMethods.HardClampOuterToZone(hwnds[i], zones[i]);
            NativeMethods.GetWindowRect(hwnds[i], out rects[i]);
            bool oz = NativeMethods.OuterExceedsZone(hwnds[i], zones[i], tol: 2);
            if (!oz) inZone++;
            sb.AppendLine($"Large70 tile{i} mode={mode} exceeds={oz} rect={rects[i].Width}x{rects[i].Height} zone={zones[i].Width}x{zones[i].Height} min={mins[i]}");
        }
        for (int i = 0; i < 3; i++)
        for (int j = i + 1; j < 3; j++)
        {
            bool hit = rects[i].Left < rects[j].Right && rects[j].Left < rects[i].Right
                && rects[i].Top < rects[j].Bottom && rects[j].Top < rects[i].Bottom;
            if (hit)
            {
                overlaps++;
                int iw = Math.Min(rects[i].Right, rects[j].Right) - Math.Max(rects[i].Left, rects[j].Left);
                sb.AppendLine($"Large70 OVERLAP {i}x{j} w={iw}");
            }
        }

        for (int i = 0; i < 3; i++)
        {
            MinTracks.Remove(hwnds[i]);
            DestroyWindow(hwnds[i]);
        }

        bool placeOk = honorOk && inZone == 3 && overlaps == 0;
        sb.AppendLine($"Large70HardClamp {(placeOk ? "PASS" : "FAIL")} honor={honorOk} inZone={inZone}/3 overlaps={overlaps}");
        return placeOk ? 0 : 1;
    }
}
