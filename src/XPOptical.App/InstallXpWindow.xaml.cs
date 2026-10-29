using System.IO;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using XPOptical.Core.Setup;

namespace XPOptical.App;

/// <summary>全自動安裝 XP：選擇 ISO（自動偵測版本）並輸入產品序號。</summary>
public partial class InstallXpWindow : Window
{
    private XpIsoInfo? _info;

    public InstallXpWindow(string? suggestedIso)
    {
        InitializeComponent();
        if (suggestedIso is not null && File.Exists(suggestedIso)) SetIso(suggestedIso);
        Loaded += (_, _) => KeyBox.Focus();
    }

    public string IsoPath => IsoBox.Text;
    public string ProductKey => UnattendedSetup.NormalizeProductKey(KeyBox.Text)!;

    private void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "選擇 Windows XP 安裝光碟的 ISO 檔",
            Filter = "光碟映像檔 (*.iso)|*.iso|所有檔案 (*.*)|*.*",
        };
        if (dlg.ShowDialog(this) == true) SetIso(dlg.FileName);
    }

    private void SetIso(string path)
    {
        IsoBox.Text = path;
        try
        {
            _info = XpIsoInfo.Inspect(path);
            IsoInfo.Text = _info.IsXp ? $"偵測到：{_info.Description}" : "這不是 Windows XP 安裝光碟，請選擇其他檔案。";
            IsoInfo.Foreground = _info.IsXp ? Brushes.ForestGreen : Brushes.OrangeRed;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            _info = null;
            IsoInfo.Text = $"無法讀取這個檔案：{ex.Message}";
            IsoInfo.Foreground = Brushes.OrangeRed;
        }
        UpdateStart();
    }

    private void OnKeyChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => UpdateStart();

    private void UpdateStart()
    {
        var keyOk = UnattendedSetup.NormalizeProductKey(KeyBox.Text) is not null;
        if (KeyBox.Text.Length > 0)
        {
            KeyInfo.Text = keyOk ? "序號格式正確。" : "請輸入 25 碼序號（英文字母與數字，可含連字號）。";
            KeyInfo.Foreground = keyOk ? Brushes.ForestGreen : (Brush)new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55));
        }
        StartButton.IsEnabled = keyOk && _info?.IsXp == true;
    }

    private void OnStartClick(object sender, RoutedEventArgs e) => DialogResult = true;
}
