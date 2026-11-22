using System.IO;
using System.Globalization;
using System.Security;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using ShapePath = System.Windows.Shapes.Path;

namespace RadialMenu1;

public partial class SettingsWindow : Window
{
    private const int MaxItems = 10;
    private const string ModeDragFormat = "RadialMenu.Mode";
    private const string StartupDescription = "RadialMenu будет готов к работе после входа в Windows.";
    private bool _updatingColor;
    private readonly Func<MenuHotkey, bool> _registerHotkey;
    private bool _hotkeyAvailable;
    private bool _recordingHotkey;
    private readonly DispatcherTimer _appearanceSave = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly CancellationTokenSource _serviceLifetime = new();
    private ReleaseUpdater.Release? _pendingUpdate;
    private bool _updateBusy;
    private Mode? _modeDragSource;
    private Entry? _editedSubmenu;
    private Point _modeDragStart;

    private static AppConfig Cfg => ConfigStore.Current;


    private Mode? RootMode => ModeList.SelectedItem as Mode;
    private Mode? SelMode => _editedSubmenu?.Submenu ?? RootMode;
    private Entry? SelEntry => EntryList.SelectedItem as Entry;
    internal bool IsRecordingHotkey => _recordingHotkey && IsActive;

    internal SettingsWindow(Func<MenuHotkey, bool> registerHotkey, bool hotkeyAvailable)
    {
        _registerHotkey = registerHotkey;
        _hotkeyAvailable = hotkeyAvailable;
        InitializeComponent();
        SettingsTheme.PreventMaximize(this);
        if (Cfg.AnimationsEnabled) WindowSurface.Opacity = 0;
        Loaded += (_, _) => UpdateTabIndicator(animate: false);
        _appearanceSave.Tick += (s, e) => SaveAppearance();
        Closed += (s, e) =>
        {
            if (_appearanceSave.IsEnabled) SaveAppearance();
            _serviceLifetime.Cancel();
            _serviceLifetime.Dispose();
        };

        // предпросмотр всегда показывает режим, выбранный в списке слева
        Preview.ModeSource = () => SelMode;
        Preview.EntryMoveRequested += MoveEntry;
        Preview.SetMenuScale(Cfg.MenuScalePercent);
        MenuScale.Minimum = AppConfig.MinMenuScale;
        MenuScale.Maximum = AppConfig.MaxMenuScale;
        MenuScale.TickFrequency = AppConfig.MenuScaleStep;
        MenuScale.SmallChange = AppConfig.MenuScaleStep;
        MenuScale.LargeChange = AppConfig.MenuScaleStep;
        MenuScale.Value = Cfg.MenuScalePercent;
        MenuScale.ValueChanged += MenuScale_ValueChanged;
        MenuTransparency.Value = Cfg.TransparencyPercent;
        MenuTransparency.ValueChanged += (s, e) =>
        {
            Cfg.TransparencyPercent = (int)e.NewValue;
            Preview.RefreshAppearance();
            QueueAppearanceSave();
        };
        AnimationsToggle.IsChecked = Cfg.AnimationsEnabled;
        AnimationsToggle.Checked += AnimationsToggle_Changed;
        AnimationsToggle.Unchecked += AnimationsToggle_Changed;
        ReleaseSelectionToggle.IsChecked = Cfg.SelectOnRelease;
        SubmenusToggle.IsChecked = Cfg.SubmenusEnabled;
        GamePauseToggle.IsChecked = Cfg.PauseInGames;
        RefreshGameList();

        UpdateHotkeyButton(hotkeyAvailable ? "" : "Сочетание недоступно. Выбери другое.");
        HotkeyButton.LostKeyboardFocus += (s, e) =>
        {
            if (_recordingHotkey) UpdateHotkeyButton();
        };
        Deactivated += (s, e) =>
        {
            if (_recordingHotkey) UpdateHotkeyButton();
        };

        ModeList.ItemsSource = Cfg.Modes;
        ModeList.SelectedIndex = Math.Clamp(Cfg.ActiveMode, 0, Cfg.Modes.Count - 1);

        ThemeList.ItemsSource = ThemeOptions();
        ColorTarget.SelectionChanged += (s, e) =>
        {
            if (ColorTarget.SelectedIndex < 0) ColorTarget.SelectedIndex = 0;
            else SynchronizeColorEditor();
        };
        ThemeColorWheel.SelectionChanged += ColorWheelChanged;
        ColorBrightness.ValueChanged += (s, e) => ColorWheelChanged();
        ThemeList.SelectedValue = Enum.IsDefined(Cfg.Theme) ? Cfg.Theme : MenuTheme.Violet;
        WindowsStartupStatus.Text = StartupDescription;
        try { WindowsStartupToggle.IsChecked = WindowsStartup.IsRegistered(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            WindowsStartupToggle.IsEnabled = false;
            WindowsStartupStatus.Text = "Не удалось прочитать настройку автозапуска Windows.";
        }
        UpdateStatus.Text = $"Установлена версия {ReleaseUpdater.DisplayVersion}.";
        ReportCard.Visibility = ReportDelivery.CanReport ? Visibility.Visible : Visibility.Collapsed;
        if (!ReportDelivery.CanSend) ReportButton.Content = "Открыть бота в Telegram";
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        if (WindowSurface.Opacity != 0) return;

        WindowSurface.Opacity = 1;
        if (!Cfg.AnimationsEnabled) return;

        var duration = TimeSpan.FromMilliseconds(240);
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        var fade = new DoubleAnimation(0, 1, duration) { EasingFunction = easing, FillBehavior = FillBehavior.Stop };
        var zoom = new DoubleAnimation(0.975, 1, duration) { EasingFunction = easing, FillBehavior = FillBehavior.Stop };
        WindowSurface.BeginAnimation(OpacityProperty, fade);
        WindowScale.BeginAnimation(ScaleTransform.ScaleXProperty, zoom);
        WindowScale.BeginAnimation(ScaleTransform.ScaleYProperty, zoom);
    }

    private void PreviewHeader_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 1 && e.LeftButton == MouseButtonState.Pressed)
            DragMove();
    }

    private void CloseSettings_Click(object sender, RoutedEventArgs e) => Close();

    private void SettingsTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded && e.Source == SettingsTabs)
            Dispatcher.InvokeAsync(() => UpdateTabIndicator(animate: true), DispatcherPriority.Loaded);
    }

    private void SettingsTabs_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (IsLoaded) UpdateTabIndicator(animate: false);
    }

    private void UpdateTabIndicator(bool animate)
    {
        if (!IsLoaded || SettingsTabs.SelectedItem is not TabItem tab || tab.ActualWidth <= 1 || tab.ActualHeight <= 1 ||
            SettingsTabs.Template.FindName("TabHeaderSurface", SettingsTabs) is not FrameworkElement header ||
            SettingsTabs.Template.FindName("TabIndicator", SettingsTabs) is not ShapePath indicator)
            return;

        var target = new Rect(tab.TranslatePoint(new Point(0.5, 0.5), header),
            new Size(tab.ActualWidth - 1, tab.ActualHeight - 1));
        if (indicator.Data is not RectangleGeometry geometry)
        {
            indicator.Data = new RectangleGeometry(target, 9, 9);
            return;
        }

        animate &= Cfg.AnimationsEnabled;
        if (animate && (Rect)geometry.GetAnimationBaseValue(RectangleGeometry.RectProperty) == target) return;

        var current = geometry.Rect;
        geometry.BeginAnimation(RectangleGeometry.RectProperty, null);
        geometry.Rect = target;
        if (!animate || Math.Abs(current.Y - target.Y) > 1 || current == target) return;

        // Move the leading edge first, then let the trailing edge catch up.
        bool movingRight = target.Left > current.Left;
        double left = current.Left + (target.Left - current.Left) * (movingRight ? 0.23 : 0.80);
        double right = current.Right + (target.Right - current.Right) * (movingRight ? 0.80 : 0.23);
        var stretch = new Rect(left, target.Y, Math.Max(1, right - left), target.Height);

        var slide = new RectAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
        slide.KeyFrames.Add(new DiscreteRectKeyFrame(current, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        slide.KeyFrames.Add(new SplineRectKeyFrame(stretch, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(125)),
            new KeySpline(0.24, 0.10, 0.42, 0.88)));
        slide.KeyFrames.Add(new SplineRectKeyFrame(target, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(300)),
            new KeySpline(0.18, 0.48, 0.36, 1)));
        geometry.BeginAnimation(RectangleGeometry.RectProperty, slide);
    }

    private void BehaviorToggle_Click(object sender, RoutedEventArgs e)
    {
        Cfg.SelectOnRelease = ReleaseSelectionToggle.IsChecked == true;
        Cfg.SubmenusEnabled = SubmenusToggle.IsChecked == true;
        Cfg.PauseInGames = GamePauseToggle.IsChecked == true;
        ConfigStore.Commit();
    }

    private void RefreshGameList() =>
        GameList.ItemsSource = (Cfg.GameExecutables ??= new())
            .Select(path => new GameOption(Path.GetFileNameWithoutExtension(path), path)).ToArray();

    private void AddRunningGame_Click(object sender, RoutedEventArgs e)
    {
        var picker = new RunningAppsWindow { Owner = this };
        if (picker.ShowDialog() == true && picker.SelectedApp is { } app) AddGame(app.Details);
    }

    private void AddGameFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Выберите EXE игры", Filter = "Программы (*.exe)|*.exe" };
        if (dialog.ShowDialog(this) == true) AddGame(dialog.FileName);
    }

    private void AddGame(string path)
    {
        if (!Cfg.GameExecutables.Contains(path, StringComparer.OrdinalIgnoreCase))
        {
            Cfg.GameExecutables.Add(path);
            ConfigStore.Save();
            RefreshGameList();
        }

        GameList.SelectedItem = GameList.Items.OfType<GameOption>()
            .FirstOrDefault(game => string.Equals(game.Path, path, StringComparison.OrdinalIgnoreCase));
    }

    private void GameList_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        RemoveGameButton.IsEnabled = GameList.SelectedItem is GameOption;

    private void RemoveGame_Click(object sender, RoutedEventArgs e)
    {
        if (GameList.SelectedItem is not GameOption game) return;
        Cfg.GameExecutables.Remove(game.Path);
        ConfigStore.Save();
        RefreshGameList();
    }

    private sealed record GameOption(string Name, string Path);

    private void WindowsStartupToggle_Click(object sender, RoutedEventArgs e)
    {
        bool enabled = WindowsStartupToggle.IsChecked == true;
        try
        {
            WindowsStartup.SetRegistered(enabled);
            WindowsStartupStatus.Text = StartupDescription;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or InvalidOperationException)
        {
            WindowsStartupToggle.IsChecked = !enabled;
            WindowsStartupStatus.Text = ex is InvalidOperationException ? ex.Message :
                "Не удалось изменить автозапуск. Проверь разрешения текущей учётной записи Windows.";
        }
    }

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e) => await CheckUpdatesAsync();

    internal async Task CheckUpdatesAsync()
    {
        SettingsTabs.SelectedItem = ServiceTab;
        if (_updateBusy) return;
        var cancellation = _serviceLifetime.Token;
        _updateBusy = true;
        CheckUpdateButton.IsEnabled = false;
        InstallUpdateButton.Visibility = Visibility.Collapsed;
        _pendingUpdate = null;
        UpdateStatus.Text = "Проверяю стабильные релизы…";
        try
        {
            _pendingUpdate = await ReleaseUpdater.CheckAsync(cancellation);
            if (cancellation.IsCancellationRequested) return;
            if (_pendingUpdate == null)
            {
                UpdateStatus.Text = $"Установлена актуальная версия {ReleaseUpdater.DisplayVersion}.";
                return;
            }
            string display = _pendingUpdate.DisplayVersion;
            UpdateStatus.Text = $"Доступна версия {display}. Сейчас установлена {ReleaseUpdater.DisplayVersion}.";
            InstallUpdateButton.Content = $"Установить {display}";
            InstallUpdateButton.IsEnabled = true;
            InstallUpdateButton.Visibility = Visibility.Visible;
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or System.Text.Json.JsonException or
                                       InvalidOperationException or KeyNotFoundException or OperationCanceledException or FormatException)
        {
            if (!cancellation.IsCancellationRequested) UpdateStatus.Text = "Проверка обновлений: " + ex.Message;
        }
        finally
        {
            _updateBusy = false;
            if (!cancellation.IsCancellationRequested) CheckUpdateButton.IsEnabled = true;
        }
    }

    private async void InstallUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_updateBusy || _pendingUpdate is not { } release) return;
        var cancellation = _serviceLifetime.Token;
        _updateBusy = true;
        CheckUpdateButton.IsEnabled = InstallUpdateButton.IsEnabled = false;
        string status = $"Обновляем RadialMenu до {release.DisplayVersion}";
        UpdateStatus.Text = status + " · 0%";
        UpdateProgress.Value = 0;
        UpdateProgress.Visibility = Visibility.Visible;
        var progress = new Progress<int>(percent =>
        {
            if (cancellation.IsCancellationRequested || UpdateProgress.Visibility != Visibility.Visible ||
                percent <= UpdateProgress.Value) return;
            UpdateProgress.Value = percent;
            UpdateStatus.Text = $"{status} · {percent}%";
        });
        try
        {
            using var installer = await ReleaseUpdater.DownloadAsync(release, progress, cancellation);
            UpdateProgress.Value = 100;
            UpdateStatus.Text = status + " · установка…";
            await Dispatcher.Yield(DispatcherPriority.Background);
            cancellation.ThrowIfCancellationRequested();
            installer.Start(release.Version, MenuPalette.From(Cfg), Cfg.AnimationsEnabled);
            Application.Current.Shutdown();
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or IOException or InvalidDataException or
                                       UnauthorizedAccessException or InvalidOperationException or OperationCanceledException or
                                       System.ComponentModel.Win32Exception or SecurityException)
        {
            if (!cancellation.IsCancellationRequested)
            {
                UpdateProgress.Visibility = Visibility.Collapsed;
                UpdateStatus.Text = "Установка: " + ex.Message;
                CheckUpdateButton.IsEnabled = InstallUpdateButton.IsEnabled = true;
            }
        }
        finally { _updateBusy = false; }
    }

    private void BugReport_Click(object sender, RoutedEventArgs e) => ShowBugReport();

    internal void ShowBugReport()
    {
        if (ReportDelivery.CanSend)
        {
            BugReportWindow.ShowFor(this);
            return;
        }

        try { ReportDelivery.OpenBot(); }
        catch (System.ComponentModel.Win32Exception)
        {
            InputDialog.Inform(this, "Telegram", "Не удалось открыть ссылку на бота.");
        }
    }

    internal void SynchronizeTheme() => ThemeList.SelectedValue = Cfg.Theme;

    private void HotkeyButton_Click(object sender, RoutedEventArgs e)
    {
        if (_recordingHotkey)
        {
            UpdateHotkeyButton();
            return;
        }

        HotkeyButton.Focus();
        _recordingHotkey = true;
        HotkeyButton.Content = "Нажми клавишу…";
        HotkeyStatus.Text = "Клавиша, Mouse4 или Mouse5. Esc — отмена.";
    }

    private void UpdateHotkeyButton(string? status = null)
    {
        _recordingHotkey = false;
        HotkeyButton.Content = Cfg.Hotkey.CanRegister ? Cfg.Hotkey.ToString() : "Назначить";
        HotkeyStatus.Text = status ?? (_hotkeyAvailable ? "" : "Сочетание недоступно. Выбери другое.");
    }

    internal bool UseCurrentHotkeyWhileRecording()
    {
        if (!_recordingHotkey || !IsActive) return false;
        UpdateHotkeyButton("Это сочетание уже назначено.");
        return true;
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (!_recordingHotkey) return;

        e.Handled = true;
        if (e.IsRepeat) return;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        RecordHotkey(new MenuHotkey(key, Keyboard.Modifiers));
    }

    protected override void OnPreviewMouseDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseDown(e);
        if (!_recordingHotkey) return;
        int button = e.ChangedButton switch { MouseButton.XButton1 => 4, MouseButton.XButton2 => 5, _ => 0 };
        if (button == 0) return;
        e.Handled = true;
        RecordHotkey(new MenuHotkey(Key.None, Keyboard.Modifiers, button));
    }

    private void RecordHotkey(MenuHotkey hotkey)
    {
        if (hotkey.Key == Key.Escape && hotkey.Modifiers == ModifierKeys.None)
        {
            UpdateHotkeyButton();
            return;
        }

        if (hotkey.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or
            Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return;

        if (!_registerHotkey(hotkey))
        {
            HotkeyStatus.Text = "Сочетание занято или недоступно. Попробуй другое.";
            return;
        }

        _hotkeyAvailable = true;
        Cfg.Hotkey = hotkey;
        ConfigStore.Save();
        UpdateHotkeyButton("Горячая клавиша сохранена.");
    }

    private void MenuScale_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (Cfg.MenuScalePercent == (int)e.NewValue) return;
        Cfg.MenuScalePercent = (int)e.NewValue;
        Preview.SetMenuScale(Cfg.MenuScalePercent);
        QueueAppearanceSave();
    }

    private void AnimationsToggle_Changed(object sender, RoutedEventArgs e)
    {
        Cfg.AnimationsEnabled = AnimationsToggle.IsChecked == true;
        if (!Cfg.AnimationsEnabled) UpdateTabIndicator(animate: false);
        Preview.RefreshAppearance();
        QueueAppearanceSave();
    }

    private void QueueAppearanceSave()
    {
        _appearanceSave.Stop();
        _appearanceSave.Start();
    }

    private void SaveAppearance()
    {
        _appearanceSave.Stop();
        ConfigStore.Save();
    }

    private static ThemeOption[] ThemeOptions() =>
    [
        CreateThemeOption(MenuTheme.Violet),
        CreateThemeOption(MenuTheme.Dark),
        CreateThemeOption(MenuTheme.Light),
        CreateThemeOption(MenuTheme.Ocean),
        CreateThemeOption(MenuTheme.Forest),
        CreateThemeOption(MenuTheme.Glass),
        CreateThemeOption(MenuTheme.Custom)
    ];

    private static ThemeOption CreateThemeOption(MenuTheme theme)
    {
        var palette = MenuPalette.ForTheme(Cfg, theme);
        return new(theme, MenuPalette.DisplayName(theme), new SolidColorBrush(palette.Disc), new SolidColorBrush(palette.Accent));
    }

    private void ThemeList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ThemeList.SelectedValue is not MenuTheme theme) return;

        bool changed = Cfg.Theme != theme;
        Cfg.Theme = theme;
        ApplyTheme();
        SynchronizeColorEditor();
        Preview.RefreshAppearance();
        if (changed) ConfigStore.Commit();
    }

    private bool EditingBackground => ColorTarget.SelectedIndex == 0;

    private void SynchronizeColorEditor()
    {
        var palette = MenuPalette.From(Cfg);
        var colors = Cfg.ThemeOverrides?.GetValueOrDefault(Cfg.Theme);
        SetColorEditor(EditingBackground ? palette.Disc : palette.Accent);
        ResetColorButton.IsEnabled = MenuPalette.TryParseColor(EditingBackground ? colors?.Background : colors?.Accent, out _);
    }

    private void UpdateThemeColor(string? hex)
    {
        var overrides = Cfg.ThemeOverrides ??= new();
        var colors = overrides.GetValueOrDefault(Cfg.Theme) ?? new ThemeColors();
        var updated = EditingBackground ? colors with { Background = hex } : colors with { Accent = hex };
        if (colors == updated) return;

        if (updated is { Background: null, Accent: null }) overrides.Remove(Cfg.Theme);
        else overrides[Cfg.Theme] = updated;

        if (ThemeList.SelectedItem is ThemeOption option)
        {
            var palette = MenuPalette.From(Cfg);
            option.Surface.Color = palette.Disc;
            option.Accent.Color = palette.Accent;
        }
        ResetColorButton.IsEnabled = hex != null;
        ApplyTheme();
        Preview.RefreshAppearance();
        QueueAppearanceSave();
    }

    private void ResetColor_Click(object sender, RoutedEventArgs e)
    {
        UpdateThemeColor(null);
        SynchronizeColorEditor();
    }

    private void ColorInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_updatingColor && ColorSwatch != null && ColorHint != null &&
            MenuPalette.TryParseColor(ColorInput.Text, out var color))
        {
            SetColorEditor(color, updateText: false);
            UpdateThemeColor(FormatColor(color));
        }
    }

    private void ColorInput_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (MenuPalette.TryParseColor(ColorInput.Text, out var color)) ColorInput.Text = FormatColor(color);
        else ColorHint.Text = "Введи HEX-код, например #34D399.";
    }

    private void ColorWheelChanged()
    {
        if (_updatingColor) return;
        var color = ThemeColorWheel.ColorAtBrightness(ColorBrightness.Value / 100);
        SetColorEditor(color, updateControls: false);
        UpdateThemeColor(FormatColor(color));
    }

    private void SetColorEditor(Color color, bool updateText = true, bool updateControls = true)
    {
        _updatingColor = true;
        if (updateControls)
        {
            ThemeColorWheel.SelectColor(color);
            ColorBrightness.Value = Math.Max(color.R, Math.Max(color.G, color.B)) / 255.0 * 100;
        }
        ColorSwatch.Background = new SolidColorBrush(color);
        ColorHint.Text = "";
        if (updateText) ColorInput.Text = FormatColor(color);
        _updatingColor = false;
    }

    private static string FormatColor(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    private void ApplyTheme() => SettingsTheme.Apply(Resources, MenuPalette.From(Cfg));

    private sealed record ThemeOption(MenuTheme Value, string Name, SolidColorBrush Surface, SolidColorBrush Accent);

    private void ModeList_SelectionChanged(object sender, SelectionChangedEventArgs e) => EditSubmenu(null);

    private void EditSubmenu(Entry? entry)
    {
        _editedSubmenu = entry;
        BackToModeButton.Visibility = entry == null ? Visibility.Collapsed : Visibility.Visible;
        EntriesHeading.Text = entry?.Name ?? "В этом режиме";
        EntryList.ItemsSource = SelMode?.Items;
        Preview.Rebuild(true);
    }

    private void EntryList_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        EditSubmenuButton.Visibility = SelEntry?.Submenu != null ? Visibility.Visible : Visibility.Collapsed;

    private void EditSubmenu_Click(object sender, RoutedEventArgs e)
    {
        if (SelEntry is { Submenu: not null } entry) EditSubmenu(entry);
    }

    private void BackToMode_Click(object sender, RoutedEventArgs e)
    {
        var entry = _editedSubmenu;
        EditSubmenu(null);
        EntryList.SelectedItem = entry;
    }

    private void ModeMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is ContextMenu { PlacementTarget: ListBoxItem { Content: Mode mode } })
            ModeList.SelectedItem = mode;
    }

    private void ActivateMode_Click(object sender, RoutedEventArgs e)
    {
        if (ModeList.SelectedIndex < 0) return;
        Cfg.ActiveMode = ModeList.SelectedIndex;
        ConfigStore.Commit();
    }

    // =====================================================================
    //  Режимы
    // =====================================================================
    private void AddMode_Click(object sender, RoutedEventArgs e)
    {
        string? name = InputDialog.Ask(this, "Новый режим", "Название режима:", "Новый режим");
        if (name == null) return;

        Cfg.Modes.Add(new Mode(name));
        ModeList.SelectedIndex = Cfg.Modes.Count - 1;
        ConfigStore.Commit();
    }

    private void RenameMode_Click(object sender, RoutedEventArgs e)
    {
        if (RootMode is not { } mode) return;

        string? name = InputDialog.Ask(this, "Переименовать режим", "Название режима:", mode.Name);
        if (name == null) return;

        mode.Name = name;
        ConfigStore.Commit();
    }

    private void DeleteMode_Click(object sender, RoutedEventArgs e)
    {
        if (RootMode is not { } mode) return;

        if (Cfg.Modes.Count <= 1)
        {
            InputDialog.Inform(this, "Удаление режима", "Нужен хотя бы один режим.");
            return;
        }

        if (!InputDialog.Confirm(this, "Удаление режима",
                "Удалить режим «" + mode.Name + "» вместе со всеми его пунктами?")) return;

        var active = Cfg.Modes[Math.Clamp(Cfg.ActiveMode, 0, Cfg.Modes.Count - 1)];
        int index = ModeList.SelectedIndex;
        Cfg.Modes.Remove(mode);

        int activeIndex = Cfg.Modes.IndexOf(active);
        Cfg.ActiveMode = activeIndex >= 0 ? activeIndex : 0;

        ModeList.SelectedIndex = Math.Min(index, Cfg.Modes.Count - 1);
        ConfigStore.Commit();
    }

    private Mode? ModeUnderPointer(RoutedEventArgs e) =>
        e.OriginalSource is DependencyObject source &&
        ItemsControl.ContainerFromElement(ModeList, source) is ListBoxItem { Content: Mode mode }
            ? mode : null;

    private void ModeList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _modeDragSource = ModeUnderPointer(e);
        _modeDragStart = e.GetPosition(ModeList);
    }

    private void ModeList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            _modeDragSource = null;
            return;
        }

        if (_modeDragSource is not { } mode) return;
        var offset = e.GetPosition(ModeList) - _modeDragStart;
        if (Math.Abs(offset.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(offset.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        _modeDragSource = null;
        DragDrop.DoDragDrop(ModeList, new DataObject(ModeDragFormat, mode), DragDropEffects.Move);
    }

    private void ModeList_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(ModeDragFormat, false) && ModeUnderPointer(e) != null
            ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void ModeList_Drop(object sender, DragEventArgs e)
    {
        e.Effects = DragDropEffects.None;
        e.Handled = true;
        if (e.Data.GetData(ModeDragFormat, false) is not Mode source || ModeUnderPointer(e) is not { } target) return;
        int i = Cfg.Modes.IndexOf(source);
        int j = Cfg.Modes.IndexOf(target);
        if (i < 0 || j < 0 || i == j) return;

        var active = Cfg.Modes[Math.Clamp(Cfg.ActiveMode, 0, Cfg.Modes.Count - 1)];
        Cfg.Modes.Move(i, j);
        Cfg.ActiveMode = Cfg.Modes.IndexOf(active);

        ModeList.SelectedItem = source;
        e.Effects = DragDropEffects.Move;
        ConfigStore.Commit();
    }

    // =====================================================================
    //  Пункты
    // =====================================================================
    private bool HasRoom(Mode mode)
    {
        if (mode.Items.Count < MaxItems) return true;

        InputDialog.Inform(this, "Пункты режима", "В одном режиме максимум " + MaxItems + " пунктов.");
        return false;
    }

    private void OtherSources_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { ContextMenu: { } menu } button) return;
        AddSubmenuItem.Visibility = Cfg.SubmenusEnabled && _editedSubmenu == null
            ? Visibility.Visible : Visibility.Collapsed;
        menu.PlacementTarget = button;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void AddInstalledApp_Click(object sender, RoutedEventArgs e) => AddFromAppPicker(installed: true);

    private void AddRunningApp_Click(object sender, RoutedEventArgs e) => AddFromAppPicker(installed: false);

    private void AddFromAppPicker(bool installed)
    {
        if (SelMode is not { } mode || !HasRoom(mode)) return;
        var picker = new RunningAppsWindow(installed) { Owner = this };
        if (picker.ShowDialog() == true && picker.SelectedApp is { } app)
            AddEntry(mode, new Entry(app.Name, app.Target, app.IsPackaged ? "\uE737" : ""));
    }

    private void AddSubmenu_Click(object sender, RoutedEventArgs e)
    {
        if (!Cfg.SubmenusEnabled || _editedSubmenu != null || RootMode is not { } mode || !HasRoom(mode)) return;
        string? name = InputDialog.Ask(this, "Новое подменю", "Название подменю:", "Инструменты");
        if (name == null) return;
        var entry = new Entry(name, "", "\uE8B7") { Submenu = new Mode(name) };
        AddEntry(mode, entry);
        EditSubmenu(entry);
    }

    private void AddApp_Click(object sender, RoutedEventArgs e)
    {
        if (SelMode is not { } mode || !HasRoom(mode)) return;

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Выберите программу, ярлык или файл",
            Filter = "Программы и ярлыки (*.exe;*.lnk)|*.exe;*.lnk|Все файлы (*.*)|*.*"
        };
        if (dialog.ShowDialog(this) != true) return;

        string name = Path.GetFileNameWithoutExtension(dialog.FileName);
        AddEntry(mode, new Entry(name, dialog.FileName));
    }

    private void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        if (SelMode is not { } mode || !HasRoom(mode)) return;

        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Выберите папку" };
        if (dialog.ShowDialog(this) != true) return;

        string folder = dialog.FolderName;
        string name = Path.GetFileName(folder.TrimEnd('\\', '/'));
        if (string.IsNullOrWhiteSpace(name)) name = folder;
        AddEntry(mode, new Entry(name, folder));
    }

    private void AddSite_Click(object sender, RoutedEventArgs e)
    {
        if (SelMode is not { } mode || !HasRoom(mode)) return;

        string? url = InputDialog.Ask(this, "Добавить сайт", "Адрес сайта:", "https://");
        if (url == null) return;
        if (!url.Contains("://")) url = "https://" + url;

        string host = Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Host.Length > 0
            ? uri.Host.Replace("www.", "")
            : url;

        string? name = InputDialog.Ask(this, "Добавить сайт", "Название:", host);
        if (name == null) return;

        AddEntry(mode, new Entry(name, url, "\uE774"));
    }

    private void AddEntry(Mode mode, Entry entry)
    {
        mode.Items.Add(entry);
        EntryList.SelectedIndex = mode.Items.Count - 1;
        EntryList.ScrollIntoView(entry);
        ConfigStore.Commit();
    }

    private void RenameEntry_Click(object sender, RoutedEventArgs e)
    {
        if (SelEntry is not { } entry) return;

        string? name = InputDialog.Ask(this, "Переименовать пункт", "Название:", entry.Name);
        if (name == null) return;

        entry.Name = name;
        if (entry.Submenu is { } submenu) submenu.Name = name;
        ConfigStore.Commit();
    }

    private void DeleteEntry_Click(object sender, RoutedEventArgs e)
    {
        if (SelMode is not { } mode || SelEntry is not { } entry) return;
        if (entry.Submenu is { Items.Count: > 0 } &&
            !InputDialog.Confirm(this, "Удаление подменю", "Удалить «" + entry.Name + "» вместе с его пунктами?")) return;

        int index = EntryList.SelectedIndex;
        mode.Items.Remove(entry);
        if (mode.Items.Count > 0) EntryList.SelectedIndex = Math.Min(index, mode.Items.Count - 1);
        ConfigStore.Commit();
    }

    private void MoveEntry(Mode mode, int from, int to)
    {
        if (mode != SelMode) return;
        mode.Items.Move(from, to);
        EntryList.SelectedIndex = to;
        ConfigStore.Commit();
    }

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        Application.Current.Shutdown();
    }
}

