using System.Security.Cryptography;
using System.Text;

namespace XPOptical.Core.Qemu;

/// <summary>決定 XP 執行環境「長什麼樣子」的參數；休眠檔只能在相同的參數下恢復。</summary>
public sealed record VmLaunchSpec
{
    public required string SystemDisk { get; init; }
    public int RamMB { get; init; } = 512;
    /// <summary>依序嘗試的加速方式；第一個失敗時 QEMU 會自動改用下一個。</summary>
    public IReadOnlyList<string> Accelerators { get; init; } = DefaultAccelerators;
    public string? FirmwareDir { get; init; }
    /// <summary>光碟機內的映像；null 表示空的光碟機（光碟機一律存在，讓裝置組成固定）。</summary>
    public string? CdromImage { get; init; }
    public bool BootFromCdrom { get; init; }

    /// <summary>
    /// Windows 上只用軟體模擬：QEMU 的 WHPX 不支援休眠（缺少 dirty memory tracking，migrate 直接被擋），
    /// 而「關閉時休眠」是必要功能。
    /// </summary>
    public static IReadOnlyList<string> DefaultAccelerators { get; } =
        OperatingSystem.IsWindows() ? ["tcg"] : ["kvm", "tcg"];
}

/// <summary>每次啟動都可能不同、不影響休眠相容性的連線端點。</summary>
public sealed record VmEndpoints(int QmpPort, int VncPort, bool Incoming = false);

public static class QemuCommandBuilder
{
    /// <summary>USB 裝置掛載的匯流排：ICH9 EHCI，全速/低速裝置自動交給 UHCI 伴隨控制器。</summary>
    public const string UsbBus = "ehci.0";
    public const int VncBasePort = 5900;
    /// <summary>光碟機的磁碟名稱（機器上還有軟碟機，不能只看「可移除」）。</summary>
    public const string CdromDrive = "cd0";

    public static IReadOnlyList<string> Build(VmLaunchSpec spec, VmEndpoints ep)
    {
        if (ep.VncPort != 0 && ep.VncPort < VncBasePort)
            throw new ArgumentOutOfRangeException(nameof(ep), "VNC 連接埠必須 >= 5900");

        var args = new List<string> { "-name", "XPOptical" };
        if (spec.FirmwareDir is not null) args.AddRange(["-L", spec.FirmwareDir]);
        foreach (var accel in spec.Accelerators) args.AddRange(["-accel", accel]);

        args.AddRange([
            "-machine", "pc",
            "-m", spec.RamMB.ToString(),
            // Windows 客體預期 RTC 是本地時間
            "-rtc", "base=localtime",
            "-drive", $"file={EscapeOption(spec.SystemDisk)},if=ide,index=0,media=disk,format=qcow2,cache=writeback",
            "-drive", spec.CdromImage is null
                ? $"if=ide,index=2,media=cdrom,id={CdromDrive}"
                : $"file={EscapeOption(spec.CdromImage)},if=ide,index=2,media=cdrom,format=raw,id={CdromDrive}",
            "-boot", spec.BootFromCdrom ? "order=dc,menu=off" : "order=c,menu=off",
            // XP 內建 Cirrus 驅動，免安裝即可 1024x768
            "-vga", "cirrus",
            // XP 相容 USB 拓撲：EHCI + 3 組 UHCI，不使用 XP 沒有驅動的 xHCI
            "-device", "ich9-usb-ehci1,id=ehci,addr=1d.7,multifunction=on",
            "-device", "ich9-usb-uhci1,addr=1d.0,multifunction=on,masterbus=ehci.0,firstport=0",
            "-device", "ich9-usb-uhci2,addr=1d.1,multifunction=on,masterbus=ehci.0,firstport=2",
            "-device", "ich9-usb-uhci3,addr=1d.2,multifunction=on,masterbus=ehci.0,firstport=4",
            // 絕對座標滑鼠，游標不會被鎖在視窗內
            "-device", $"usb-tablet,bus={UsbBus}",
            // XP 未修補，預設不連網
            "-nic", "none",
            // 光學軟體不需要音效；不初始化音效裝置
            "-audio", "none",
            "-display", "none",
            "-vnc", $"127.0.0.1:{(ep.VncPort == 0 ? 0 : ep.VncPort - VncBasePort)}",
            "-qmp", $"tcp:127.0.0.1:{ep.QmpPort},server=on,wait=off",
        ]);
        if (ep.Incoming) args.AddRange(["-incoming", "defer"]);
        return args;
    }

    /// <summary>
    /// XP 執行環境組成的指紋（不含連接埠與資料夾位置），休眠檔與目前組成不同時不可恢復。
    /// 只取檔名，讓整個資料夾搬到別處後仍可恢復。
    /// </summary>
    public static string ComputeSignature(VmLaunchSpec spec)
    {
        var portable = spec with
        {
            SystemDisk = FileNameOf(spec.SystemDisk),
            CdromImage = spec.CdromImage is null ? null : FileNameOf(spec.CdromImage),
            FirmwareDir = null,
        };
        var text = string.Join("\n", Build(portable, new VmEndpoints(0, 0)));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    private static string FileNameOf(string path) => path[(path.LastIndexOfAny(['/', '\\']) + 1)..];

    /// <summary>QEMU 選項字串中的逗號要寫成兩個逗號。</summary>
    internal static string EscapeOption(string value) => value.Replace(",", ",,");
}
