using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WorkflowDashboard;

internal static class WindowIconLoader
{
    private static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    private const uint SHGFI_ICON = 0x000000100;
    private const uint SHGFI_SMALLICON = 0x000000001;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    public static ImageSource? ForProcess(uint pid)
    {
        string? path = null;
        try
        {
            using var p = Process.GetProcessById((int)pid);
            path = p.MainModule?.FileName;
        }
        catch { }

        if (string.IsNullOrEmpty(path))
            return null;

        lock (Cache)
        {
            if (Cache.TryGetValue(path, out var cached))
                return cached;
        }

        ImageSource? src = null;
        try
        {
            var info = new SHFILEINFO();
            var r = SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_ICON | SHGFI_SMALLICON);
            if (r != IntPtr.Zero && info.hIcon != IntPtr.Zero)
            {
                try
                {
                    src = Imaging.CreateBitmapSourceFromHIcon(
                        info.hIcon,
                        Int32Rect.Empty,
                        BitmapSizeOptions.FromWidthAndHeight(16, 16));
                    src.Freeze();
                }
                finally
                {
                    DestroyIcon(info.hIcon);
                }
            }
        }
        catch { }

        lock (Cache)
        {
            Cache[path] = src;
        }
        return src;
    }
}
