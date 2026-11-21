using System.Globalization;
using System.Windows.Input;

namespace RadialMenu1;

public readonly record struct MenuHotkey(Key Key, ModifierKeys Modifiers, int MouseButton = 0)
{
    public static MenuHotkey Default => new(Key.Q, ModifierKeys.Control | ModifierKeys.Alt);

    internal bool CanRegister =>
        (Modifiers & ~(ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift | ModifierKeys.Windows)) == 0 &&
        (MouseButton is 4 or 5 ? Key == Key.None : MouseButton == 0 && IsKeyboardKey);

    private bool IsKeyboardKey =>
        Enum.IsDefined(Key) && KeyInterop.VirtualKeyFromKey(Key) != 0 &&
        Key is not (Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or
            Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.F12) &&
        (Key != Key.Escape || Modifiers != ModifierKeys.None);

    public override string ToString()
    {
        var parts = new List<string>();
        if (Modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(MouseButton is 4 or 5
            ? $"Mouse{MouseButton}"
            : new KeyConverter().ConvertToString(null, CultureInfo.InvariantCulture, Key) ?? Key.ToString());
        return string.Join(" + ", parts);
    }
}
