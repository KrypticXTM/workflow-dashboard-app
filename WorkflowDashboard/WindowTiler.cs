using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace WorkflowDashboard;

/// <summary>One real window assigned into a pane (or parked as a chip).</summary>
internal sealed class WorkingWindow : INotifyPropertyChanged
{
    public IntPtr Hwnd { get; init; }
    public string Title { get; set; } = "";
    public string ProcessName { get; set; } = "";
    public ImageSource? Icon { get; set; }

    private int _zoneIndex;
    public int ZoneIndex
    {
        get => _zoneIndex;
        set { if (_zoneIndex != value) { _zoneIndex = value; OnPropertyChanged(); OnPropertyChanged(nameof(ZoneLabel)); OnPropertyChanged(nameof(ChipLabel)); OnPropertyChanged(nameof(DisplayPaneNumber)); } }
    }

    private bool _parked;
    public bool Parked
    {
        get => _parked;
        set { if (_parked != value) { _parked = value; OnPropertyChanged(); OnPropertyChanged(nameof(ChipLabel)); } }
    }

    public NativeMethods.Rect RestoreRect { get; set; }
    public bool HasRestore { get; set; }
    /// <summary>Window was maximized when first linked (re-opens maximized after unlink).</summary>
    public bool WasMaximized { get; set; }

    /// <summary>LiveFill = real HWND tiled into pane; PreviewProxy = DWM thumbnail (min too large).</summary>
    private bool _previewProxy;
    public bool PreviewProxy
    {
        get => _previewProxy;
        set
        {
            if (_previewProxy == value) return;
            _previewProxy = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ChipLabel));
            OnPropertyChanged(nameof(LinkModeLabel));
        }
    }

    public string LinkModeLabel => Collapsed ? "Collapsed" : Floating ? "Floating" : PreviewProxy ? "Preview" : "Live";

    /// <summary>
    /// v1.0.1 collapse: pane too small for the app, so the real window is minimized (still linked/tracked)
    /// and the pane shows a compact card. Always paired with PreviewProxy=true so every Live z-order /
    /// heal / TOPMOST path (which already skips PreviewProxy) leaves the HWND alone.
    /// </summary>
    private bool _collapsed;
    public bool Collapsed
    {
        get => _collapsed;
        set
        {
            if (_collapsed == value) return;
            _collapsed = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(LinkModeLabel));
        }
    }
    /// <summary>User collapsed it explicitly: never auto re-expand on resize.</summary>
    public bool CollapsedManual { get; set; }
    /// <summary>Expanded while the pane was still too small: floats centered over the desk (TOPMOST band), still linked.</summary>
    private bool _floating;
    public bool Floating
    {
        get => _floating;
        set
        {
            if (_floating == value) return;
            _floating = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(LinkModeLabel));
        }
    }
    /// <summary>Physical-px size the pane needed when it collapsed (hysteresis baseline).</summary>
    public int CollapseNeedW { get; set; }
    public int CollapseNeedH { get; set; }

    /// <summary>Optional crop in DWM thumbnail source coordinates.</summary>
    public NativeMethods.Rect? CropRect { get; set; }
    public bool HasCrop => CropRect is { } r && r.Width > 1 && r.Height > 1;

    /// <summary>Show doesn't-fit chooser overlay until user picks an option.</summary>
    public bool ShowFitPrompt { get; set; }

    public string ShortTitle
    {
        get
        {
            var t = string.IsNullOrWhiteSpace(Title) ? ProcessName : Title;
            if (t.Length <= 22) return t;
            return t[..19] + "...";
        }
    }

    /// <summary>1-based visual left-to-right pane number (set by MainWindow from screen X).</summary>
    private int _displayPaneNumber;
    public int DisplayPaneNumber
    {
        get => _displayPaneNumber > 0 ? _displayPaneNumber : ZoneIndex + 1;
        set
        {
            int v = value;
            if (_displayPaneNumber != v)
            {
                _displayPaneNumber = v;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ZoneLabel));
                OnPropertyChanged(nameof(ChipLabel));
            }
        }
    }

    public string ZoneLabel => Parked ? "Park" : $"P{DisplayPaneNumber}";
    public string ChipLabel => PreviewProxy ? $"{ZoneLabel} | Preview | {ShortTitle}" : $"{ZoneLabel} | {ShortTitle}";

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public void NotifyTitle()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(ShortTitle));
        OnPropertyChanged(nameof(ChipLabel));
    }
}

