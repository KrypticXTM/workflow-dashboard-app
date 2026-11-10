using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace WorkflowDashboard;

public sealed class WindowEntry : INotifyPropertyChanged
{
    public IntPtr Hwnd { get; init; }
    public string Title { get; init; } = "";
    public uint ProcessId { get; init; }
    public string ProcessName { get; init; } = "";
    public ImageSource? Icon { get; init; }

    public string ShortTitle => string.IsNullOrWhiteSpace(Title) ? ProcessName : Title;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
