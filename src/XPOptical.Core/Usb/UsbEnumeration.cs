using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;

namespace XPOptical.Core.Usb;

public sealed record UsbDeviceInfo(ushort Vid, ushort Pid, string InstanceId, string? Description)
{
    public string IdText => $"{Vid:x4}:{Pid:x4}";
    public string DisplayName => string.IsNullOrWhiteSpace(Description) ? $"USB 裝置 {IdText}" : $"{Description}（{IdText}）";
}

public interface IUsbEnumerator
{
    /// <summary>目前接在電腦上的 USB 裝置（不含複合裝置的子介面）。</summary>
    IReadOnlyList<UsbDeviceInfo> GetPresentDevices();
}

public static partial class UsbInstanceId
{
    [GeneratedRegex(@"^USB\\VID_([0-9A-F]{4})&PID_([0-9A-F]{4})(?<rest>[^\\]*)\\", RegexOptions.IgnoreCase)]
    private static partial Regex Pattern();

    /// <summary>解析 `USB\VID_xxxx&amp;PID_yyyy\序號`；複合裝置的子介面（&amp;MI_xx）回傳 false。</summary>
    public static bool TryParse(string instanceId, out ushort vid, out ushort pid)
    {
        vid = pid = 0;
        var m = Pattern().Match(instanceId);
        if (!m.Success || m.Groups["rest"].Value.Contains("&MI_", StringComparison.OrdinalIgnoreCase)) return false;
        vid = ushort.Parse(m.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        pid = ushort.Parse(m.Groups[2].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return true;
    }
}

/// <summary>以 Configuration Manager API 列出 USB 裝置。</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsUsbEnumerator : IUsbEnumerator
{
    private const uint CmGetIdListFilterEnumerator = 0x1;
    private const uint CmGetIdListFilterPresent = 0x100;
    private const int CrSuccess = 0;
    private const int CrBufferSmall = 0x1A;

    private static readonly DevPropKey BusReportedDeviceDesc =
        new(new Guid("540b947e-8b40-45bc-a8a2-6a0b894cbda2"), 4);
    private static readonly DevPropKey FriendlyName =
        new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 14);
    private static readonly DevPropKey DeviceDesc =
        new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 2);

    public IReadOnlyList<UsbDeviceInfo> GetPresentDevices()
    {
        var result = new List<UsbDeviceInfo>();
        foreach (var id in GetDeviceIds("USB", CmGetIdListFilterEnumerator | CmGetIdListFilterPresent))
        {
            if (!UsbInstanceId.TryParse(id, out var vid, out var pid)) continue;
            result.Add(new UsbDeviceInfo(vid, pid, id, GetDescription(id)));
        }
        return result;
    }

    private static List<string> GetDeviceIds(string filter, uint flags)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            if (CM_Get_Device_ID_List_SizeW(out var len, filter, flags) != CrSuccess) return [];
            var buffer = new char[len];
            var cr = CM_Get_Device_ID_ListW(filter, buffer, len, flags);
            if (cr == CrBufferSmall) continue; // 列舉期間有裝置插入，重試
            if (cr != CrSuccess) return [];
            return new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries).ToList();
        }
        return [];
    }

    private static string? GetDescription(string instanceId)
    {
        if (CM_Locate_DevNodeW(out var devInst, instanceId, 0) != CrSuccess) return null;
        return GetStringProperty(devInst, BusReportedDeviceDesc)
               ?? GetStringProperty(devInst, FriendlyName)
               ?? GetStringProperty(devInst, DeviceDesc);
    }

    private static string? GetStringProperty(uint devInst, DevPropKey key)
    {
        uint size = 0;
        var cr = CM_Get_DevNode_PropertyW(devInst, ref key, out _, null, ref size, 0);
        if (cr != CrBufferSmall || size == 0) return null;
        var buffer = new byte[size];
        if (CM_Get_DevNode_PropertyW(devInst, ref key, out _, buffer, ref size, 0) != CrSuccess) return null;
        var text = System.Text.Encoding.Unicode.GetString(buffer).TrimEnd('\0');
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DevPropKey(Guid fmtid, uint pid)
    {
        public Guid FmtId = fmtid;
        public uint Pid = pid;
    }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_ID_List_SizeW(out uint length, string filter, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_ID_ListW(string filter, [Out] char[] buffer, uint length, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Locate_DevNodeW(out uint devInst, string deviceId, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_DevNode_PropertyW(uint devInst, ref DevPropKey key, out uint propertyType,
        [Out] byte[]? buffer, ref uint bufferSize, uint flags);
}
