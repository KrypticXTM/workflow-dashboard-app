namespace WorkflowDashboard;

/// <summary>
/// Opacity of the assign-target pane outline. Holds, then fades out.
/// HoldMs=0 starts fading immediately (used when a link completes).
/// The drawn stroke uses <see cref="PeakOpacity"/> so it stays a soft accent wash,
/// and its brush is the live AccentBrush resource (theme swaps recolor it).
/// </summary>
internal static class LinkTargetIndicator
{
    public const int HoldMs = 2800;
    public const int FadeMs = 400;
    public const double Thickness = 2;
    public const double PeakOpacity = 0.62;

    public static double OpacityAt(double elapsedMs, double holdMs = HoldMs, double fadeMs = FadeMs)
    {
        if (double.IsNaN(elapsedMs) || double.IsInfinity(elapsedMs) || elapsedMs < 0)
            return 0;
        if (holdMs < 0) holdMs = 0;
        if (fadeMs < 0) fadeMs = 0;
        if (elapsedMs < holdMs) return 1;
        if (fadeMs <= 0 || elapsedMs >= holdMs + fadeMs) return 0;
        return Math.Clamp(1.0 - ((elapsedMs - holdMs) / fadeMs), 0, 1);
    }

    public static double CombinedOpacity(double elapsedMs, double holdMs = HoldMs, double fadeMs = FadeMs)
        => OpacityAt(elapsedMs, holdMs, fadeMs) * PeakOpacity;

    public static bool IsVisible(double opacity) => opacity > 0.01;
}
