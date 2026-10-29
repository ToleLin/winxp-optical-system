using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace XPOptical.App;

/// <summary>首次使用時需要系統管理員權限的兩件事：安裝 UsbDk、啟用 Windows Hypervisor Platform。</summary>
internal static class WindowsSetup
{
    private const int ErrorCancelled = 1223;

    public static bool IsUsbDkInstalled()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\UsbDk");
        return key is not null;
    }

    public static string? FindUsbDkInstaller(string driversDir) =>
        Directory.Exists(driversDir)
            ? Directory.EnumerateFiles(driversDir, "UsbDk*x64*.msi").Concat(Directory.EnumerateFiles(driversDir, "UsbDk*.msi"))
                .FirstOrDefault()
            : null;

    /// <summary>以系統管理員身分安裝。使用者在 UAC 按「否」時回傳 false。</summary>
    public static Task<bool> InstallUsbDkAsync(string msi) =>
        RunElevatedAsync("msiexec.exe", $"/i \"{msi}\" /passive /norestart");

    /// <summary>啟用 Windows Hypervisor Platform（需重新開機才生效）。</summary>
    public static Task<bool> EnableHypervisorPlatformAsync() =>
        RunElevatedAsync("dism.exe", "/online /enable-feature /featurename:HypervisorPlatform /all /norestart");

    private static async Task<bool> RunElevatedAsync(string exe, string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, args)
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            if (p is null) return false;
            await p.WaitForExitAsync();
            // dism 成功但需重開機時回傳 3010
            return p.ExitCode is 0 or 3010;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            return false;
        }
    }
}
