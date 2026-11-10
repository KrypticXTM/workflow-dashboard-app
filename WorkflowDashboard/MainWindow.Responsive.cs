using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace WorkflowDashboard;

/// <summary>
/// v1.0.1 responsive header + hidden LiveDiag status.
///
/// Header modes (chosen from the groups' natural widths vs the header width; deterministic, no feedback loop):
///   0 wide      - center group centered over the full header, right group right-aligned (classic look)
///   1 medium    - center group left-aligned on the same row (would otherwise run under the right group)
///   2 compact   - group labels ("Panes" / "Layout" / "Size") hidden, still one row
///   3 narrow    - two rows: center group on row 0, right group right-aligned on row 1
/// Both groups are WrapPanels capped at the header width, so even below mode 3 they wrap instead of
/// overlapping (any window width, any UI zoom). The window keeps its MinWidth of 980 DIP.
///
/// LiveDiag: the self-check still writes %LOCALAPPDATA%\WorkflowDashboard\live-diag.txt, but its verdict
/// is only echoed to the status bar when enabled with Ctrl+Shift+F12 (or env WD_LIVEDIAG=1).
/// </summary>
public partial class MainWindow
{
    private int _headerMode = -1;
    private bool _headerUpdating;
    private bool _liveDiagVisible =
        string.Equals(Environment.GetEnvironmentVariable("WD_LIVEDIAG"), "1", StringComparison.Ordinal);

    private void HeaderChrome_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged) UpdateHeaderResponsive();
    }

    private void HeaderRightGroup_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Button captions can change (e.g. "Collapse" <-> "Windows"); re-evaluate after this layout pass.
        if (!e.WidthChanged || _headerUpdating) return;
        Dispatcher.BeginInvoke(new Action(UpdateHeaderResponsive), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private static double NaturalWidth(FrameworkElement el)
    {
        el.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return el.DesiredSize.Width;
    }

    private void SetHeaderLabelsVisible(bool on)
    {
        var v = on ? Visibility.Visible : Visibility.Collapsed;
        if (TxtHdrPanes != null) TxtHdrPanes.Visibility = v;
        if (TxtHdrLayout != null) TxtHdrLayout.Visibility = v;
        if (TxtHdrSize != null) TxtHdrSize.Visibility = v;
    }

    private void UpdateHeaderResponsive()
    {
        if (_headerUpdating) return;
        if (HeaderChrome == null || HeaderCenterGroup == null || HeaderRightGroup == null) return;
        double w = HeaderChrome.ActualWidth;
        if (w <= 1 || HeaderChrome.Visibility != Visibility.Visible) return;
        _headerUpdating = true;
        try
        {
            const double gap = 12;
            HeaderCenterGroup.MaxWidth = double.PositiveInfinity;
            HeaderRightGroup.MaxWidth = double.PositiveInfinity;

            SetHeaderLabelsVisible(true);
            double c = NaturalWidth(HeaderCenterGroup);
            double r = NaturalWidth(HeaderRightGroup);
            SetHeaderLabelsVisible(false);
            double cS = NaturalWidth(HeaderCenterGroup);
            double rS = NaturalWidth(HeaderRightGroup);
            int ModeFor(double ww)
            {
                if (ww >= c + 2 * (r + gap)) return 0;
                if (ww >= c + r + gap) return 1;
                return ww >= cS + rS + gap ? 2 : 3;
            }
            int mode = ModeFor(w);
            // Hysteresis (resize blinking): moving to a WIDER mode needs 24 DIP of headroom, so dragging
            // the frame across a breakpoint doesn't flip 1-row/2-row (which moves the desk + retiles).
            if (_headerMode >= 0 && mode < _headerMode)
                mode = Math.Min(_headerMode, ModeFor(w - 24));

            SetHeaderLabelsVisible(mode <= 1);
            Grid.SetRow(HeaderCenterGroup, 0);
            HeaderCenterGroup.HorizontalAlignment = mode == 0 ? HorizontalAlignment.Center : HorizontalAlignment.Left;
            Grid.SetRow(HeaderRightGroup, mode == 3 ? 1 : 0);
            HeaderRightGroup.HorizontalAlignment = HorizontalAlignment.Right;
            HeaderRightGroup.Margin = mode == 3 ? new Thickness(0, 6, 0, 0) : new Thickness(0);
            HeaderCenterGroup.MaxWidth = w;
            HeaderRightGroup.MaxWidth = w;

            if (mode != _headerMode)
            {
                bool rowsChanged = (_headerMode == 3) != (mode == 3);
                _headerMode = mode;
                // Header height changes move the desk without a window resize: retile the Live panes.
                if (rowsChanged && IsLoaded) ScheduleLiveRetile();
            }
        }
        catch { }
        finally
        {
            _headerUpdating = false;
        }
    }

    private bool HandleLiveDiagHotkey(KeyEventArgs e)
    {
        if (e.Key != Key.F12) return false;
        if ((Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != (ModifierKeys.Control | ModifierKeys.Shift))
            return false;
        _liveDiagVisible = !_liveDiagVisible;
        try { StatusText.Text = _liveDiagVisible ? "LiveDiag status on (Ctrl+Shift+F12 to hide)" : "LiveDiag status hidden"; } catch { }
        e.Handled = true;
        return true;
    }

    /// <summary>Feeds the pane root rects (overlay coordinates) to the tray-depth overlay; cheap no-op when unchanged.</summary>
    private void UpdateTrayBevel()
    {
        if (TrayBevel == null || _slots == null) return;
        try
        {
            var list = new List<Rect>(4);
            for (int i = 0; i < _slots.Length && i < _paneCount; i++)
            {
                var root = _slots[i]?.Root;
                if (root == null || !root.IsVisible || root.ActualWidth < 4 || root.ActualHeight < 4) continue;
                var tr = root.TransformToVisual(TrayBevel);
                list.Add(tr.TransformBounds(new Rect(0, 0, root.ActualWidth, root.ActualHeight)));
            }
            TrayBevel.SetRects(list.ToArray());
        }
        catch { }
    }
}