// =========================================================================
//  Маленькое окошко «введите текст»
// =========================================================================
internal sealed class InputDialog : Window
{
    private readonly TextBox? _box;

    private InputDialog(Window owner, string title, string label, string? initial, string acceptLabel, bool canCancel)
    {
        Owner = owner;
        Title = title;
        Width = 400;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontFamily = owner.FontFamily;
        FontSize = owner.FontSize;
        Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/RadialMenu1;component/SettingsStyles.xaml", UriKind.Relative)
        });
        SettingsTheme.Apply(Resources, MenuPalette.From(ConfigStore.Current));
        SetResourceReference(BackgroundProperty, "ThemeWindow");
        SetResourceReference(ForegroundProperty, "ThemeText");

        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap });
        if (initial != null)
        {
            _box = new TextBox { Text = initial, Margin = new Thickness(0, 8, 0, 0) };
            System.Windows.Automation.AutomationProperties.SetName(_box, label);
            panel.Children.Add(_box);
        }

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0)
        };
        var ok = new Button
        {
            Content = acceptLabel,
            IsDefault = initial != null || !canCancel,
            MinWidth = 80
        };
        ok.SetResourceReference(StyleProperty, "PrimaryButton");
        ok.Click += (s, e) => DialogResult = true;
        row.Children.Add(ok);

        Button? cancel = null;
        if (canCancel)
        {
            cancel = new Button
            {
                Content = "Отмена", IsCancel = true, MinWidth = 80, Margin = new Thickness(8, 0, 0, 0)
            };
            row.Children.Add(cancel);
        }
        panel.Children.Add(row);

        Content = panel;
        Loaded += (s, e) =>
        {
            if (_box != null)
            {
                _box.Focus();
                _box.SelectAll();
            }
            else (cancel ?? ok).Focus();
        };
    }

    public static string? Ask(Window owner, string title, string label, string initial)
    {
        var dialog = new InputDialog(owner, title, label, initial, "ОК", true);
        if (dialog.ShowDialog() != true) return null;

        string text = dialog._box!.Text.Trim();
        return text.Length == 0 ? null : text;
    }

    public static bool Confirm(Window owner, string title, string message) =>
        new InputDialog(owner, title, message, null, "Удалить", true).ShowDialog() == true;

    public static void Inform(Window owner, string title, string message) =>
        new InputDialog(owner, title, message, null, "ОК", false).ShowDialog();
}

