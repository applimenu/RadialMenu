using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;

namespace RadialMenu1;

internal static class TelegramWebTabs
{
    private const string TelegramHost = "web.telegram.org";
    // Chromium's installed app IDs for the Telegram root, Web A, Web K and Web Z manifest URLs.
    private static readonly string[] TelegramAppNames =
    [
        "_crx_nigookeodlehlnjcpdfifmophdcbjoma",
        "_crx_ibblmnobmgdmpoeblocemifbpglakpoi",
        "_crx_majiogicmcnmdhhlgmkahaleckhjbmlk",
        "_crx_aeejceomnhmjmeacdckmmopfaigimnkl",
    ];
    private static readonly HashSet<string> ChromiumBrowsers = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome", "msedge", "brave", "opera", "vivaldi", "browser", "chromium",
    };
    private static readonly Condition AddressBox = new AndCondition(
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit),
        new OrCondition(
            new PropertyCondition(AutomationElement.ClassNameProperty, "OmniboxViewViews"),
            new PropertyCondition(AutomationElement.AutomationIdProperty, "view_1012")));
    private static readonly Condition TabStrip = new OrCondition(
        new PropertyCondition(AutomationElement.ClassNameProperty, "TabStrip"),
        new PropertyCondition(AutomationElement.ClassNameProperty, "TabContainer"),
        new PropertyCondition(AutomationElement.AutomationIdProperty, "view_21"));
    private static readonly Condition TabItems = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem);

    public static bool IsWebLink(string target) =>
        Uri.TryCreate(target, UriKind.Absolute, out var uri) && IsTelegramClient(uri);

    public static string? InstalledAppId(string target)
    {
        string? appId = WindowsAppIdentity.AppIdForTarget(target);
        if (appId == null && target.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) &&
            WindowsAppIdentity.FileForTarget(target) is { } path)
            appId = ShellAppUserModelId.ForShortcut(path);

        return IsInstalledTelegram(appId) ? appId : null;
    }

    private static bool IsInstalledTelegram(string? appId)
    {
        if (string.IsNullOrEmpty(appId)) return false;
        var components = appId.Split('.');
        // Chromium fits its Windows AppUserModelID into 64 characters by shortening
        // each component in the middle. The component count includes the profile when present.
        const int maxWindowsIdLength = 64;
        int limit = (maxWindowsIdLength - (components.Length - 1)) / components.Length;
        foreach (string name in TelegramAppNames)
        {
            if (components.Contains(name, StringComparer.OrdinalIgnoreCase)) return true;
            if (limit is < 2 || limit >= name.Length) continue;
            int firstLength = limit / 2;
            string shortened = name[..firstLength] + name[^(limit - firstLength)..];
            if (components.Contains(shortened, StringComparer.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    // UI Automation providers run in other processes. Call this from a worker thread.
    public static nint FindExistingWindow(string? installedAppId)
    {
        var windows = GetBrowserWindows();
        if (installedAppId != null)
        {
            foreach (nint window in windows)
            {
                if (string.Equals(ShellAppUserModelId.ForWindow(window), installedAppId, StringComparison.OrdinalIgnoreCase))
                    return window;
            }
        }

        foreach (nint window in windows)
        {
            try
            {
                if (SelectInWindow(AutomationElement.FromHandle(window))) return window;
            }
            catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or
                COMException or Win32Exception or UnauthorizedAccessException)
            {
                // A browser may close or make its controls unavailable while being inspected.
            }
        }

        if (installedAppId == null)
        {
            foreach (nint window in windows)
            {
                if (IsInstalledTelegram(ShellAppUserModelId.ForWindow(window))) return window;
            }
        }
        return 0;
    }

    private static bool SelectInWindow(AutomationElement browser)
    {
        var address = browser.FindFirst(TreeScope.Descendants, AddressBox);
        if (address == null) return false;
        if (ShowsTelegram(address)) return true;

        var strip = browser.FindFirst(TreeScope.Descendants, TabStrip);
        if (strip == null) return false;
        var candidates = new List<SelectionItemPattern>();
        SelectionItemPattern? originalTab = null;
        foreach (AutomationElement tab in strip.FindAll(TreeScope.Descendants, TabItems))
        {
            if (!tab.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var pattern)) continue;
            var selection = (SelectionItemPattern)pattern;
            if (selection.Current.IsSelected)
            {
                originalTab = selection;
                continue;
            }
            string name = tab.Current.Name;
            if (name.Contains("Telegram", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Телеграм", StringComparison.OrdinalIgnoreCase))
                candidates.Add(selection);
        }
        if (originalTab == null) return false;

        bool matched = false;
        bool changed = false;
        try
        {
            foreach (var candidate in candidates)
            {
                changed = true;
                candidate.Select();
                // The address bar may update just after the tab selection is acknowledged.
                for (int attempt = 0; attempt < 4; attempt++)
                {
                    if (ShowsTelegram(address))
                    {
                        matched = true;
                        return true;
                    }
                    if (attempt < 3) Thread.Sleep(25);
                }
            }
            return false;
        }
        finally
        {
            if (changed && !matched) originalTab.Select();
        }
    }

    private static bool ShowsTelegram(AutomationElement address)
    {
        if (!address.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern)) return false;
        string value = ((ValuePattern)pattern).Current.Value.Trim();
        if (!value.Contains("://", StringComparison.Ordinal)) value = "https://" + value;
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) && IsTelegramClient(uri);
    }

    private static bool IsTelegramClient(Uri uri) =>
        uri.Scheme is "https" or "http" && uri.IsDefaultPort &&
        uri.IdnHost.Equals(TelegramHost, StringComparison.OrdinalIgnoreCase) &&
        uri.AbsolutePath.TrimEnd('/') is "" or "/a" or "/k" or "/z";

    private static List<nint> GetBrowserWindows()
    {
        var windows = new List<nint>();
        var className = new StringBuilder(64);
        var browserProcesses = new Dictionary<int, bool>();
        EnumWindows((window, _) =>
        {
            if (!IsWindowVisible(window) || IsHungAppWindow(window)) return true;
            GetClassNameW(window, className, className.Capacity);
            if (!className.ToString().Equals("Chrome_WidgetWin_1", StringComparison.Ordinal)) return true;
            if (GetWindowThreadProcessId(window, out int processId) == 0) return true;

            if (!browserProcesses.TryGetValue(processId, out bool isBrowser))
            {
                try
                {
                    using var process = Process.GetProcessById(processId);
                    isBrowser = ChromiumBrowsers.Contains(process.ProcessName);
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception) { }
                browserProcesses.Add(processId, isBrowser);
            }
            if (isBrowser) windows.Add(window);
            return true;
        }, 0);
        return windows;
    }

    private delegate bool WindowCallback(nint window, nint parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(WindowCallback callback, nint parameter);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern bool IsHungAppWindow(nint window);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out int processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetClassNameW(nint window, StringBuilder name, int size);
}
