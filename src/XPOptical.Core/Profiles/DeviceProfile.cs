using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace XPOptical.Core.Profiles;

public enum CloseAction
{
    /// <summary>記憶體狀態存檔，下次回到關閉前的畫面。</summary>
    Hibernate,
    /// <summary>XP 正常關機。</summary>
    Shutdown,
}

/// <summary>儀器設定檔（例如 profiles/ihr320.json）。</summary>
public sealed class DeviceProfile
{
    public int SchemaVersion { get; set; } = 1;
    public string ProfileName { get; set; } = "";
    public VmDefaults Vm { get; set; } = new();
    public List<UsbDeviceRule> Usb { get; set; } = [];

    public static DeviceProfile Load(string path) =>
        JsonSerializer.Deserialize<DeviceProfile>(File.ReadAllText(path), Json.Options)
        ?? throw new InvalidDataException($"設定檔是空的：{path}");
}

public sealed class VmDefaults
{
    public int RamMB { get; set; } = 512;
    public CloseAction CloseAction { get; set; } = CloseAction.Hibernate;
}

/// <summary>要自動接入 XP 的 USB 裝置。</summary>
public sealed class UsbDeviceRule
{
    public string Name { get; set; } = "";
    public List<UsbId> Match { get; set; } = [];
    /// <summary>XP 驅動下載韌體後，裝置以新的 VID:PID 重新出現時使用。</summary>
    public List<UsbId> FollowReenumeration { get; set; } = [];
    public bool AutoAttach { get; set; } = true;

    [JsonIgnore]
    public IEnumerable<UsbId> AllIds => Match.Concat(FollowReenumeration).Where(id => id.IsValid);

    public bool Matches(ushort vid, ushort pid) => AllIds.Any(id => id.Matches(vid, pid));
}

public sealed class UsbId
{
    public string Vid { get; set; } = "";
    public string Pid { get; set; } = "";

    public UsbId() { }
    public UsbId(ushort vid, ushort pid)
    {
        Vid = vid.ToString("x4");
        Pid = pid.ToString("x4");
    }

    [JsonIgnore]
    public bool IsValid => TryParse(Vid, out _) && TryParse(Pid, out _);

    public bool Matches(ushort vid, ushort pid) =>
        TryParse(Vid, out var v) && TryParse(Pid, out var p) && v == vid && p == pid;

    public override string ToString() => $"{Vid.ToLowerInvariant()}:{Pid.ToLowerInvariant()}";

    private static bool TryParse(string s, out ushort value) =>
        ushort.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value) && s.Length == 4;
}

internal static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
}
