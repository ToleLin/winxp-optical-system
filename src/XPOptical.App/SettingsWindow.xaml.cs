using System.IO;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using XPOptical.Core;
using XPOptical.Core.Diagnostics;
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
        RamHint.Text = canChangeRam ? "" : "要變更記憶體，請先讓 XP 正常關機（工具列的「■ 關機」）。";

        LoadDevices();
    }

    private sealed record DeviceItem(string Text, UsbId? Id)
    {
        public override string ToString() => Text;
    }

    /// <summary>「其他 USB 裝置」清單的項目（以資料繫結顯示勾選框）。</summary>
    public sealed class OtherDeviceItem(string text, UsbId id, string name, bool isChecked)
    {
        public string Text { get; } = text;
        public UsbId Id { get; } = id;
        public string Name { get; } = name;
        public bool IsChecked { get; set; } = isChecked;
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

        // 其他裝置：保留目前的勾選（含沒接上的），再列出現在接著的裝置
        var checkedNow = OtherDeviceList.ItemsSource is IEnumerable<OtherDeviceItem> shown
            ? shown.Where(o => o.IsChecked).Select(o => (o.Id.ToString(), o.Name)).ToList()
            : _app.Settings.UsbDevices.Where(r => r.Name != InstrumentName && r.Match.Any(m => m.IsValid))
                .Select(r => (r.Match.First(m => m.IsValid).ToString(), r.Name)).ToList();
        var others = present.Select(d => new OtherDeviceItem(d.DisplayName, new UsbId(d.Vid, d.Pid),
            d.Description ?? d.IdText, checkedNow.Any(c => c.Item1 == d.IdText))).ToList();
        foreach (var (id, name) in checkedNow.Where(c => others.All(o => o.Id.ToString() != c.Item1)))
        {
            var parts = id.Split(':');
            others.Add(new OtherDeviceItem($"{name}（{id}，現在沒有接上）", new UsbId { Vid = parts[0], Pid = parts[1] },
                name, true));
        }
        OtherDeviceList.ItemsSource = others;
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e) => LoadDevices();

    private void OnReportClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var zip = ProblemReport.Create(_app.Paths, desktop, WindowsDiagnostics.Collect(_app).ToList(),
                _enumerator.GetPresentDevices());
            MessageBox.Show(this, $"已在桌面建立問題回報：\n{Path.GetFileName(zip)}\n\n請把這個檔案傳給開發者。",
                Title, MessageBoxButton.OK, MessageBoxImage.Information);
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{zip}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"無法產生問題回報：\n{ex.Message}", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        var s = _app.Settings;
        s.CloseAction = HibernateRadio.IsChecked == true ? CloseAction.Hibernate : CloseAction.Shutdown;
        if (RamCombo.IsEnabled && RamCombo.SelectedItem is ComboBoxItem { Tag: string tag })
            s.RamMB = int.Parse(tag);

        s.UsbDevices.Clear();
        var instrument = (DeviceList.SelectedItem as DeviceItem)?.Id;
        if (instrument is not null)
        {
            // iHR320 放第一個（狀態列顯示它）；設定檔若列出韌體下載後的新 ID，一併沿用
            var follow = _app.Profile.Usb.FirstOrDefault(r => r.Name == InstrumentName)?.FollowReenumeration ?? [];
            s.UsbDevices.Add(new UsbDeviceRule { Name = InstrumentName, Match = [instrument], FollowReenumeration = follow });
        }
        foreach (var o in ((IEnumerable<OtherDeviceItem>?)OtherDeviceList.ItemsSource ?? []).Where(o => o.IsChecked))
        {
            if (o.Id.ToString() == instrument?.ToString()) continue;
            s.UsbDevices.Add(new UsbDeviceRule { Name = o.Name, Match = [o.Id] });
        }
        DialogResult = true;
    }
}
