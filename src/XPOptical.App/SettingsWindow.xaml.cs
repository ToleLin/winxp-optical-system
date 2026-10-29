using System.Windows;
using System.Windows.Controls;
using XPOptical.Core;
using XPOptical.Core.Profiles;
using XPOptical.Core.Usb;
using XPOptical.Core.Vm;

namespace XPOptical.App;

public partial class SettingsWindow : Window
{
    private const string InstrumentName = "iHR320";
    private readonly AppController _app;
    private readonly WindowsUsbEnumerator _enumerator = new();

    public SettingsWindow(AppController app)
    {
        _app = app;
        InitializeComponent();

        var close = app.Settings.EffectiveCloseAction(app.Profile);
        HibernateRadio.IsChecked = close == CloseAction.Hibernate;
        ShutdownRadio.IsChecked = close == CloseAction.Shutdown;

        var ram = app.Settings.EffectiveRamMB(app.Profile).ToString();
        RamCombo.SelectedItem = RamCombo.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == ram)
                                ?? RamCombo.Items[1];
        // 變更記憶體會讓休眠中的畫面無法恢復，所以只允許在 XP 完全關機時變更
        var canChangeRam = app.VmState == VmState.Stopped && !app.CanResume;
        RamCombo.IsEnabled = canChangeRam;
        RamHint.Text = canChangeRam ? "" : "要變更記憶體，請先讓 XP「正常關機」（工具列的 ⏻ 關機）。";

        LoadDevices();
    }

    private sealed record DeviceItem(string Text, UsbId? Id)
    {
        public override string ToString() => Text;
    }

    private UsbId? CurrentId =>
        _app.Settings.UsbDevices.FirstOrDefault(r => r.Name == InstrumentName)?.Match.FirstOrDefault(m => m.IsValid);

    private void LoadDevices()
    {
        var current = (DeviceList.SelectedItem as DeviceItem)?.Id ?? CurrentId;
        var items = new List<DeviceItem> { new("（不自動連接）", null) };
        var present = _enumerator.GetPresentDevices()
            .GroupBy(d => d.IdText).Select(g => g.First())
            .OrderBy(d => d.DisplayName, StringComparer.CurrentCulture);
        items.AddRange(present.Select(d => new DeviceItem(d.DisplayName, new UsbId(d.Vid, d.Pid))));
        if (current is not null && items.All(i => i.Id?.ToString() != current.ToString()))
            items.Add(new DeviceItem($"目前設定的裝置（{current}，現在沒有接上）", current));

        DeviceList.ItemsSource = items;
        DeviceList.SelectedItem = items.FirstOrDefault(i => i.Id?.ToString() == current?.ToString()) ?? items[0];
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e) => LoadDevices();

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        var s = _app.Settings;
        s.CloseAction = HibernateRadio.IsChecked == true ? CloseAction.Hibernate : CloseAction.Shutdown;
        if (RamCombo.IsEnabled && RamCombo.SelectedItem is ComboBoxItem { Tag: string tag })
            s.RamMB = int.Parse(tag);

        s.UsbDevices.RemoveAll(r => r.Name == InstrumentName);
        if (DeviceList.SelectedItem is DeviceItem { Id: { } id })
        {
            // 設定檔若列出韌體下載後的新 ID，一併沿用
            var follow = _app.Profile.Usb.FirstOrDefault(r => r.Name == InstrumentName)?.FollowReenumeration ?? [];
            s.UsbDevices.Add(new UsbDeviceRule { Name = InstrumentName, Match = [id], FollowReenumeration = follow });
        }
        DialogResult = true;
    }
}
