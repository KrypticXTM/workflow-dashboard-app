using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace WorkflowDashboard;

internal sealed class HotkeyService : IDisposable
{
    public const int HotkeyIdMute = 0x5701;
    public const int HotkeyIdFocus = 0x5702;
    public const int HotkeyIdDeskFocus = 0x5703;
    public const int HotkeyIdSkipAd = 0x5704;

    public const uint VK_F8 = 0x77;
    public const uint VK_F9 = 0x78;

    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_NOREPEAT = 0x4000;

    private HwndSource? _source;
    private bool _registered;
    private Action? _onMute;
    private Action? _onFocus;
    private Action? _onDeskFocus;
    private Action? _onSkipAd;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private const int WM_HOTKEY = 0x0312;

    /// <summary>SkipAdVk is the VK that was registered for Skip ad (F8 preferred, else F9), or 0.</summary>
    public uint SkipAdVk { get; private set; }

    public string SkipAdHotkeyLabel =>
        SkipAdVk == VK_F8 ? "F8" : SkipAdVk == VK_F9 ? "F9" : "";

    public bool Register(Window window, Action onMute, Action onFocus, Action? onDeskFocus = null, Action? onSkipAd = null)
    {
        _onMute = onMute;
        _onFocus = onFocus;
        _onDeskFocus = onDeskFocus;
        _onSkipAd = onSkipAd;
        SkipAdVk = 0;
        var helper = new WindowInteropHelper(window);
        helper.EnsureHandle();
        _source = HwndSource.FromHwnd(helper.Handle);
        if (_source == null) return false;
        _source.AddHook(WndProc);

        bool a = RegisterHotKey(helper.Handle, HotkeyIdMute, MOD_CONTROL | MOD_SHIFT | MOD_NOREPEAT, (uint)'M');
        bool b = RegisterHotKey(helper.Handle, HotkeyIdFocus, MOD_CONTROL | MOD_SHIFT | MOD_NOREPEAT, (uint)'F');
        bool c = RegisterHotKey(helper.Handle, HotkeyIdDeskFocus, MOD_CONTROL | MOD_SHIFT | MOD_NOREPEAT, (uint)'D');
        bool d = false;
        if (onSkipAd != null)
        {
            // Prefer single-key F8; fall back to F9 if F8 is taken globally.
            if (RegisterHotKey(helper.Handle, HotkeyIdSkipAd, MOD_NOREPEAT, VK_F8))
            {
                d = true;
                SkipAdVk = VK_F8;
            }
            else if (RegisterHotKey(helper.Handle, HotkeyIdSkipAd, MOD_NOREPEAT, VK_F9))
            {
                d = true;
                SkipAdVk = VK_F9;
            }
        }
        _registered = a || b || c || d;
        return _registered;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY)
        {
            int id = wParam.ToInt32();
            if (id == HotkeyIdMute)
            {
                _onMute?.Invoke();
                handled = true;
            }
            else if (id == HotkeyIdFocus)
            {
                _onFocus?.Invoke();
                handled = true;
            }
            else if (id == HotkeyIdDeskFocus)
            {
                _onDeskFocus?.Invoke();
                handled = true;
            }
            else if (id == HotkeyIdSkipAd)
            {
                _onSkipAd?.Invoke();
                handled = true;
            }
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_source != null)
        {
            try
            {
                var hwnd = _source.Handle;
                if (_registered && hwnd != IntPtr.Zero)
                {
                    UnregisterHotKey(hwnd, HotkeyIdMute);
                    UnregisterHotKey(hwnd, HotkeyIdFocus);
                    UnregisterHotKey(hwnd, HotkeyIdDeskFocus);
                    UnregisterHotKey(hwnd, HotkeyIdSkipAd);
                }
            }
            catch { }
            try { _source.RemoveHook(WndProc); } catch { }
            _source = null;
            _registered = false;
        }
    }
}