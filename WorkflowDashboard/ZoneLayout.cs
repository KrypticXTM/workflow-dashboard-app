namespace WorkflowDashboard;

/// <summary>
/// Computes 1�3 zone rectangles inside a work area (physical pixels).
/// Layouts: equal | main | large. Used by optional Arrange only (no overlays).
/// Main/Large skinny sides get a soft floor (~340px); center absorbs. Zones stay disjoint.
/// </summary>
internal static class ZoneLayout
{
    /// <summary>Minimum usable pane/tile size (physical px when used with screen zones).</summary>
    public const int MinPaneWidth = 280;
    public const int MinPaneHeight = 200;

    /// <summary>
    /// Soft floor for Main/Large skinny side columns (physical px).
    /// Mid of 320�360; center/main absorbs. Never inflate via WPF MinWidth (that overlapped zones).
    /// </summary>
    public const int MinSideWidth = 340;

    public static NativeMethods.Rect[] Compute(
        NativeMethods.Rect area,
        int paneCount,
        string layout,
        int activePane1Based)
    {
        paneCount = Math.Clamp(paneCount, 1, 3);
        activePane1Based = Math.Clamp(activePane1Based, 1, paneCount);
        layout = layout switch
        {
            "main" or "large" => layout,
            "focus" => "large", // legacy
            _ => "equal"
        };

        if (area.IsEmpty)
            return Array.Empty<NativeMethods.Rect>();

        const int gap = 4;
        return layout switch
        {
            "main" => MainSides(area, paneCount, gap),
            "large" => Large(area, paneCount, activePane1Based, gap),
            _ => Equal(area, paneCount, gap)
        };
    }

    /// <summary>
    /// Bump left/right skinny sides up to <paramref name="floor"/> by taking from center.
    /// Keeps left+center+right == usable. If desk too narrow for 2*floor, use a reduced floor.
    /// </summary>
    public static void ApplyThreePaneSideFloor(ref int leftW, ref int centerW, ref int rightW, int usable, int floor = MinSideWidth)
    {
        usable = Math.Max(1, usable);
        floor = Math.Max(1, floor);
        // Need room for a usable center (= ~1/5 of desk or MinPaneWidth, whichever fits).
        int minCenter = Math.Min(MinPaneWidth, Math.Max(1, usable / 5));
        int maxFloor = Math.Max(1, (usable - minCenter) / 2);
        int useFloor = Math.Min(floor, maxFloor);

        if (leftW < useFloor)
        {
            int need = useFloor - leftW;
            leftW = useFloor;
            centerW = Math.Max(minCenter, centerW - need);
        }
        if (rightW < useFloor)
        {
            int need = useFloor - rightW;
            rightW = useFloor;
            centerW = Math.Max(minCenter, centerW - need);
        }

        // Reconcile sum to usable (prefer adjusting center).
        int sum = leftW + centerW + rightW;
        if (sum != usable)
            centerW += usable - sum;
        if (centerW < 1)
        {
            centerW = 1;
            int sides = usable - 1;
            leftW = Math.Max(1, sides / 2);
            rightW = Math.Max(1, sides - leftW);
        }
        // Final guard: never negative / zero columns
        leftW = Math.Max(1, leftW);
        rightW = Math.Max(1, rightW);
        centerW = Math.Max(1, usable - leftW - rightW);
    }

    /// <summary>Bump thin side up to floor; main absorbs. main+thin == usable.</summary>
    public static void ApplyTwoPaneThinFloor(ref int mainW, ref int thinW, int usable, int floor = MinSideWidth)
    {
        usable = Math.Max(1, usable);
        floor = Math.Max(1, floor);
        int minMain = Math.Min(MinPaneWidth, Math.Max(1, usable / 3));
        int maxThin = Math.Max(1, usable - minMain);
        int useFloor = Math.Min(floor, maxThin);
        if (thinW < useFloor)
            thinW = useFloor;
        mainW = Math.Max(1, usable - thinW);
        if (mainW < 1) { mainW = 1; thinW = Math.Max(1, usable - 1); }
    }

