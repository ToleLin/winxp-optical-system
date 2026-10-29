using System.Diagnostics;

namespace XPOptical.Core.Qemu;

/// <summary>
/// 找出執行引擎（QEMU、qemu-img、usbredirect）。發佈版放在 engine\bin，
/// 韌體與鍵盤配置檔放在 engine\share\qemu；開發/測試時退而搜尋 PATH。
/// </summary>
public sealed class EngineLocator
{
    private string? _version;

    private EngineLocator(string qemuSystem, string qemuImg, string? usbRedirect, string? firmwareDir)
    {
        QemuSystem = qemuSystem;
        QemuImg = qemuImg;
        UsbRedirect = usbRedirect;
        FirmwareDir = firmwareDir;
    }

    public string QemuSystem { get; }
    public string QemuImg { get; }
    /// <summary>找不到時為 null，USB 轉送功能停用。</summary>
    public string? UsbRedirect { get; }
    public string? FirmwareDir { get; }

    public static EngineLocator? Find(string engineDir)
    {
        var binDir = Path.Combine(engineDir, "bin");
        // x86_64 版可執行 32 位元 XP，TCG 下也比 i386 版快；舊引擎資料夾只有 i386 版時仍可使用
        var qemu = Locate(binDir, "qemu-system-x86_64", "qemu-system-i386");
        var img = Locate(binDir, "qemu-img");
        if (qemu is null || img is null) return null;

        var firmware = Path.Combine(engineDir, "share", "qemu");
        return new EngineLocator(qemu, img, Locate(binDir, "usbredirect"),
            Directory.Exists(firmware) ? firmware : null);
    }

    /// <summary>
    /// 引擎名稱加上 `--version` 的第一行，用於判斷休眠檔是否還能恢復。
    /// i386 與 x86_64 版的版本字串相同，但休眠檔不相容，所以要帶上程式名稱。
    /// </summary>
    public string Version => _version ??= ReadVersion();

    private string ReadVersion()
    {
        var psi = new ProcessStartInfo(QemuSystem, "--version")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("無法啟動執行引擎");
        var first = p.StandardOutput.ReadLine() ?? "";
        p.WaitForExit(10_000);
        return $"{Path.GetFileNameWithoutExtension(QemuSystem)} {first.Trim()}";
    }

    /// <summary>依序找 names 中的任一個：先找隨附的 engine\bin，都沒有才搜尋 PATH（不混用兩處的引擎）。</summary>
    private static string? Locate(string binDir, params string[] names)
    {
        var exes = names.Select(n => OperatingSystem.IsWindows() ? n + ".exe" : n).ToList();
        foreach (var exe in exes)
        {
            var bundled = Path.Combine(binDir, exe);
            if (File.Exists(bundled)) return bundled;
        }

        foreach (var exe in exes)
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (dir.Length == 0) continue;
            var candidate = Path.Combine(dir, exe);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}
