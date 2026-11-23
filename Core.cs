using System.IO;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RadialMenu1;

// =========================================================================
//  Модели: пункт меню, режим, общие настройки
// =========================================================================

public abstract class Notifier : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Raise(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>Один пункт (сектор) меню.</summary>
public sealed class Entry : Notifier
{
    private string _name = "";
    private string _target = "";
    private string _glyph = "";

    public Entry() { }

    public Entry(string name, string target, string glyph = "")
    {
        _name = name;
        _target = target;
        _glyph = glyph;
    }

    /// <summary>Название, которое видно в центре меню.</summary>
    public string Name
    {
        get => _name;
        set { _name = value; Raise(nameof(Name)); }
    }

    /// <summary>Что запускать: путь к программе, папке, файлу, адрес сайта или системная команда.</summary>
    public string Target
    {
        get => _target;
        set { _target = value; Raise(nameof(Target)); }
    }

    /// <summary>Значок из системного шрифта. Если пусто, берётся значок самого файла.</summary>
    public string Glyph
    {
        get => _glyph;
        set { _glyph = value; Raise(nameof(Glyph)); }
    }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Mode? Submenu { get; set; }

    [JsonIgnore]
    public ImageSource Icon => IconService.Get(this);
}

/// <summary>Режим: набор пунктов (например, «Основной» или «Закладки»).</summary>
public sealed class Mode : Notifier
{
    private string _name = "";

    public Mode() { }

    public Mode(string name) => _name = name;

    public string Name
    {
        get => _name;
        set { _name = value; Raise(nameof(Name)); }
    }

    public ObservableCollection<Entry> Items { get; set; } = new();

    internal IEnumerable<Entry> VisibleItems(bool submenusEnabled) =>
        Items.Where(entry => entry.Submenu == null ||
            (submenusEnabled && entry.Submenu.VisibleItems(true).Any()));
}

public enum MenuStyle { Classic, Circles, Honeycomb, Ring }

[Flags]
public enum StatusMetrics
{
    None = 0,
    Cpu = 1,
    Audio = 2,
    Battery = 4,
    Timer = 8,
    Gpu = 16,
    Memory = 32,
    Disk = 64,
    Network = 128,
    Default = Cpu | Audio | Battery | Timer,
    All = Default | Gpu | Memory | Disk | Network
}

public sealed class AppConfig
{
    public const int MinMenuScale = 25;
    public const int MaxMenuScale = 100;
    public const int MenuScaleStep = 5;

    private int _menuScalePercent = MaxMenuScale;

    public ObservableCollection<Mode> Modes { get; set; } = new();
    public int ActiveMode { get; set; }
    public MenuHotkey Hotkey { get; set; } = MenuHotkey.Default;
    public bool SelectOnRelease { get; set; }
    public bool SubmenusEnabled { get; set; } = true;
    public bool PauseInGames { get; set; }
    public ObservableCollection<string> GameExecutables { get; set; } = new();
    public int MenuScalePercent
    {
        get => _menuScalePercent;
        set
        {
            int clamped = Math.Clamp(value, MinMenuScale, MaxMenuScale);
            _menuScalePercent = (int)Math.Round(clamped / (double)MenuScaleStep) * MenuScaleStep;
        }
    }
    public MenuTheme Theme { get; set; } = MenuTheme.Violet;
    public MenuStyle MenuStyle { get; set; }
    public string CustomColor { get; set; } = "#A855F7";
    public Dictionary<MenuTheme, ThemeColors> ThemeOverrides { get; set; } = new();
    public bool AnimationsEnabled { get; set; } = true;
    public bool SettingsPreviewVisible { get; set; } = true;
    public bool SelectionEffectsEnabled { get; set; } = true;
    public bool StatusRingEnabled { get; set; } = true;
    public StatusMetrics VisibleStatusMetrics { get; set; } = StatusMetrics.Default;
    public bool HideWidgetsInGames { get; set; }
    public bool CompactWidgetsInGames { get; set; } = true;
    public HashSet<string> CompactDesktopWidgets { get; set; } = new();
    public Dictionary<string, WidgetPlacement> WidgetPlacements { get; set; } = new();
    public HashSet<string> WidgetsWithoutDetail { get; set; } = new();
    internal bool IsModeAvailable(int index) =>
        index >= 0 && index < Modes.Count && Modes[index].VisibleItems(SubmenusEnabled).Any();

    internal int AvailableModeIndex(int start, int direction)
    {
        int count = Modes.Count;
        if (count == 0) return -1;
        int index = Math.Clamp(start, 0, count - 1);
        for (int i = 0; i < count; i++)
        {
            if (IsModeAvailable(index)) return index;
            index = (index + direction + count) % count;
        }
        return -1;
    }
    private int _transparencyPercent;
    public int TransparencyPercent
    {
        get => _transparencyPercent;
        set => _transparencyPercent = Math.Clamp(value, 0, 100);
    }

    public static AppConfig CreateDefault() => new()
    {
        Modes =
        {
            new Mode("Основной")
            {
                Items =
                {
                    new Entry("Скриншот", "ms-screenclip:", "\uE722"),
                    new Entry("Блокнот", "notepad.exe", "\uE70F"),
                    new Entry("Калькулятор", "calc.exe", "\uE8EF"),
                    new Entry("Проводник", "explorer.exe", "\uE838"),
                    new Entry("Настройки", "ms-settings:", "\uE713"),
                    new Entry("Диспетчер задач", "taskmgr.exe", "\uE9D9"),
                }
            },
            new Mode("Закладки")
            {
                Items =
                {
                    new Entry("YouTube", "https://www.youtube.com", "\uE774"),
                    new Entry("GitHub", "https://github.com", "\uE774"),
                    new Entry("Gmail", "https://mail.google.com", "\uE715"),
                    new Entry("Telegram", "https://web.telegram.org", "\uE774"),
                }
            }
        }
    };
}

public sealed record ThemeColors(string? Background = null, string? Accent = null);

// =========================================================================
//  Хранение настроек: %AppData%\RadialMenu\config.json
// =========================================================================

public static class ConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static AppConfig? _current;

    /// <summary>Срабатывает, когда настройки изменились и меню нужно перерисовать.</summary>
    public static event Action? Changed;

    public static AppConfig Current
    {
        get
        {
            if (_current == null) Load();
            return _current!;
        }
    }

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "RadialMenu",
        "config.json");

    public static void Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(FilePath));
                if (cfg != null && cfg.Modes.Count > 0)
                {
                    cfg.ActiveMode = Math.Clamp(cfg.ActiveMode, 0, cfg.Modes.Count - 1);
                    if (!Enum.IsDefined(cfg.MenuStyle)) cfg.MenuStyle = MenuStyle.Classic;
                    cfg.WidgetPlacements ??= new();
                    cfg.WidgetsWithoutDetail ??= new();
                    cfg.CompactDesktopWidgets ??= new();
                    _current = cfg;
                    return;
                }
            }
        }
        catch
        {
            // файл повреждён: начнём с настроек по умолчанию
        }

        _current = AppConfig.CreateDefault();
        Save();
    }

    public static void Save()
    {
        var cfg = _current;
        if (cfg == null) return;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(cfg, JsonOptions));
        }
        catch
        {
            // не удалось сохранить: не страшно, продолжаем работать
        }
    }

    /// <summary>Сохранить и сообщить всем окнам, что надо обновиться.</summary>
    public static void Commit()
    {
        Save();
        Changed?.Invoke();
    }
}