    /// <summary>
    /// Grow assigned 3-pane columns up to HWND min-tracks by stealing from center (fair split),
    /// then from the other side if needed. Unassigned panes keep current width (sideFloor already
    /// applied by caller). mX==0 means unassigned (do not force bump). Sum stays == usable.
    /// </summary>
    public static void ApplyAssignedMinTracksThreePane(
        ref int w0, ref int w1, ref int w2, int usable,
        int m0, int m1, int m2, int sideFloor = MinSideWidth)
    {
        usable = Math.Max(1, usable);
        sideFloor = Math.Max(1, sideFloor);
        w0 = Math.Max(1, w0);
        w1 = Math.Max(1, w1);
        w2 = Math.Max(1, w2);

        // Floors on sides (Main/Large); assigned mins also respect sideFloor.
        w0 = Math.Max(w0, sideFloor);
        w2 = Math.Max(w2, sideFloor);
        if (m0 > 0) m0 = Math.Max(m0, sideFloor);
        if (m2 > 0) m2 = Math.Max(m2, sideFloor);

        int CenterFloor() => Math.Max(1, m1 > 0 ? m1 : Math.Min(MinPaneWidth, Math.Max(1, usable / 5)));

        // Fair split of center surplus across both side needs (avoids left-starves-right).
        int need0 = m0 > w0 ? m0 - w0 : 0;
        int need2 = m2 > w2 ? m2 - w2 : 0;
        int surplus = Math.Max(0, w1 - CenterFloor());
        if (need0 + need2 > 0 && surplus > 0)
        {
            if (need0 + need2 <= surplus)
            {
                w0 += need0;
                w2 += need2;
                w1 -= need0 + need2;
            }
            else
            {
                // Proportional fair share of limited surplus.
                int take0 = (int)((long)surplus * need0 / (need0 + need2));
                int take2 = surplus - take0;
                // Prefer not rounding a side to zero when it has need.
                if (need0 > 0 && take0 == 0 && surplus > 0) { take0 = 1; take2 = surplus - 1; }
                if (need2 > 0 && take2 == 0 && surplus > take0) { take2 = 1; take0 = surplus - 1; }
                w0 += take0;
                w2 += take2;
                w1 -= surplus;
            }
        }

        // Grow center from sides (only surplus above side mins/floors).
        if (m1 > w1)
        {
            int need = m1 - w1;
            int leftFloor = m0 > 0 ? m0 : sideFloor;
            int rightFloor = m2 > 0 ? m2 : sideFloor;
            int avail0 = Math.Max(0, w0 - leftFloor);
            int avail2 = Math.Max(0, w2 - rightFloor);
            int take0 = Math.Min(need / 2, avail0);
            int take2 = Math.Min(need - take0, avail2);
            // If still short, take remaining from the other side.
            int still = need - take0 - take2;
            if (still > 0 && avail0 - take0 > 0)
            {
                int extra = Math.Min(still, avail0 - take0);
                take0 += extra;
                still -= extra;
            }
            if (still > 0 && avail2 - take2 > 0)
            {
                int extra = Math.Min(still, avail2 - take2);
                take2 += extra;
            }
            w0 -= take0;
            w2 -= take2;
            w1 += take0 + take2;
        }

        // Reconcile to usable (prefer center).
        int sum = w0 + w1 + w2;
        if (sum != usable) w1 += usable - sum;
        if (w1 < 1)
        {
            w1 = 1;
            int sides = usable - 1;
            w0 = Math.Max(1, sides / 2);
            w2 = Math.Max(1, sides - w0);
        }
        w0 = Math.Max(1, w0);
        w2 = Math.Max(1, w2);
        w1 = Math.Max(1, usable - w0 - w2);
    }
    private static NativeMethods.Rect[] Equal(NativeMethods.Rect a, int n, int gap)
    {
        var zones = new NativeMethods.Rect[n];
        if (n == 1)
        {
            zones[0] = a;
            return zones;
        }

        if (n == 2)
        {
            int w = (a.Width - gap) / 2;
            zones[0] = Box(a.Left, a.Top, w, a.Height);
            zones[1] = Box(a.Left + w + gap, a.Top, a.Right - (a.Left + w + gap), a.Height);
            return zones;
        }

        if (n == 3)
        {
            int w = (a.Width - 2 * gap) / 3;
            zones[0] = Box(a.Left, a.Top, w, a.Height);
            zones[1] = Box(a.Left + w + gap, a.Top, w, a.Height);
            zones[2] = Box(a.Left + 2 * (w + gap), a.Top, a.Right - (a.Left + 2 * (w + gap)), a.Height);
            return zones;
        }

        int halfW = (a.Width - gap) / 2;
        int halfH = (a.Height - gap) / 2;
        zones[0] = Box(a.Left, a.Top, halfW, halfH);
        zones[1] = Box(a.Left + halfW + gap, a.Top, a.Right - (a.Left + halfW + gap), halfH);
        zones[2] = Box(a.Left, a.Top + halfH + gap, halfW, a.Bottom - (a.Top + halfH + gap));
        zones[3] = Box(a.Left + halfW + gap, a.Top + halfH + gap,
            a.Right - (a.Left + halfW + gap), a.Bottom - (a.Top + halfH + gap));
        return zones;
    }

