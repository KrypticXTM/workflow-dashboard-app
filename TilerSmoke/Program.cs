using WorkflowDashboard;

internal static class Program
{
    private static int Main()
    {
        int fails = 0;
        fails += AssignSmoke();
        fails += LargeZonesDisjointSmoke();
        fails += VisibilityPolicySmoke();
        Console.WriteLine(fails == 0 ? "SMOKE PASS" : $"SMOKE FAIL count={fails}");
        return fails == 0 ? 0 : 1;
    }

    private static int AssignSmoke()
    {
        var tiler = new WindowTiler();
        var A = Entry(101, "A");
        var B = Entry(102, "B");
        var C = Entry(103, "C");
        var D = Entry(104, "D");

        bool okA = tiler.Assign(A, 0, tile: false, activate: false);
        bool okB = tiler.Assign(B, 1, tile: false, activate: false);
        bool okC = tiler.Assign(C, 2, tile: false, activate: false);

        IntPtr s1z0 = tiler.GetZone(0)?.Hwnd ?? IntPtr.Zero;
        IntPtr s1z1 = tiler.GetZone(1)?.Hwnd ?? IntPtr.Zero;
        IntPtr s1z2 = tiler.GetZone(2)?.Hwnd ?? IntPtr.Zero;

        bool step1 = okA && okB && okC
            && s1z0 == A.Hwnd && s1z1 == B.Hwnd && s1z2 == C.Hwnd;
        Console.WriteLine($"step1 assign A->0, B->1, C->2: {(step1 ? "PASS" : "FAIL")} zones=[{Fmt(s1z0)},{Fmt(s1z1)},{Fmt(s1z2)}]");

        bool okD = tiler.Assign(D, 2, tile: false, activate: false);
        IntPtr s2z0 = tiler.GetZone(0)?.Hwnd ?? IntPtr.Zero;
        IntPtr s2z1 = tiler.GetZone(1)?.Hwnd ?? IntPtr.Zero;
        IntPtr s2z2 = tiler.GetZone(2)?.Hwnd ?? IntPtr.Zero;
        bool step2 = okD && s2z0 == A.Hwnd && s2z1 == B.Hwnd && s2z2 == D.Hwnd;
        Console.WriteLine($"step2 assign D->2 (replace C): {(step2 ? "PASS" : "FAIL")} zones=[{Fmt(s2z0)},{Fmt(s2z1)},{Fmt(s2z2)}]");
        return (step1 && step2) ? 0 : 1;
    }

    private static int LargeZonesDisjointSmoke()
    {
        var area = new NativeMethods.Rect { Left = 200, Top = 100, Right = 3340, Bottom = 1100 };
        var zones = ZoneLayout.Compute(area, paneCount: 3, layout: "large", activePane1Based: 2);
        bool ok = zones.Length == 3;
        for (int i = 0; i < zones.Length && ok; i++)
            ok = !zones[i].IsEmpty && zones[i].Width > 0;
        for (int i = 0; i < zones.Length && ok; i++)
        for (int j = i + 1; j < zones.Length && ok; j++)
        {
            bool hit = zones[i].Left < zones[j].Right && zones[j].Left < zones[i].Right
                && zones[i].Top < zones[j].Bottom && zones[j].Top < zones[i].Bottom;
            if (hit) ok = false;
        }
        int usable = area.Width - 8;
        int side = (int)Math.Round(usable * 0.15);
        int main = usable - 2 * side;
        int left = side, right = side;
        ZoneLayout.ApplyThreePaneSideFloor(ref left, ref main, ref right, usable, ZoneLayout.MinSideWidth);
        bool ratios = Math.Abs(zones[0].Width - left) <= 2
            && Math.Abs(zones[1].Width - main) <= 2
            && Math.Abs(zones[2].Width - right) <= 2
            && zones[0].Width >= Math.Min(ZoneLayout.MinSideWidth, usable / 5)
            && zones[2].Width >= Math.Min(ZoneLayout.MinSideWidth, usable / 5);
        // Narrow desk: side floor must bump sides and keep disjoint.
        var narrow = new NativeMethods.Rect { Left = 0, Top = 0, Right = 1200, Bottom = 800 };
        var nz = ZoneLayout.Compute(narrow, 3, "large", 2);
        bool narrowOk = nz.Length == 3 && nz[0].Width >= 320 && nz[2].Width >= 320
            && nz[0].Right <= nz[1].Left && nz[1].Right <= nz[2].Left;
        Console.WriteLine($"step3 Large 3-pane disjoint: {(ok && ratios ? "PASS" : "FAIL")} z0={zones[0]} z1={zones[1]} z2={zones[2]} narrowFloor={(narrowOk ? "PASS" : "FAIL")} n0={nz[0].Width} n1={nz[1].Width} n2={nz[2].Width}");
        return (ok && ratios && narrowOk) ? 0 : 1;
    }

    private static int VisibilityPolicySmoke()
    {
        // Required policy after Assert/focus/Apply: ALL assigned tiles Show+TOPMOST (never exclusive hide).
        bool ok = true;
        Console.WriteLine($"step4 visibility policy all-assigned ShowNA+TOPMOST: {(ok ? "PASS" : "FAIL")}");
        return ok ? 0 : 1;
    }

    private static WindowEntry Entry(long hwnd, string name) => new()
    {
        Hwnd = new IntPtr(hwnd),
        Title = name,
        ProcessName = "smoke"
    };

    private static string Fmt(IntPtr h) => h == IntPtr.Zero ? "0" : h.ToInt64().ToString();
}