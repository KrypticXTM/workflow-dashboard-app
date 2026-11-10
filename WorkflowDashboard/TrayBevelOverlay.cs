using System.Windows;
using System.Windows.Media;

namespace WorkflowDashboard;

/// <summary>
/// v1.0.1 "tray" depth for the desk panes. A non-hit-testing overlay over PaneGrid that draws 1-device-pixel
/// translucent lines only on / directly beside the existing pane outlines (it never changes pane host size,
/// margin, padding, corner radius or zone math, and draws nothing inside the panes):
///   - desk perimeter edges: a faint darker line just OUTSIDE the outline on top/left and a faint lighter line
///     just outside on bottom/right (inside the existing 2px desk padding), so the panes read as recessed;
///   - dividers between adjacent panes: the two touching outline pixels are tinted dark | light (an etched
///     groove), still one divider.
/// Colors come from TrayShadowBrush / TrayHighlightBrush (re-derived per theme). Pixel-snapped per DPI.
/// </summary>
public sealed class TrayBevelOverlay : FrameworkElement
{
    private Rect[] _rects = Array.Empty<Rect>();

    public TrayBevelOverlay()
    {
        IsHitTestVisible = false;
        Focusable = false;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
    }

    public void SetRects(Rect[] rects)
    {
        rects ??= Array.Empty<Rect>();
        if (rects.Length == _rects.Length)
        {
            bool same = true;
            for (int i = 0; i < rects.Length && same; i++)
                same = Math.Abs(rects[i].X - _rects[i].X) < 0.01 && Math.Abs(rects[i].Y - _rects[i].Y) < 0.01
                    && Math.Abs(rects[i].Width - _rects[i].Width) < 0.01 && Math.Abs(rects[i].Height - _rects[i].Height) < 0.01;
            if (same) return;
        }
        _rects = rects;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (_rects.Length == 0) return;
        var shadow = TryFindResource("TrayShadowBrush") as Brush;
        var light = TryFindResource("TrayHighlightBrush") as Brush;
        if (shadow == null || light == null) return;

        double sx = 1.0, sy = 1.0;
        try { var dpi = VisualTreeHelper.GetDpi(this); sx = dpi.DpiScaleX; sy = dpi.DpiScaleY; } catch { }
        double px = 1.0 / sx, py = 1.0 / sy;
        double SX(double v) => Math.Round(v * sx) / sx;
        double SY(double v) => Math.Round(v * sy) / sy;
        void Fill(Brush b, double x, double y, double w, double h)
        {
            if (w <= 0 || h <= 0) return;
            dc.DrawRectangle(b, null, new Rect(x, y, w, h));
        }

        const double tol = 1.5;
        foreach (var r0 in _rects)
        {
            if (r0.Width < 4 || r0.Height < 4) continue;
            bool nL = false, nR = false, nT = false, nB = false;
            foreach (var o in _rects)
            {
                if (o == r0) continue;
                bool vOverlap = o.Top < r0.Bottom - tol && o.Bottom > r0.Top + tol;
                bool hOverlap = o.Left < r0.Right - tol && o.Right > r0.Left + tol;
                if (vOverlap && Math.Abs(o.Right - r0.Left) <= tol) nL = true;
                if (vOverlap && Math.Abs(o.Left - r0.Right) <= tol) nR = true;
                if (hOverlap && Math.Abs(o.Bottom - r0.Top) <= tol) nT = true;
                if (hOverlap && Math.Abs(o.Top - r0.Bottom) <= tol) nB = true;
            }
            double l = SX(r0.Left), t = SY(r0.Top), r = SX(r0.Right), b = SY(r0.Bottom);
            double w = r - l, h = b - t;

            // Top / left: shadow side.
            if (nT) Fill(light, l, t, w, py);            // groove: lower half of an etched horizontal divider
            else Fill(shadow, l - px, t - py, w + px, py);
            if (nL) Fill(light, l, t, px, h);            // groove: right half of an etched vertical divider
            else Fill(shadow, l - px, t, px, h);
            // Bottom / right: lit side.
            if (nB) Fill(shadow, l, b - py, w, py);
            else Fill(light, l, b, w + px, py);
            if (nR) Fill(shadow, r - px, t, px, h);
            else Fill(light, r, t, px, h);
        }
    }
}
