using System.IO.Compression;
using XPOptical.Core.Diagnostics;
using XPOptical.Core.Usb;

namespace XPOptical.Core.Tests;

public class ProblemReportTests
{
    [Fact]
    public void CollectsLogsSettingsAndStateButNotDiskImages()
    {
        using var dir = new TempDir();
        var paths = new AppPaths(dir.Path);
        Directory.CreateDirectory(paths.LogsDir);
        Directory.CreateDirectory(paths.VmDir);
        File.WriteAllText(Path.Combine(paths.LogsDir, "XPOptical-20261029.log"), "[INFO] 程式啟動");
        File.WriteAllText(paths.SettingsFile, "{\"closeAction\":\"hibernate\"}");
        File.WriteAllText(Path.Combine(paths.VmDir, "vm.json"), "{\"installed\":true}");
        File.WriteAllText(Path.Combine(paths.VmDir, "base.qcow2"), "XP 磁碟");
        File.WriteAllText(Path.Combine(paths.VmDir, "state.bin"), "記憶體");

        var zipPath = ProblemReport.Create(paths, Path.Combine(dir.Path, "桌面"),
            [("作業系統", "Windows 11 專業版 24H2"), ("UsbDk 已安裝", "是")],
            [new UsbDeviceInfo(0x1234, 0x5678, @"USB\VID_1234&PID_5678\SN1", "Fake iHR320")]);

        using var zip = ZipFile.OpenRead(zipPath);
        var names = zip.Entries.Select(e => e.FullName).ToList();
        Assert.Contains("summary.txt", names);
        Assert.True(SafeName(Path.GetFileName(zipPath)), zipPath);
        Assert.Contains("logs/XPOptical-20261029.log", names);
        Assert.Contains("settings.json", names);
        Assert.Contains("vm/vm.json", names);
        Assert.DoesNotContain(names, n => n.EndsWith(".qcow2") || n.EndsWith("state.bin"));

        using var summary = new StreamReader(zip.GetEntry("summary.txt")!.Open());
        var text = summary.ReadToEnd();
        Assert.Contains("作業系統：Windows 11 專業版 24H2", text);
        Assert.Contains("1234:5678  Fake iHR320", text);
        Assert.Contains("base.qcow2", text); // 只列出檔名與大小
    }

    private static bool SafeName(string name) => name.All(c => c < 128);

    [Fact]
    public void LargeLogsKeepOnlyTheEnd()
    {
        using var dir = new TempDir();
        var paths = new AppPaths(dir.Path);
        Directory.CreateDirectory(paths.LogsDir);
        var log = Path.Combine(paths.LogsDir, "big.log");
        File.WriteAllText(log, new string('a', ProblemReport.MaxLogBytes) + "結尾");

        var zipPath = ProblemReport.Create(paths, dir.Path, [], []);

        using var zip = ZipFile.OpenRead(zipPath);
        var entry = zip.GetEntry("logs/big.log")!;
        Assert.Equal(ProblemReport.MaxLogBytes, entry.Length);
        using var r = new StreamReader(entry.Open());
        Assert.EndsWith("結尾", r.ReadToEnd());
    }
}
