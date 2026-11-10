using System.Windows;

namespace WorkflowDashboard;

/// <summary>
/// Attached flags for the keycap Button template (App.xaml).
/// Lit = the key's face is already "inverted" (accent face + AccentTextBrush text, e.g. the selected Docs
/// source). On hover a lit key keeps its own face and text and only gains the accent glow/border, so a
/// light accent never ends up as dark text on the dim accent hover face.
/// </summary>
public static class KeyCap
{
    public static readonly DependencyProperty LitProperty = DependencyProperty.RegisterAttached(
        "Lit", typeof(bool), typeof(KeyCap), new FrameworkPropertyMetadata(false));

    public static bool GetLit(DependencyObject d) => (bool)d.GetValue(LitProperty);
    public static void SetLit(DependencyObject d, bool value) => d.SetValue(LitProperty, value);

    /// <summary>
    /// Recessed = an unselected tab key (e.g. Docs source Files / Downloads / Desktop): it sits sunk into the
    /// keyboard (pressed geometry, no shadow) until it becomes Lit (selected), which raises it as a full keycap.
    /// </summary>
    public static readonly DependencyProperty RecessedProperty = DependencyProperty.RegisterAttached(
        "Recessed", typeof(bool), typeof(KeyCap), new FrameworkPropertyMetadata(false));

    public static bool GetRecessed(DependencyObject d) => (bool)d.GetValue(RecessedProperty);
    public static void SetRecessed(DependencyObject d, bool value) => d.SetValue(RecessedProperty, value);
}
