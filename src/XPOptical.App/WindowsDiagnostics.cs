using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using XPOptical.Core;

namespace XPOptical.App;

/// <summary>問題回報中的 Windows 環境資訊。每一項獨立讀取，讀不到時寫「無法取得」而不中斷。</summary>
internal static class WindowsDiagnostics
{
    private const int PfVirtFirmwareEnabled = 21;
    private const int PfSecondLevelAddressTranslation = 20;

    public static IEnumerable<(string Name, string Value)> Collect(AppController app)
    {
        yield return ("程式版本", Safe(() =>
            Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? "未知"));
        yield return ("作業系統", Safe(OsDescription));
        yield return ("CPU", Safe(() => (ReadMachine(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString") as string)?.Trim()));
        yield return ("記憶體", Safe(() => $"{GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1073741824.0:F1} GB"));
        yield return ("BIOS 已開啟硬體虛擬化", Safe(() => YesNo(IsProcessorFeaturePresent(PfVirtFirmwareEnabled))
                                                   + "（Hyper-V 等已在執行時此項可能顯示「否」）"));
        yield return ("CPU 支援 SLAT", Safe(() => YesNo(IsProcessorFeaturePresent(PfSecondLevelAddressTranslation))));
        yield return ("Windows Hypervisor Platform（WinHvPlatform.dll）", Safe(() =>
            YesNo(File.Exists(Path.Combine(Environment.SystemDirectory, "WinHvPlatform.dll")))));
        yield return ("記憶體完整性（HVCI）", Safe(() =>
            ReadMachine(@"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity", "Enabled")
                is int v ? YesNo(v == 1) : "未設定"));
        yield return ("UsbDk 已安裝", Safe(() => YesNo(WindowsSetup.IsUsbDkInstalled())));
        yield return ("執行引擎", Safe(() => app.Engine?.Version ?? "找不到"));
        yield return ("XP 已建立", Safe(() => YesNo(app.Store.IsInstalled)));
        yield return ("XP 狀態", Safe(() => $"{app.VmState}{(app.InstallMode ? "（安裝中）" : "")}"));
        yield return ("有休眠檔", Safe(() => YesNo(app.CanResume)));
        yield return ("軟體模擬", Safe(() => YesNo(app.Vm?.SoftwareEmulation == true)));
        foreach (var s in app.Usb.GetStatus())
            yield return ($"USB 規則「{s.Name}」", $"{s.State} {s.Detail}");
    }

    private static string OsDescription()
    {
        const string key = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";
        var build = ReadMachine(key, "CurrentBuild") as string ?? "";
        var ubr = ReadMachine(key, "UBR");
        // Windows 11 的登錄檔 ProductName 仍寫 Windows 10，以組建編號判斷
        var name = (ReadMachine(key, "ProductName") as string ?? "Windows").Replace("Windows 10",
            int.TryParse(build, out var b) && b >= 22000 ? "Windows 11" : "Windows 10");
        return $"{name} {ReadMachine(key, "DisplayVersion")}（組建 {build}.{ubr}）";
    }

    private static object? ReadMachine(string key, string value)
    {
        using var k = Registry.LocalMachine.OpenSubKey(key);
        return k?.GetValue(value);
    }

    private static string YesNo(bool value) => value ? "是" : "否";

    private static string Safe(Func<string?> read)
    {
        try { return read() ?? "無法取得"; }
        catch (Exception ex) { return $"無法取得（{ex.GetType().Name}）"; }
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsProcessorFeaturePresent(int feature);
}