internal static class SettingsTheme
{
    public static void PreventMaximize(Window window)
    {
        var workArea = SystemParameters.WorkArea;
        window.Width = Math.Min(window.Width, workArea.Width);
        window.Height = Math.Min(window.Height, workArea.Height);
        window.StateChanged += (s, e) =>
        {
            if (window.WindowState == WindowState.Maximized) window.WindowState = WindowState.Normal;
        };
    }

    public static void Apply(ResourceDictionary resources, MenuPalette palette)
    {
        resources["ThemeWindow"] = new SolidColorBrush(palette.Center);
        resources["ThemePanel"] = new SolidColorBrush(palette.Panel);
        resources["ThemeControl"] = new SolidColorBrush(palette.Control);
        resources["ThemeText"] = new SolidColorBrush(palette.Text);
        resources["ThemeMuted"] = new SolidColorBrush(palette.Muted);
        resources["ThemeBorder"] = new SolidColorBrush(palette.ControlBorder);
        resources["ThemeHover"] = new SolidColorBrush(palette.Hover);
        resources["ThemeSelected"] = new SolidColorBrush(palette.Selected);
        resources["ThemeFocus"] = new SolidColorBrush(palette.Focus);
        resources["ThemeAccent"] = new SolidColorBrush(palette.Accent);
        resources["ThemeOnAccent"] = new SolidColorBrush(palette.AccentButtonText);
        resources["ThemeAccentText"] = new SolidColorBrush(palette.AccentText);
    }
}

public sealed class EntryIconConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
        values[0] is Entry entry && values[1] is SolidColorBrush brush
            ? IconService.Get(entry, brush.Color)
            : DependencyProperty.UnsetValue;

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
