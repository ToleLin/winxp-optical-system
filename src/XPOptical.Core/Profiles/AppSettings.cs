using System.Text.Json;

namespace XPOptical.Core.Profiles;

/// <summary>使用者在「設定」中的選擇；未設定的項目沿用儀器設定檔的預設值。</summary>
public sealed class AppSettings
{
    public CloseAction? CloseAction { get; set; }
    public int? RamMB { get; set; }
    /// <summary>使用者在設定畫面勾選、要自動接入 XP 的 USB 裝置。</summary>
    public List<UsbDeviceRule> UsbDevices { get; set; } = [];

    public static AppSettings Load(string path)
    {
        if (!File.Exists(path)) return new AppSettings();
        try
        {
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Json.Options) ?? new AppSettings();
        }
        catch (JsonException)
        {
            return new AppSettings();
        }
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json.Options));
        File.Move(tmp, path, overwrite: true);
    }

    public CloseAction EffectiveCloseAction(DeviceProfile profile) => CloseAction ?? profile.Vm.CloseAction;
    public int EffectiveRamMB(DeviceProfile profile) => RamMB ?? profile.Vm.RamMB;

    /// <summary>設定檔中有效的規則加上使用者勾選的裝置。</summary>
    public IReadOnlyList<UsbDeviceRule> EffectiveUsbRules(DeviceProfile profile) =>
        profile.Usb.Concat(UsbDevices).Where(r => r.AutoAttach && r.AllIds.Any()).ToList();
}
