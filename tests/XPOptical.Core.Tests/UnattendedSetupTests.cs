using System.Text;
using DiscUtils.Fat;
using XPOptical.Core.Media;
using XPOptical.Core.Setup;

namespace XPOptical.Core.Tests;

public class UnattendedSetupTests
{
    // 符合 XP 序號字元規則的測試用序號（不是真的序號）
    internal const string TestKey = "BCDFG-HJKMP-QRTVW-XY234-6789B";

    /// <summary>建立含指定標記檔的假 XP 安裝光碟。</summary>
    internal static async Task<string> FakeXpIsoAsync(string dir, params string[] tagFiles)
    {
        var src = Path.Combine(dir, "iso-src-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(src, "I386"));
        File.WriteAllText(Path.Combine(src, "I386", "SETUPLDR.BIN"), "loader");
        foreach (var tag in tagFiles) File.WriteAllText(Path.Combine(src, tag), "");
        var iso = Path.Combine(dir, $"xp-{Guid.NewGuid():N}.iso");
        await IsoBuilder.BuildFromFolderAsync(src, iso);
        return iso;
    }

    [Theory]
    [InlineData(new[] { "WIN51", "WIN51IP", "WIN51IP.SP3" }, XpEdition.Professional, 3)]
    [InlineData(new[] { "WIN51", "WIN51IC", "WIN51IC.SP2" }, XpEdition.Home, 2)]
    [InlineData(new[] { "WIN51", "WIN51IP" }, XpEdition.Professional, 0)]
    public async Task DetectsXpEditionAndServicePack(string[] tags, XpEdition edition, int sp)
    {
        using var dir = new TempDir();
        var info = XpIsoInfo.Inspect(await FakeXpIsoAsync(dir.Path, tags));

        Assert.True(info.IsXp);
        Assert.Equal(edition, info.Edition);
        Assert.Equal(sp, info.ServicePack);
    }

    [Fact]
    public async Task RejectsNonXpIso()
    {
        using var dir = new TempDir();
        var info = XpIsoInfo.Inspect(await FakeXpIsoAsync(dir.Path, "README.TXT"));
        Assert.False(info.IsXp);
        Assert.Equal("這不是 Windows XP 安裝光碟", info.Description);
    }

    [Theory]
    [InlineData("bcdfg-hjkmp-qrtvw-xy234-6789b", TestKey)]
    [InlineData("BCDFGHJKMPQRTVWXY2346789B", TestKey)]
    [InlineData(" BCDFG HJKMP QRTVW XY234 6789B ", TestKey)]
    [InlineData("ＢＣＤＦＧ－ＨＪＫＭＰ－ＱＲＴＶＷ－ＸＹ２３４－６７８９Ｂ", TestKey)]  // 全形
    [InlineData("BCDFG  -  HJKMP-QRTVW-XY234-6789B\t", TestKey)]  // 貼上時夾帶的空白
    [InlineData("BCDFG-HJKMP-QRTVW-XY234-6789", null)]   // 少一碼
    [InlineData("ABCDE-HJKMP-QRTVW-XY234-6789B", null)]  // A、E 不會出現在 XP 序號
    [InlineData("", null)]
    public void NormalizesProductKey(string input, string? expected) =>
        Assert.Equal(expected, UnattendedSetup.NormalizeProductKey(input));

    [Fact]
    public void WinntSifIsAsciiCrlfAndRunsPostInstallScript()
    {
        var sif = UnattendedSetup.BuildWinntSif("bcdfghjkmpqrtvwxy2346789b");

        Assert.Contains($"ProductKey=\"{TestKey}\"", sif);
        Assert.Contains("UnattendMode=FullUnattended", sif);
        Assert.Contains("Command0=\"wscript.exe A:\\XPOPOST.VBS\"", sif);
        Assert.Contains("TimeZone=220", sif);
        Assert.All(sif, c => Assert.True(c < 128));
        Assert.DoesNotContain("\n", sif.Replace("\r\n", ""));
        Assert.Throws<ArgumentException>(() => UnattendedSetup.BuildWinntSif("123"));
    }

    [Fact]
    public void PostInstallScriptSetsAutoLogonMarksDoneAndPowersOff()
    {
        var vbs = UnattendedSetup.BuildPostInstallScript();

        Assert.Contains("\"AutoAdminLogon\", \"1\"", vbs);
        Assert.Contains("A:\\DONE.TXT", vbs);
        Assert.Contains("winmgmts:{impersonationLevel=impersonate,(Shutdown)}!\\\\.\\root\\cimv2", vbs);
        Assert.Contains("os.Win32Shutdown 12", vbs);
        Assert.All(vbs, c => Assert.True(c < 128));
    }

    [Fact]
    public void FloppyHoldsSetupFilesAndDetectsCompletion()
    {
        using var dir = new TempDir();
        var floppy = Path.Combine(dir.Path, "setup-floppy.img");

        UnattendedSetup.CreateFloppy(floppy, TestKey);

        Assert.Equal(1474560, new FileInfo(floppy).Length);
        using (var stream = File.OpenRead(floppy))
        using (var fs = new FatFileSystem(stream))
        {
            using var sif = new StreamReader(fs.OpenFile("WINNT.SIF", FileMode.Open), Encoding.ASCII);
            Assert.Contains(TestKey, sif.ReadToEnd());
            Assert.True(fs.FileExists("XPOPOST.VBS"));
        }
        Assert.False(UnattendedSetup.IsCompleted(floppy));

        MarkDone(floppy);
        Assert.True(UnattendedSetup.IsCompleted(floppy));
    }

    /// <summary>模擬 XP 的安裝後腳本在 A: 寫入 done.txt（XP 以小寫建立）。</summary>
    internal static void MarkDone(string floppy)
    {
        using var stream = new FileStream(floppy, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        using var fs = new FatFileSystem(stream);
        using var f = fs.OpenFile("done.txt", FileMode.Create, FileAccess.Write);
        f.Write("done\r\n"u8);
    }
}