/// <summary>
/// Tracks assigned HWNDs for contained panes (thumbnails + Focus).
/// Optional Arrange uses SetWindowPos into monitor zones — no desktop overlays.
/// </summary>
internal sealed class WindowTiler
{
    private readonly WorkingWindow?[] _byZone = new WorkingWindow?[4];
    public ObservableCollection<WorkingWindow> WorkingSet { get; } = new();

    public WorkingWindow? GetZone(int zoneIndex)
        => zoneIndex >= 0 && zoneIndex < 4 ? _byZone[zoneIndex] : null;

    /// <summary>Swap two zone slots (used when Expanding a non-first pane to one-pane desk).</summary>
    public void SwapZones(int a, int b)
    {
        if (a < 0 || a >= 4 || b < 0 || b >= 4 || a == b) return;
        var wa = _byZone[a];
        var wb = _byZone[b];
        _byZone[a] = wb;
        _byZone[b] = wa;
        if (wa != null) wa.ZoneIndex = b;
        if (wb != null) wb.ZoneIndex = a;
    }

    /// <summary>
    /// Assign <paramref name="entry"/> into <paramref name="zoneIndex"/>.
    /// Never clears unrelated panes: only the target zone (replace/swap) and
    /// optionally the source zone if this HWND is moving from another pane.
    /// When <paramref name="swapIfOccupied"/> is true and the target already has
    /// a different window, swap the two assignments instead of wiping the target.
    /// Preserves original RestoreRect across pane moves so Unlink does not leave
    /// the window stuck at a tiny tiled size.
    /// </summary>
    public bool Assign(WindowEntry entry, int zoneIndex, bool tile = false, bool activate = false, NativeMethods.Rect[]? zones = null, bool swapIfOccupied = false)
    {
        if (entry.Hwnd == IntPtr.Zero || zoneIndex < 0 || zoneIndex >= 4)
            return false;

        // Already in the requested zone — keep other panes untouched.
        if (_byZone[zoneIndex] is { } same && same.Hwnd == entry.Hwnd)
        {
            if (tile && zones != null && zoneIndex < zones.Length)
                return NativeMethods.TileWindow(entry.Hwnd, zones[zoneIndex], activate);
            if (activate) NativeMethods.FocusWindow(entry.Hwnd);
            return entry.Hwnd != IntPtr.Zero && (!tile || NativeMethods.IsWindow(entry.Hwnd));
        }

        // If this HWND already lives in another zone, remember it for move/swap.
        var existing = WorkingSet.FirstOrDefault(w => w.Hwnd == entry.Hwnd);
        int sourceZone = existing is { Parked: false } ? existing.ZoneIndex : -1;

        var occupant = _byZone[zoneIndex];

        // Swap: exchange source and target without dropping either assignment.
        if (swapIfOccupied && occupant != null && occupant.Hwnd != entry.Hwnd && sourceZone >= 0 && sourceZone != zoneIndex)
        {
            SwapZones(sourceZone, zoneIndex);
            if (tile && zones != null)
            {
                if (zoneIndex < zones.Length)
                    NativeMethods.TileWindow(entry.Hwnd, zones[zoneIndex], activate);
                if (sourceZone < zones.Length && _byZone[sourceZone] != null)
                    NativeMethods.TileWindow(_byZone[sourceZone]!.Hwnd, zones[sourceZone], activate: false);
            }
            else if (activate)
                NativeMethods.FocusWindow(entry.Hwnd);
            return entry.Hwnd != IntPtr.Zero && (!tile || NativeMethods.IsWindow(entry.Hwnd));
        }

        // Capture original desktop bounds BEFORE Unassign removes the WorkingWindow.
        NativeMethods.Rect restore = default;
        bool hasRestore = false;
        bool wasMaximized = existing?.WasMaximized ?? NativeMethods.IsZoomed(entry.Hwnd);
        if (existing != null && existing.HasRestore && existing.RestoreRect.Width >= 200 && existing.RestoreRect.Height >= 120)
        {
            restore = existing.RestoreRect;
            hasRestore = true;
        }

        // Invariant: each HWND in at most ONE pane. Strip this HWND from every zone/chip first.
        UnbindHwnd(entry.Hwnd, restore: false);
        // Also drop any stale WorkingSet duplicates / orphan zone pointers for this HWND.
        EnforceSingleHwndInvariant();

        // Replace: only clear the target zone occupant. Tim: the displaced window is handed back
        // minimized (restored size/maximize state kept for when he re-opens it), never left
        // behind the desk and never closed. Swaps returned above, so this is a true displacement.
        LastDisplacedHwnd = IntPtr.Zero;
        if (_byZone[zoneIndex] is { } prior && prior.Hwnd != entry.Hwnd)
        {
            LastDisplacedHwnd = prior.Hwnd;
            Unassign(prior, restore: true, minimize: true);
        }

        if (!hasRestore)
        {
            hasRestore = NativeMethods.GetWindowRect(entry.Hwnd, out restore);
            hasRestore = hasRestore && restore.Width >= 200 && restore.Height >= 120;
            // If current rect looks like a tiny tile leftover, still store it but mark weak;
            // Unassign will avoid restoring absurdly small rects.
            if (!hasRestore && NativeMethods.GetWindowRect(entry.Hwnd, out restore) && restore.Width > 0 && restore.Height > 0)
                hasRestore = true;
        }

        var item = new WorkingWindow
        {
            Hwnd = entry.Hwnd,
            Title = entry.Title,
            ProcessName = entry.ProcessName,
            Icon = entry.Icon,
            ZoneIndex = zoneIndex,
            Parked = false,
            RestoreRect = restore,
            HasRestore = hasRestore,
            WasMaximized = wasMaximized
        };

        _byZone[zoneIndex] = item;
        WorkingSet.Add(item);

        if (tile && zones != null && zoneIndex < zones.Length)
            return NativeMethods.TileWindow(entry.Hwnd, zones[zoneIndex], activate);

        if (activate)
            NativeMethods.FocusWindow(entry.Hwnd);

        return entry.Hwnd != IntPtr.Zero && (!tile || NativeMethods.IsWindow(entry.Hwnd));
    }

