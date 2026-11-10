using System.Runtime.InteropServices;

namespace WorkflowDashboard;

/// <summary>Smoke-test stub: fake HWNDs (1..1000) are treated as live windows; no native calls.</summary>
internal static class NativeMethods
{
    public const uint SW_MINIMIZE = 6;

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        public int Left, Top, Right, Bottom;
        public int Width => Right - Left;
        public int Height => Bottom - Top;
        public bool IsEmpty => Width <= 0 || Height <= 0;
        public override string ToString() => $"{Left},{Top} {Width}x{Height}";
    }

    public static bool IsWindow(IntPtr hWnd) => hWnd != IntPtr.Zero;

    public static bool GetWindowRect(IntPtr hWnd, out Rect lpRect)
    {
        // Fake desks for smoke: invent a plausible restore rect so HasRestore can stick.
        if (hWnd != IntPtr.Zero)
        {
            lpRect = new Rect { Left = 100, Top = 100, Right = 900, Bottom = 700 };
            return true;
        }
        lpRect = default;
        return false;
    }

    public static bool ShowWindow(IntPtr hWnd, uint nCmdShow) => hWnd != IntPtr.Zero;

    public static void ClearLiveTopmost(IntPtr hwnd) { }

    public static bool TileWindow(IntPtr hwnd, Rect targetVisible, bool activate, string fitMode = "fill")
        => hwnd != IntPtr.Zero;

    public static void FocusWindow(IntPtr hwnd) { }

    public static bool OuterExceedsZone(IntPtr hwnd, Rect zone, int tol = 4) => false;
    public static bool HardClampOuterToZone(IntPtr hwnd, Rect zone) => hwnd != IntPtr.Zero;

    public static bool TryGetMonitorWorkArea(IntPtr hwnd, out Rect work, out Rect monitor)
    {
        work = new Rect { Left = 0, Top = 0, Right = 1920, Bottom = 1080 };
        monitor = work;
        return hwnd != IntPtr.Zero;
    }
}
