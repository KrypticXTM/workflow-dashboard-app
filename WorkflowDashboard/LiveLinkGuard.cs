namespace WorkflowDashboard;

/// <summary>
/// After a pane click or a fresh link, the dashboard may deactivate because Focus
/// moved onto the tiled HWND. Yielding Live TOPMOST in that window buries every
/// tile under the opaque desk (they look like they disappeared).
/// The same yield also fires while the user is still picking / dragging a window
/// to link (hover over a second candidate): OLE drag deactivates the desk, and
/// raising the unassigned foreground HWND then covers already-linked tiles.
/// </summary>
internal static class LiveLinkGuard
{
    public static bool ShouldSuppressOutsideYield(
        DateTime nowUtc,
        DateTime paneClickQuietUntilUtc,
        DateTime linkQuietUntilUtc,
        DateTime linkInteractionQuietUntilUtc = default)
        => nowUtc < paneClickQuietUntilUtc
        || nowUtc < linkQuietUntilUtc
        || nowUtc < linkInteractionQuietUntilUtc;

    /// <summary>
    /// Assign-to-pane in Fill mode wants to keep the HWND Live (clamped), not
    /// immediately collapse/minimize it during the first settle pass.
    /// </summary>
    public static bool ShouldSkipCollapseDuringLinkSettle(DateTime nowUtc, DateTime linkQuietUntilUtc)
        => nowUtc < linkQuietUntilUtc;
}