    /// <summary>Swap two assigned panes (or no-op if either index invalid). Does not clear other panes.</summary>
    public bool TrySwapAssignments(int zoneA, int zoneB)
    {
        if (zoneA < 0 || zoneA >= 4 || zoneB < 0 || zoneB >= 4 || zoneA == zoneB) return false;
        SwapZones(zoneA, zoneB);
        return true;
    }

    public int ArrangeAssigned(NativeMethods.Rect[] zones)
    {
        int moved = 0;
        foreach (var w in WorkingSet.ToList())
        {
            if (w.Parked) continue;
            if (!NativeMethods.IsWindow(w.Hwnd))
            {
                Unassign(w, restore: false);
                continue;
            }
            if (w.ZoneIndex < 0 || w.ZoneIndex >= zones.Length) continue;
            if (NativeMethods.TileWindow(w.Hwnd, zones[w.ZoneIndex], activate: false))
            {
                // Hard clamp: never leave a linked HWND crossing zone edges (neighbors/sidebar).
                if (NativeMethods.OuterExceedsZone(w.Hwnd, zones[w.ZoneIndex], tol: 2))
                    NativeMethods.HardClampOuterToZone(w.Hwnd, zones[w.ZoneIndex]);
                moved++;
            }
        }
        return moved;
    }

    /// <summary>HWND displaced (and minimized) by the most recent replace-Assign; Zero if none.</summary>
    public IntPtr LastDisplacedHwnd { get; private set; }

    public void Unassign(WorkingWindow item, bool restore) => Unassign(item, restore, minimize: false);

    /// <summary>
    /// <paramref name="minimize"/>: user unlink / displacement. Instead of re-showing the window at its
    /// pre-link bounds (where it lands behind the desk), set those bounds as its restored placement and
    /// minimize it without activation. Exit (RestoreAllAndClear) keeps the plain restore.
    /// </summary>
    /// <summary>Cascade slot for <see cref="NativeMethods.UniformReleaseRect"/> (32 px steps, wraps at 8).</summary>
    private int _releaseSlot;