// =========================================================================
//  Значки: из системного шрифта или из самого файла (exe, папка, ярлык)
// =========================================================================

public static class IconService
{
    public static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    private static readonly Dictionary<string, ImageSource> Cache = new();
    private static readonly Dictionary<string, ImageSource?> NativeIcons = new(StringComparer.OrdinalIgnoreCase);

    public static ImageSource Get(Entry entry, Color? glyphColor = null)
    {
        if (GetNative(entry.Target) is { } native) return native;
        string key = entry.Glyph + "|" + entry.Target + "|" + glyphColor;
        if (Cache.TryGetValue(key, out var hit)) return hit;

        var image = GlyphImage(string.IsNullOrEmpty(entry.Glyph) ? "\uE774" : entry.Glyph, glyphColor);
        Cache[key] = image;
        return image;
    }

    internal static ImageSource? GetNative(string target)
    {
        if (NativeIcons.TryGetValue(target, out var cached)) return cached;
        ImageSource? icon = null;
        try
        {
            string? appId = WindowsAppIdentity.AppIdForTarget(target, includeActions: true);
            string? file = WindowsAppIdentity.FileForTarget(target);
            icon = (appId == null ? null : PackagedIcon(appId)) ?? (file == null ? null : FileIcon(file));
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException or COMException)
        {
            // Некоторые ярлыки и хранилища пакетов недоступны текущему пользователю.
        }
        NativeIcons[target] = icon;
        return icon;
    }

