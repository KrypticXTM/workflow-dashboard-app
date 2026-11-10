using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;

namespace WorkflowDashboard;

/// <summary>
/// A Border that is also a UI Automation "Button" with an Invoke pattern. Used for the color swatches and
/// the Windows-list link chips so screen readers / automation can trigger them; look and mouse behavior
/// are unchanged (they still use their own MouseLeftButtonUp handlers).
/// </summary>
public class InvokableBorder : Border
{
    public event RoutedEventHandler? Invoked;

    internal void RaiseInvoked() => Invoked?.Invoke(this, new RoutedEventArgs());

    protected override AutomationPeer OnCreateAutomationPeer() => new InvokableBorderAutomationPeer(this);
}

internal sealed class InvokableBorderAutomationPeer : FrameworkElementAutomationPeer, IInvokeProvider
{
    public InvokableBorderAutomationPeer(InvokableBorder owner) : base(owner) { }

    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Button;
    protected override string GetClassNameCore() => nameof(InvokableBorder);
    protected override bool IsControlElementCore() => true;
    protected override bool IsContentElementCore() => true;

    protected override string GetNameCore()
    {
        var n = base.GetNameCore();
        if (!string.IsNullOrEmpty(n)) return n;
        return (Owner as FrameworkElement)?.ToolTip as string ?? string.Empty;
    }

    public override object GetPattern(PatternInterface patternInterface)
        => patternInterface == PatternInterface.Invoke ? this : base.GetPattern(patternInterface);

    public void Invoke()
    {
        if (!IsEnabled()) throw new ElementNotEnabledException();
        var owner = (InvokableBorder)Owner;
        owner.Dispatcher.BeginInvoke(new Action(owner.RaiseInvoked));
    }
}