    public void Unassign(WorkingWindow item, bool restore, bool minimize)
    {
        // Clear every zone slot that points at this instance or the same HWND (no double-link leftovers).
        for (int i = 0; i < 4; i++)
        {
            var z = _byZone[i];
            if (z == null) continue;
            if (ReferenceEquals(z, item) || z.Hwnd == item.Hwnd)
                _byZone[i] = null;
        }

        // Remove every WorkingSet entry for this HWND (chips must stay 1:1 with panes).
        for (int i = WorkingSet.Count - 1; i >= 0; i--)
        {
            if (WorkingSet[i].Hwnd == item.Hwnd || ReferenceEquals(WorkingSet[i], item))
                WorkingSet.RemoveAt(i);
        }

        // Release (unlink / replace / pane trim / exit): uniform normal size for every released window.
        if (restore && NativeMethods.IsWindow(item.Hwnd))
        {
            item.Collapsed = false;
            item.Floating = false;
            item.CollapsedManual = false;
            item.PreviewProxy = false;
            NativeMethods.TrySetCloaked(item.Hwnd, false);
            var nr = NativeMethods.UniformReleaseRect(_releaseSlot++);
            NativeMethods.ReleaseWithPlacement(item.Hwnd, nr, item.WasMaximized, minimize);
            return;
        }

        // Collapsed (minimized) / floating link: hand the window back un-minimized.
        if (item.Collapsed || item.Floating)
        {
            bool wasCollapsed = item.Collapsed;
            item.Collapsed = false;
            item.Floating = false;
            item.CollapsedManual = false;
            item.PreviewProxy = false;
            if (wasCollapsed && NativeMethods.IsWindow(item.Hwnd) && NativeMethods.IsIconic(item.Hwnd))
                NativeMethods.ShowWindow(item.Hwnd, NativeMethods.SW_SHOWNOACTIVATE);
        }

        // Always drop Live TOPMOST pin before restore / abandon.
        if (NativeMethods.IsWindow(item.Hwnd))
            NativeMethods.ClearLiveTopmost(item.Hwnd);

        if (restore && item.HasRestore && NativeMethods.IsWindow(item.Hwnd))
        {
            NativeMethods.TileWindow(item.Hwnd, SaneRestoreRect(item), activate: false);
        }
    }

    private static NativeMethods.Rect SaneRestoreRect(WorkingWindow item)
    {
        {
            var r = item.RestoreRect;
            // Guard: never leave the window stuck tiny from a bad saved tile rect.
            if (r.Width < 200 || r.Height < 120)
            {
                if (NativeMethods.TryGetMonitorWorkArea(item.Hwnd, out var work, out _))
                {
                    int w = Math.Max(640, work.Width / 2);
                    int h = Math.Max(400, work.Height / 2);
                    int left = work.Left + Math.Max(0, (work.Width - w) / 2);
                    int top = work.Top + Math.Max(0, (work.Height - h) / 2);
                    r = new NativeMethods.Rect
                    {
                        Left = left,
                        Top = top,
                        Right = left + w,
                        Bottom = top + h
                    };
                }
            }
            return r;
        }
    }

    public void UnbindHwnd(IntPtr hwnd, bool restore)
    {
        var existing = WorkingSet.FirstOrDefault(w => w.Hwnd == hwnd);
        if (existing != null)
            Unassign(existing, restore);
    }

    public void Park(WorkingWindow item)
    {
        if (item.ZoneIndex >= 0 && item.ZoneIndex < 4 && _byZone[item.ZoneIndex] == item)
            _byZone[item.ZoneIndex] = null;
        item.Parked = true;
        if (NativeMethods.IsWindow(item.Hwnd))
        {
            NativeMethods.ClearLiveTopmost(item.Hwnd);
            NativeMethods.ShowWindow(item.Hwnd, NativeMethods.SW_MINIMIZE);
        }
    }

    /// <summary>Bring HWND to foreground; re-slot parked items without tiling.</summary>
    public void FocusOnly(WorkingWindow item)
    {
        if (!NativeMethods.IsWindow(item.Hwnd)) return;

        if (item.Parked)
        {
            int z = item.ZoneIndex;
            if (z < 0 || z >= 4 || _byZone[z] != null)
            {
                z = -1;
                for (int i = 0; i < 4; i++)
                {
                    if (_byZone[i] == null) { z = i; break; }
                }
            }

            if (z >= 0)
            {
                item.ZoneIndex = z;
                item.Parked = false;
                _byZone[z] = item;
            }
        }

        NativeMethods.FocusWindow(item.Hwnd);
    }

