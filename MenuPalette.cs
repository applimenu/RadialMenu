using System.Globalization;
using System.Windows.Media;

namespace RadialMenu1;

public enum MenuTheme { Violet, Dark, Light, Ocean, Forest, Custom, Glass }

internal sealed record MenuPalette(Color Disc, Color Center, Color Accent, Color Text)
{
    public bool HasCustomBackground { get; private init; }
    public bool HasCustomColors { get; private init; }

    public static string DisplayName(MenuTheme theme) => theme switch
    {
        MenuTheme.Violet => "Фиолетовая",
        MenuTheme.Dark => "Тёмная",
        MenuTheme.Light => "Светлая",
        MenuTheme.Ocean => "Океан",
        MenuTheme.Forest => "Лес",
        MenuTheme.Glass => "Жидкое стекло",
        _ => "Свой цвет"
    };

    public Color Rim => Blend(Disc, Accent, 0.45);
    public Color Highlight => KeepTextContrast(Blend(Disc, Accent, 0.35));
    public Color Muted
    {
        get
        {
            var background = Selected;
            if (HasCustomColors)
            {
                foreach (var surface in new[] { Disc, Control, Hover })
                    if (Contrast(surface, Text) < Contrast(background, Text)) background = surface;
            }
            return ContrastAgainst(background, Blend(Disc, Text, 0.82), Text);
        }
    }
    public Color Panel => Blend(Center, Disc, 0.75);
    public Color Control => KeepTextContrast(Blend(Disc, Text, 0.065));
    public Color ControlBorder => Blend(Disc, Text, 0.24);
    public Color Hover => KeepTextContrast(Blend(Control, Accent, 0.13));
    public Color Selected => KeepTextContrast(Blend(Control, Accent, 0.25));
    public Color Focus => ContrastAgainst(Disc, Accent, Text);
    public Color AccentText => ContrastAgainst(Selected, Accent, Text);
    public Color AccentButtonText => Contrast(Accent, Colors.Black) >= Contrast(Accent, Colors.White)
        ? Colors.Black : Colors.White;

    public static MenuPalette From(AppConfig config) => ForTheme(config, config.Theme);

    public static MenuPalette ForTheme(AppConfig config, MenuTheme theme)
    {
        var palette = DefaultForTheme(config, theme);
        var colors = config.ThemeOverrides?.GetValueOrDefault(theme);
        if (TryParseColor(colors?.Accent, out var accent))
            palette = palette with { Accent = accent, HasCustomColors = true };

        if (TryParseColor(colors?.Background, out var background))
        {
            bool darkText = Contrast(background, Colors.Black) >= Contrast(background, Colors.White);
            palette = palette with
            {
                Disc = background,
                Center = Blend(background, darkText ? Colors.White : Colors.Black, 0.18),
                Text = darkText ? Colors.Black : Colors.White,
                HasCustomBackground = true,
                HasCustomColors = true
            };
        }

        return palette;
    }

    private static MenuPalette DefaultForTheme(AppConfig config, MenuTheme theme) => theme switch
    {
        MenuTheme.Dark => Create(0x202227, 0x101115, 0x9CA7B8),
        MenuTheme.Light => Create(0xF5F7FC, 0xFFFFFF, 0x3B59C4, 0x172037),
        MenuTheme.Ocean => Create(0x112D3D, 0x091D28, 0x42C8F5),
        MenuTheme.Forest => Create(0x192E25, 0x0E2018, 0x6CDD98),
        MenuTheme.Glass => Create(0x172D43, 0x0A192B, 0xA1E2FF),
        MenuTheme.Custom when TryParseColor(config.CustomColor, out var accent) =>
            new(Blend(Rgb(0x18191F), accent, 0.15), Rgb(0x101116), accent, Rgb(0xF2F2F7)),
        _ => Create(0x1B1724, 0x09070D, 0xA855F7)
    };

    public static bool TryParseColor(string? input, out Color color)
    {
        var hex = input?.Trim().TrimStart('#');
        if (hex?.Length == 6 && uint.TryParse(hex, NumberStyles.AllowHexSpecifier,
            CultureInfo.InvariantCulture, out uint value))
        {
            color = Rgb(value);
            return true;
        }

        color = default;
        return false;
    }

    private static MenuPalette Create(uint disc, uint center, uint accent, uint text = 0xF2F2F7) =>
        new(Rgb(disc), Rgb(center), Rgb(accent), Rgb(text));

    private static Color Rgb(uint value) =>
        Color.FromRgb((byte)(value >> 16), (byte)(value >> 8), (byte)value);

    private Color KeepTextContrast(Color color) => HasCustomColors ? ContrastAgainst(Text, color, Disc) : color;

    private static Color ContrastAgainst(Color background, Color foreground, Color fallback)
    {
        for (int step = 0; step < 10; step++)
        {
            var candidate = Blend(foreground, fallback, step / 10.0);
            if (Contrast(background, candidate) >= 4.5) return candidate;
        }

        return fallback;
    }

    private static double Contrast(Color a, Color b)
    {
        static double Linear(byte value)
        {
            double channel = value / 255.0;
            return channel <= 0.04045 ? channel / 12.92 : Math.Pow((channel + 0.055) / 1.055, 2.4);
        }

        static double Luminance(Color color) =>
            0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);

        double first = Luminance(a) + 0.05;
        double second = Luminance(b) + 0.05;
        return Math.Max(first, second) / Math.Min(first, second);
    }

    private static Color Blend(Color start, Color end, double amount) => Color.FromRgb(
        (byte)(start.R + (end.R - start.R) * amount),
        (byte)(start.G + (end.G - start.G) * amount),
        (byte)(start.B + (end.B - start.B) * amount));
}
