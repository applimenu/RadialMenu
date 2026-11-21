using System.Net.Http;
using System.Text.Json;
using System.Windows;

namespace RadialMenu1;

public partial class BugReportWindow : Window
{
    private static BugReportWindow? _activeDialog;
    private readonly CancellationTokenSource _lifetime = new();

    public BugReportWindow()
    {
        InitializeComponent();
        SettingsTheme.PreventMaximize(this);
        SettingsTheme.Apply(Resources, MenuPalette.From(ConfigStore.Current));
        Loaded += (s, e) => Description.Focus();
        Closed += (s, e) =>
        {
            _lifetime.Cancel();
            _lifetime.Dispose();
        };
    }

    internal static void ShowFor(Window? owner)
    {
        if (_activeDialog is { } active)
        {
            active.Activate();
            return;
        }

        var dialog = new BugReportWindow
        {
            Owner = owner,
            WindowStartupLocation = owner == null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner
        };
        _activeDialog = dialog;
        try { dialog.ShowDialog(); }
        finally { _activeDialog = null; }
    }

    private async void Send_Click(object sender, RoutedEventArgs e)
    {
        string description = Description.Text.Trim();
        if (description.Length == 0)
        {
            DeliveryStatus.Text = "Напиши сообщение перед отправкой.";
            return;
        }

        var cancellation = _lifetime.Token;
        SendButton.IsEnabled = false;
        Description.IsReadOnly = true;
        DeliveryStatus.Text = "Отправляю…";
        try
        {
            await ReportDelivery.SendAsync(description, cancellation);
            if (cancellation.IsCancellationRequested) return;
            DeliveryStatus.Text = "Спасибо, сообщение доставлено.";
            SendButton.Content = "Отправлено";
            await Task.Delay(TimeSpan.FromSeconds(1.2), cancellation);
            if (!cancellation.IsCancellationRequested) Close();
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException or OperationCanceledException)
        {
            if (cancellation.IsCancellationRequested) return;
            DeliveryStatus.Text = ex is InvalidOperationException ? ex.Message :
                "Не удалось доставить отчёт. Проверь соединение и попробуй снова.";
            SendButton.IsEnabled = true;
            Description.IsReadOnly = false;
        }
    }
}