    public void TrimZones(int paneCount, bool restoreExtras)
    {
        for (int i = paneCount; i < 4; i++)
        {
            if (_byZone[i] is { } item)
                Unassign(item, restoreExtras, minimize: restoreExtras); // same release rule as Unlink
        }
    }

    /// <summary>Dashboard exit: every linked window comes back visible at the uniform release size (cascaded).</summary>
    public void RestoreAllAndClear()
    {
        _releaseSlot = 0;
        foreach (var w in WorkingSet.ToList())
            Unassign(w, restore: true);
        for (int i = 0; i < 4; i++)
            _byZone[i] = null;
    }

    public bool PruneDead()
    {
        bool changed = false;
        foreach (var w in WorkingSet.ToList())
        {
            if (!NativeMethods.IsWindow(w.Hwnd))
            {
                Unassign(w, restore: false);
                changed = true;
            }
        }
        if (EnforceSingleHwndInvariant())
            changed = true;
        return changed;
    }

    /// <summary>
    /// Enforce: each HWND is assigned to at most one pane and appears once in WorkingSet.
    /// SoftRefresh / Assign call this so duplicates never accumulate.
    /// </summary>
    /// <returns>True if anything was removed or remapped.</returns>
    public bool EnforceSingleHwndInvariant()
    {
        bool changed = false;

        // 1) Drop WorkingSet duplicates (keep first non-parked, else first).
        var seen = new HashSet<IntPtr>();
        foreach (var w in WorkingSet.ToList())
        {
            if (w.Hwnd == IntPtr.Zero)
            {
                WorkingSet.Remove(w);
                changed = true;
                continue;
            }
            if (!seen.Add(w.Hwnd))
            {
                // Duplicate chip — clear any zone still pointing at this instance, then remove.
                for (int i = 0; i < 4; i++)
                {
                    if (ReferenceEquals(_byZone[i], w))
                        _byZone[i] = null;
                }
                WorkingSet.Remove(w);
                changed = true;
            }
        }

        // 2) Zone map: if two zones share an HWND, keep the lowest index; clear the rest.
        var hwndOwner = new Dictionary<IntPtr, int>();
        for (int i = 0; i < 4; i++)
        {
            var z = _byZone[i];
            if (z == null) continue;
            if (z.Hwnd == IntPtr.Zero || !NativeMethods.IsWindow(z.Hwnd))
            {
                _byZone[i] = null;
                changed = true;
                continue;
            }
            if (hwndOwner.TryGetValue(z.Hwnd, out int first))
            {
                _byZone[i] = null;
                changed = true;
                continue;
            }
            hwndOwner[z.Hwnd] = i;
            if (z.ZoneIndex != i)
            {
                z.ZoneIndex = i;
                changed = true;
            }
            z.Parked = false;
        }

        // 3) WorkingSet entries must match zone map (or be parked).
        foreach (var w in WorkingSet.ToList())
        {
            if (w.Parked) continue;
            int zone = -1;
            for (int i = 0; i < 4; i++)
            {
                if (_byZone[i] != null && _byZone[i]!.Hwnd == w.Hwnd)
                {
                    zone = i;
                    break;
                }
            }
            if (zone < 0)
            {
                // Orphan chip (not in any zone) — remove rather than re-adding a phantom link.
                WorkingSet.Remove(w);
                changed = true;
                continue;
            }
            if (!ReferenceEquals(_byZone[zone], w))
            {
                // Prefer the zone's instance; drop the stray WorkingSet object.
                WorkingSet.Remove(w);
                if (!WorkingSet.Contains(_byZone[zone]!))
                    WorkingSet.Add(_byZone[zone]!);
                changed = true;
                continue;
            }
            if (w.ZoneIndex != zone)
            {
                w.ZoneIndex = zone;
                changed = true;
            }
        }

        // 4) Ensure every zone occupant is in WorkingSet exactly once.
        for (int i = 0; i < 4; i++)
        {
            var z = _byZone[i];
            if (z == null) continue;
            if (!WorkingSet.Contains(z))
            {
                WorkingSet.Add(z);
                changed = true;
            }
        }

        return changed;
    }
}
