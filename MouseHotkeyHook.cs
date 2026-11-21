using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;

namespace RadialMenu1;

internal sealed class MouseHotkeyHook(Func<MenuHotkey, Point, bool> onPress, Action<Point> onRelease) : IDisposable
{
    private const int LowLevelMouse = 14;
    private const int XButtonDown = 0x020B;
    private const int XButtonUp = 0x020C;

    private delegate nint HookCallback(int code, nint message, nint data);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowsHookExW(int id, HookCallback callback, nint module, uint threadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(nint hook);

    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hook, int code, nint message, nint data);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);

    [DllImport("kernel32.dll")]
    private static extern nint GetModuleHandleW(nint moduleName);

    private nint _handle;
    private HookCallback? _callback;
    private int _pressedButton;

    public bool Start()
    {
        if (_handle != 0) return true;
        _callback = HandleMouse;
        _handle = SetWindowsHookExW(LowLevelMouse, _callback, GetModuleHandleW(0), 0);
        return _handle != 0;
    }

    private nint HandleMouse(int code, nint message, nint data)
    {
        if (code == 0 && (message == XButtonDown || message == XButtonUp))
        {
            // Старшее слово mouseData в MSLLHOOKSTRUCT содержит номер боковой кнопки.
            int button = ((Marshal.ReadInt32(data, 8) >> 16) & 0xFFFF) switch { 1 => 4, 2 => 5, _ => 0 };
            if (button != 0 && message == XButtonDown && button == _pressedButton) return 1;
            if (button != 0 && message == XButtonDown && _pressedButton == 0 &&
                onPress(new(Key.None, ReadModifiers(), button), ReadPoint(data)))
            {
                _pressedButton = button;
                return 1;
            }
            if (button != 0 && message == XButtonUp && button == _pressedButton)
            {
                _pressedButton = 0;
                onRelease(ReadPoint(data));
                return 1;
            }
        }

        return CallNextHookEx(0, code, message, data);
    }

    private static Point ReadPoint(nint data) => new(Marshal.ReadInt32(data), Marshal.ReadInt32(data, 4));

    private static ModifierKeys ReadModifiers()
    {
        static bool Down(int key) => GetAsyncKeyState(key) < 0;
        var result = ModifierKeys.None;
        if (Down(0x10)) result |= ModifierKeys.Shift;
        if (Down(0x11)) result |= ModifierKeys.Control;
        if (Down(0x12)) result |= ModifierKeys.Alt;
        if (Down(0x5B) || Down(0x5C)) result |= ModifierKeys.Windows;
        return result;
    }

    public void Dispose()
    {
        if (_handle != 0) UnhookWindowsHookEx(_handle);
        _handle = 0;
        _pressedButton = 0;
    }
}
