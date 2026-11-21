using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32.SafeHandles;

namespace RadialMenu1;

public partial class RunningAppsWindow : Window
{
    private RunningApp[] _apps = [];
    private bool _loadedApps;

    internal RunningApp? SelectedApp { get; private set; }

    public RunningAppsWindow() : this(false) { }

    internal RunningAppsWindow(bool installed)
    {
        _installed = installed;
        InitializeComponent();
        SettingsTheme.Apply(Resources, MenuPalette.From(ConfigStore.Current));
        if (installed)
        {
            Heading.Text = "Установленные приложения";
            AutomationProperties.SetName(AppList, Heading.Text);
            ShowBackground.Visibility = Visibility.Collapsed;
        }
        Loaded += async (s, e) =>
        {
            SearchBox.Focus();
            Status.Text = installed ? "Загружаю приложения из меню «Пуск»…" : "Ищу запущенные приложения…";
            try
            {
                _apps = installed ? await InstalledApps.ReadAsync() : await Task.Run(RunningApp.Read);
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or COMException or
                Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or InvalidOperationException)
            {
                Status.Text = installed ? "Не удалось прочитать список приложений из меню «Пуск»."
                    : "Не удалось получить список запущенных процессов.";
                return;
            }
            _loadedApps = true;
            UpdateList();
        };
    }

    private readonly bool _installed;

    private void FilterChanged(object sender, RoutedEventArgs e) => UpdateList();

    private void UpdateList()
    {
        if (!_loadedApps) return;
        string query = SearchBox.Text.Trim();
        var matches = _apps.Where(app =>
            (app.HasWindow || ShowBackground.IsChecked == true || query.Length > 0) &&
            (app.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
             app.Details.Contains(query, StringComparison.OrdinalIgnoreCase))).ToArray();
        AppList.ItemsSource = matches;
        AppList.SelectedIndex = matches.Length > 0 ? 0 : -1;
        Status.Text = matches.Length > 0
            ? "Введи имя приложения, например Telegram."
            : _installed ? "Совпадений нет. Если программы нет в меню «Пуск», выбери её файл или ярлык."
                : "Совпадений нет. Открой приложение и снова открой этот список.";
    }

    private void SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        AddButton.IsEnabled = AppList.SelectedItem != null;

    private void AppList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source &&
            ItemsControl.ContainerFromElement(AppList, source) is ListBoxItem) Add_Click(sender, e);
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        if (AppList.SelectedItem is not RunningApp app) return;
        SelectedApp = app;
        DialogResult = true;
    }
}

internal sealed record RunningApp(string Name, string Details, string Target, bool HasWindow, bool IsPackaged)
{
    public ImageSource Icon => IconService.GetNative(Target) ?? IconService.Get(new Entry(Name, Details, "\uE737"));

    internal const string PackagedPrefix = @"shell:AppsFolder\";
    private const uint QueryLimitedInformation = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, int flags, StringBuilder path, ref int length);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetApplicationUserModelId(SafeProcessHandle process, ref uint length, StringBuilder appId);

    internal static string? ReadAppId(int processId)
    {
        using var handle = OpenProcess(QueryLimitedInformation, false, processId);
        if (handle.IsInvalid) return null;
        var appId = new StringBuilder(256);
        uint length = (uint)appId.Capacity;
        return GetApplicationUserModelId(handle, ref length, appId) == 0 ? appId.ToString() : null;
    }

    internal static string? ReadExecutable(int processId)
    {
        using var handle = OpenProcess(QueryLimitedInformation, false, processId);
        if (handle.IsInvalid) return null;
        var path = new StringBuilder(32768);
        int length = path.Capacity;
        return QueryFullProcessImageName(handle, 0, path, ref length) ? path.ToString() : null;
    }

    public static RunningApp[] Read()
    {
        var apps = new List<RunningApp>();
        using var current = Process.GetCurrentProcess();
        int session = current.SessionId;
        var path = new StringBuilder(32768);
        var appId = new StringBuilder(256);
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.Id == Environment.ProcessId || process.SessionId != session) continue;
                    using var handle = OpenProcess(QueryLimitedInformation, false, process.Id);
                    if (handle.IsInvalid) continue;

                    path.Clear();
                    int length = path.Capacity;
                    if (!QueryFullProcessImageName(handle, 0, path, ref length)) continue;
                    string file = path.ToString();
                    bool hasWindow = process.MainWindowHandle != 0;

                    appId.Clear();
                    uint idLength = (uint)appId.Capacity;
                    bool packaged = GetApplicationUserModelId(handle, ref idLength, appId) == 0;
                    string? description = ReadDescription(file);
                    string name = string.IsNullOrWhiteSpace(description) ? process.ProcessName : description.Trim();
                    string target = packaged ? PackagedPrefix + appId : file;
                    apps.Add(new RunningApp(name, file, target, hasWindow, packaged));
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException
                                               or IOException or UnauthorizedAccessException)
                {
                    // Процесс мог завершиться или запретить чтение своих данных.
                }
            }
        }

        return apps.OrderByDescending(app => app.HasWindow)
            .ThenBy(app => app.Name, StringComparer.CurrentCultureIgnoreCase)
            .DistinctBy(app => app.Target, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string? ReadDescription(string path)
    {
        try { return FileVersionInfo.GetVersionInfo(path).FileDescription; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
}