    // ---- значок из шрифта ----
    private static ImageSource GlyphImage(string glyph, Color? glyphColor)
    {
        const double box = 64;
        var typeface = new Typeface(IconFont, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var text = new FormattedText(
            glyph, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            typeface, box * 0.8, Brushes.White, 1.0);

        var geometry = text.BuildGeometry(new Point(0, 0));
        var bounds = geometry.Bounds;

        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(
            Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, box, box))));

        if (!bounds.IsEmpty)
        {
            var inner = new DrawingGroup
            {
                Transform = new TranslateTransform(
                    box / 2 - (bounds.X + bounds.Width / 2),
                    box / 2 - (bounds.Y + bounds.Height / 2))
            };
            inner.Children.Add(new GeometryDrawing(
                glyphColor is { } color ? new SolidColorBrush(color) : Brushes.White, null, geometry));
            group.Children.Add(inner);
        }

        var image = new DrawingImage(group);
        image.Freeze();
        return image;
    }

    // ---- значок файла, папки или ярлыка через Windows ----
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(
        string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    [DllImport("shell32.dll", EntryPoint = "SHGetFileInfoW")]
    private static extern IntPtr SHGetPidlInfo(
        nint pidl, uint attributes, ref SHFILEINFO info, uint size, uint flags);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHParseDisplayName(string name, nint bindContext, out nint pidl, uint mask, out uint attributes);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private const uint SHGFI_ICON = 0x100;
    private const uint SHGFI_LARGEICON = 0x0;

    private static ImageSource? FileIcon(string path)
    {
        var info = new SHFILEINFO();
        SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_ICON | SHGFI_LARGEICON);
        return TakeIcon(info.hIcon);
    }

    private static ImageSource? PackagedIcon(string appId)
    {
        if (SHParseDisplayName(RunningApp.PackagedPrefix + appId, 0, out var pidl, 0, out _) < 0 || pidl == 0)
            return null;
        try
        {
            var info = new SHFILEINFO();
            const uint shgfiPidl = 0x8;
            SHGetPidlInfo(pidl, 0, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_ICON | shgfiPidl);
            return TakeIcon(info.hIcon);
        }
        finally { Marshal.FreeCoTaskMem(pidl); }
    }

    private static ImageSource? TakeIcon(nint icon)
    {
        if (icon == 0) return null;
        try
        {
            var source = Imaging.CreateBitmapSourceFromHIcon(
                icon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        finally
        {
            DestroyIcon(icon);
        }
    }
}

// =========================================================================
//  Запуск пункта
// =========================================================================

public static class Launcher
{
    public static void Run(Entry entry)
    {
        try
        {
            if (entry.Target.StartsWith(RunningApp.PackagedPrefix, StringComparison.OrdinalIgnoreCase))
            {
                var shell = new ProcessStartInfo("explorer.exe");
                shell.ArgumentList.Add(entry.Target);
                Process.Start(shell);
            }
            else
            {
                Process.Start(new ProcessStartInfo(entry.Target) { UseShellExecute = true });
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Не удалось открыть «" + entry.Name + "»");
        }
    }
}
