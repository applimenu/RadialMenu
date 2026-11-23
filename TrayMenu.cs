using System.Windows;
using Forms = System.Windows.Forms;

namespace RadialMenu1;

internal sealed class TrayMenu : IDisposable
{
    private readonly Forms.NotifyIcon _notification;
    private readonly Forms.ContextMenuStrip _menu = new();
    private readonly System.Drawing.Icon _icon;
    private readonly Forms.ToolStripMenuItem _recordItem = new();

    public TrayMenu(Action settings, Action updates, Action report, Action<MenuTheme> selectTheme, Action exit)
    {
        var resource = Application.GetResourceStream(new Uri("pack://application:,,,/RadialMenu1;component/app.ico"));
        using var stream = resource?.Stream;
        using var original = stream == null ? null : new System.Drawing.Icon(stream);
        _icon = (System.Drawing.Icon)(original ?? System.Drawing.SystemIcons.Application).Clone();

        _menu.Items.Add("Настройки", null, (s, e) => settings());
        var themes = new Forms.ToolStripMenuItem("Тема");
        foreach (var theme in Enum.GetValues<MenuTheme>())
        {
            var item = new Forms.ToolStripMenuItem(MenuPalette.DisplayName(theme)) { Tag = theme };
            item.Click += (s, e) => selectTheme(theme);
            themes.DropDownItems.Add(item);
        }
        _menu.Opening += (s, e) =>
        {
            foreach (Forms.ToolStripMenuItem item in themes.DropDownItems)
                item.Checked = item.Tag is MenuTheme theme && ConfigStore.Current.Theme == theme;
        };
        _menu.Items.Add(themes);
        _recordItem.Click += ToggleRecording;
        _menu.Items.Add(_recordItem);
        _menu.Items.Add("Папка записей", null, (_, _) => ScreenRecording.OpenFolder());
        _menu.Items.Add("Проверить обновления", null, (s, e) => updates());
        if (ReportDelivery.CanReport)
            _menu.Items.Add(ReportDelivery.CanSend ? "Ошибка или идея…" : "Бот обратной связи", null, (s, e) => report());
        _menu.Items.Add(new Forms.ToolStripSeparator());
        _menu.Items.Add("Выйти из RadialMenu", null, (s, e) => exit());

        _notification = new Forms.NotifyIcon
        {
            Icon = _icon, Text = "RadialMenu", ContextMenuStrip = _menu, Visible = true
        };
        _notification.MouseDoubleClick += (s, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) settings();
        };
        ScreenRecording.Changed += UpdateRecording;
        UpdateRecording();
    }

    public void Dispose()
    {
        ScreenRecording.Changed -= UpdateRecording;
        _notification.Visible = false;
        _notification.Dispose();
        _menu.Dispose();
        _icon.Dispose();
    }

    private void UpdateRecording()
    {
        _recordItem.Text = ScreenRecording.IsRecording ? "● Остановить запись" : "Начать запись экрана";
        _recordItem.Enabled = !ScreenRecording.IsBusy;
        _notification.Text = ScreenRecording.IsRecording ? "RadialMenu — идёт запись" : "RadialMenu";
    }

    private async void ToggleRecording(object? sender, EventArgs e)
    {
        await ScreenRecording.ToggleAsync();
        if (!ScreenRecording.IsBusy && !ScreenRecording.IsRecording && _notification.Visible)
            _notification.ShowBalloonTip(4000, "RadialMenu", ScreenRecording.StatusText, Forms.ToolTipIcon.Info);
    }

    public void NotifyTimerElapsed() =>
        _notification.ShowBalloonTip(4000, "RadialMenu", "Таймер истёк", Forms.ToolTipIcon.Info);
}