    private static NativeMethods.Rect[] MainSides(NativeMethods.Rect a, int n, int gap)
    {
        var zones = new NativeMethods.Rect[n];
        if (n == 1)
        {
            zones[0] = a;
            return zones;
        }

        if (n == 2)
        {
            // ~70/30 � thin side floored so Flip/Main never create unusable slivers
            int usable = Math.Max(1, a.Width - gap);
            int thin = Math.Max(1, (int)Math.Round(usable * 0.30));
            int mainW = Math.Max(1, usable - thin);
            ApplyTwoPaneThinFloor(ref mainW, ref thin, usable, MinSideWidth);
            zones[0] = Box(a.Left, a.Top, mainW, a.Height);
            zones[1] = Box(a.Left + mainW + gap, a.Top, thin, a.Height);
            return zones;
        }

        if (n == 3)
        {
            // ~22|56|22 with side floor; center absorbs. No MinWidth inflate (overlap).
            int usable = Math.Max(1, a.Width - 2 * gap);
            int side = Math.Max(1, (int)Math.Round(usable * 0.22));
            int main = Math.Max(1, usable - 2 * side);
            int left = side, right = side;
            ApplyThreePaneSideFloor(ref left, ref main, ref right, usable, MinSideWidth);
            zones[0] = Box(a.Left, a.Top, left, a.Height);
            zones[1] = Box(a.Left + left + gap, a.Top, main, a.Height);
            zones[2] = Box(a.Left + left + gap + main + gap, a.Top,
                a.Right - (a.Left + left + gap + main + gap), a.Height);
            return zones;
        }

        int sideW4 = Math.Max(MinPaneWidth, (int)(a.Width * 0.30));
        sideW4 = Math.Min(sideW4, a.Width - MinPaneWidth - gap);
        int mainW4 = a.Width - sideW4 - gap;
        int h = (a.Height - 2 * gap) / 3;
        h = Math.Max(h, Math.Min(MinPaneHeight, a.Height / 3));
        zones[0] = Box(a.Left, a.Top, mainW4, a.Height);
        zones[1] = Box(a.Left + mainW4 + gap, a.Top, sideW4, h);
        zones[2] = Box(a.Left + mainW4 + gap, a.Top + h + gap, sideW4, h);
        zones[3] = Box(a.Left + mainW4 + gap, a.Top + 2 * (h + gap), sideW4,
            a.Bottom - (a.Top + 2 * (h + gap)));
        return zones;
    }

    private static NativeMethods.Rect[] Large(NativeMethods.Rect a, int n, int active1, int gap)
    {
        // Stable LTR geometry. active1 kept for API compat; sizes do not relocate zones.
        _ = active1;
        var zones = new NativeMethods.Rect[n];
        if (n == 1)
        {
            zones[0] = a;
            return zones;
        }

        if (n == 2)
        {
            int usable = Math.Max(1, a.Width - gap);
            int sideW = Math.Max(1, (int)Math.Round(usable * 0.12));
            int mainW = Math.Max(1, usable - sideW);
            ApplyTwoPaneThinFloor(ref mainW, ref sideW, usable, MinSideWidth);
            zones[0] = Box(a.Left, a.Top, mainW, a.Height);
            zones[1] = Box(a.Left + mainW + gap, a.Top, sideW, a.Height);
            return zones;
        }

        if (n == 3)
        {
            // Base 15|70|15, then side floor (~340). Center absorbs. Disjoint � no MinWidth inflate.
            int usable = Math.Max(1, a.Width - 2 * gap);
            int side = Math.Max(1, (int)Math.Round(usable * 0.12));
            int main = Math.Max(1, usable - 2 * side);
            int left = side, right = side;
            ApplyThreePaneSideFloor(ref left, ref main, ref right, usable, MinSideWidth);
            zones[0] = Box(a.Left, a.Top, left, a.Height);
            zones[1] = Box(a.Left + left + gap, a.Top, main, a.Height);
            zones[2] = Box(a.Left + left + gap + main + gap, a.Top,
                a.Right - (a.Left + left + gap + main + gap), a.Height);
            return zones;
        }

        // 4: 2x2 with left column wider (~70/30)
        int leftW = Math.Max(MinPaneWidth, (int)(a.Width * 0.70));
        leftW = Math.Min(leftW, a.Width - MinPaneWidth - gap);
        int rightW = a.Width - leftW - gap;
        int halfH = (a.Height - gap) / 2;
        zones[0] = Box(a.Left, a.Top, leftW, halfH);
        zones[1] = Box(a.Left + leftW + gap, a.Top, rightW, halfH);
        zones[2] = Box(a.Left, a.Top + halfH + gap, leftW, a.Bottom - (a.Top + halfH + gap));
        zones[3] = Box(a.Left + leftW + gap, a.Top + halfH + gap, rightW,
            a.Bottom - (a.Top + halfH + gap));
        return zones;
    }
    private static NativeMethods.Rect Box(int left, int top, int width, int height)
        => new()
        {
            Left = left,
            Top = top,
            Right = left + Math.Max(1, width),
            Bottom = top + Math.Max(1, height)
        };

    public static NativeMethods.Rect SubtractStrip(NativeMethods.Rect work, NativeMethods.Rect strip, string tray)
    {
        return tray switch
        {
            "Left" => new NativeMethods.Rect
            {
                Left = Math.Max(work.Left, strip.Right),
                Top = work.Top,
                Right = work.Right,
                Bottom = work.Bottom
            },
            "Bottom" => new NativeMethods.Rect
            {
                Left = work.Left,
                Top = work.Top,
                Right = work.Right,
                Bottom = Math.Min(work.Bottom, strip.Top)
            },
            "Top" => new NativeMethods.Rect
            {
                Left = work.Left,
                Top = Math.Max(work.Top, strip.Bottom),
                Right = work.Right,
                Bottom = work.Bottom
            },
            _ => new NativeMethods.Rect
            {
                Left = work.Left,
                Top = work.Top,
                Right = Math.Min(work.Right, strip.Left),
                Bottom = work.Bottom
            }
        };
    }
}
