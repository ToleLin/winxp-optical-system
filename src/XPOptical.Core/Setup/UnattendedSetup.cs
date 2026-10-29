using System.Text;
using System.Text.RegularExpressions;
using DiscUtils;
using DiscUtils.Fat;

namespace XPOptical.Core.Setup;

/// <summary>
/// XP 無人值守安裝：產生一片虛擬磁碟片，內含
/// <list type="bullet">
/// <item><c>WINNT.SIF</c>：XP 安裝程式從光碟開機時會自動讀取 A:\WINNT.SIF，全程不需操作</item>
/// <item><c>XPOPOST.VBS</c>：安裝完成後第一次登入時執行（GuiRunOnce）：設定自動登入、寫入完成標記、關機</item>
/// </list>
/// 主程式在 XP 自行關機（執行引擎結束）後檢查磁碟片上的 <c>DONE.TXT</c>，確認安裝完成。
/// 兩個檔案都只用 ASCII，XP 的安裝程式與 Windows Script Host 以系統字碼頁讀取。
/// </summary>
public static partial class UnattendedSetup
{
    public const string SifName = "WINNT.SIF";
    public const string ScriptName = "XPOPOST.VBS";
    public const string DoneName = "DONE.TXT";

    /// <summary>XP 產品序號使用的字元（不含容易混淆的 A E I L N O S U Z 0 1 5）。</summary>
    [GeneratedRegex("^[BCDFGHJKMPQRTVWXY2346789]{5}(-[BCDFGHJKMPQRTVWXY2346789]{5}){4}$")]
    private static partial Regex KeyPattern();

    /// <summary>整理使用者輸入的序號（去空白、轉大寫、補上連字號）；格式不對時回傳 null。</summary>
    public static string? NormalizeProductKey(string input)
    {
        var chars = new string(input.ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());
        if (chars.Length != 25) return null;
        var key = string.Join("-", Enumerable.Range(0, 5).Select(i => chars.Substring(i * 5, 5)));
        return KeyPattern().IsMatch(key) ? key : null;
    }

    public static string BuildWinntSif(string productKey)
    {
        var key = NormalizeProductKey(productKey) ?? throw new ArgumentException("產品序號格式不正確", nameof(productKey));
        return $"""
            ; XPOptical unattended setup (generated)
            [Data]
            AutoPartition=1
            MsDosInitiated="0"
            UnattendedInstall="Yes"
            AutomaticUpdates=No

            [Unattended]
            UnattendMode=FullUnattended
            OemSkipEula=Yes
            OemPreinstall=No
            TargetPath=\WINDOWS
            Repartition=Yes
            UnattendSwitch=Yes
            WaitForReboot=No
            DriverSigningPolicy=Ignore
            NonDriverSigningPolicy=Ignore
            Hibernation=No

            [GuiUnattended]
            AdminPassword=*
            EncryptedAdminPassword=NO
            AutoLogon=Yes
            AutoLogonCount=1
            OEMSkipRegional=1
            OemSkipWelcome=1
            TimeZone=220

            [UserData]
            ProductKey="{key}"
            FullName="XPOptical"
            OrgName=""
            ComputerName=XPOPTICAL

            [Display]
            BitsPerPel=16
            Xresolution=1024
            YResolution=768
            Vrefresh=60

            [Identification]
            JoinWorkgroup=WORKGROUP

            [Networking]
            InstallDefaultComponents=Yes

            [GuiRunOnce]
            Command0="wscript.exe A:\{ScriptName}"

            """.ReplaceLineEndings("\r\n");
    }

    /// <summary>安裝完成後第一次登入執行：自動登入、完成標記、強制關機（電源關閉）。</summary>
    public static string BuildPostInstallScript() => $"""
        ' XPOptical: runs once after XP setup finishes (GuiRunOnce)
        On Error Resume Next
        Dim sh, net, fso, f, os
        Set sh = CreateObject("WScript.Shell")
        Set net = CreateObject("WScript.Network")
        Const WL = "HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon\"
        sh.RegWrite WL & "AutoAdminLogon", "1", "REG_SZ"
        sh.RegWrite WL & "DefaultUserName", net.UserName, "REG_SZ"
        sh.RegWrite WL & "DefaultPassword", "", "REG_SZ"
        sh.RegWrite WL & "DefaultDomainName", net.ComputerName, "REG_SZ"
        Set fso = CreateObject("Scripting.FileSystemObject")
        Set f = fso.CreateTextFile("A:\{DoneName}", True)
        f.WriteLine "done"
        f.Close
        ' 12 = forced power off; QEMU exits when XP powers off
        For Each os In GetObject("winmgmts:{"{"}impersonationLevel=impersonate,(Shutdown){"}"}!\\.\root\cimv2").ExecQuery("Select * from Win32_OperatingSystem")
            os.Win32Shutdown 12
        Next

        """.ReplaceLineEndings("\r\n");

    /// <summary>建立 1.44 MB 磁碟片映像，放入自動安裝設定與安裝後腳本。</summary>
    public static void CreateFloppy(string path, string productKey)
    {
        var sif = BuildWinntSif(productKey);
        var tmp = path + ".tmp";
        using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.ReadWrite))
        using (var fs = FatFileSystem.FormatFloppy(stream, FloppyDiskType.HighDensity, "XPOPTICAL"))
        {
            WriteFile(fs, SifName, sif);
            WriteFile(fs, ScriptName, BuildPostInstallScript());
        }
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>XP 的安裝後腳本已執行（磁碟片上有完成標記）。</summary>
    public static bool IsCompleted(string floppyPath)
    {
        if (!File.Exists(floppyPath)) return false;
        using var stream = new FileStream(floppyPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var fs = new FatFileSystem(stream);
        return fs.GetFiles("\\").Any(f => f.TrimStart('\\').Equals(DoneName, StringComparison.OrdinalIgnoreCase));
    }

    private static void WriteFile(FatFileSystem fs, string name, string content)
    {
        using var s = fs.OpenFile(name, FileMode.Create, FileAccess.Write);
        var bytes = Encoding.ASCII.GetBytes(content);
        s.Write(bytes, 0, bytes.Length);
    }
}
