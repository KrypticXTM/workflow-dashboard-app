using System.Diagnostics;
using System.Text;

namespace WorkflowDashboard;

internal static class WindowEnumerator
{
    private static readonly HashSet<string> ExcludedProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "WorkflowDashboard",
        "ScreenSketch",       // Snipping Tool (Win10/11)
        "SnippingTool",       // legacy Snipping Tool
        "ScreenClippingHost", // screen clip host / snip overlay
    };

    public static List<WindowEntry> GetTopLevelWindows(IntPtr excludeHwnd, int excludePid)
    {
        var results = new List<WindowEntry>();
        var shell = NativeMethods.GetShellWindow();

        NativeMethods.EnumWindows((hWnd, _) =>
        {
            if (hWnd == IntPtr.Zero || hWnd == excludeHwnd || hWnd == shell)
                return true;
            if (!NativeMethods.IsWindowVisible(hWnd))
                return true;
            if (NativeMethods.IsCloaked(hWnd))
                return true;

            var exStyle = NativeMethods.GetWindowLongPtr(hWnd, NativeMethods.GWL_EXSTYLE).ToInt64();
            bool tool = (exStyle & NativeMethods.WS_EX_TOOLWINDOW) != 0;
            bool app = (exStyle & NativeMethods.WS_EX_APPWINDOW) != 0;
            if (tool && !app)
                return true;

            int len = NativeMethods.GetWindowTextLength(hWnd);
            if (len <= 0)
                return true;

            var sb = new StringBuilder(len + 1);
            NativeMethods.GetWindowText(hWnd, sb, sb.Capacity);
            string title = TitleSanitizer.Clean(sb.ToString());
            if (string.IsNullOrWhiteSpace(title))
                return true;

            NativeMethods.GetWindowThreadProcessId(hWnd, out uint pid);
            if (pid == 0 || pid == (uint)excludePid)
                return true;

            string procName = "";
            try
            {
                using var p = Process.GetProcessById((int)pid);
                procName = p.ProcessName;
            }
            catch
            {
                // process may have exited
            }

            if (ExcludedProcesses.Contains(procName))
                return true;

            // Title fallback for snip UI that may host under other process names
            if (title.Contains("Snipping Tool", StringComparison.OrdinalIgnoreCase) ||
                title.Contains("Screen Sketch", StringComparison.OrdinalIgnoreCase) ||
                title.Equals("Snip & Sketch", StringComparison.OrdinalIgnoreCase))
                return true;

            results.Add(new WindowEntry
            {
                Hwnd = hWnd,
                Title = title,
                ProcessId = pid,
                ProcessName = procName,
                Icon = WindowIconLoader.ForProcess(pid)
            });
            return true;
        }, IntPtr.Zero);

        return results
            .OrderBy(w => w.ProcessName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(w => w.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
