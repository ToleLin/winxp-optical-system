using XPOptical.Core.Qemu;

namespace XPOptical.Core.Tests;

public class SafePathTests
{
    [Fact]
    public void AsciiPathIsKept()
    {
        Assert.Equal("/opt/XPOptical/vm/system.qcow2", SafePath.MakeSafe("/opt/XPOptical/vm/system.qcow2", "/opt"));
    }

    [Fact]
    public void ChinesePathInsideWorkingDirBecomesRelative()
    {
        var vm = Path.Combine(Path.GetTempPath(), "王小明的桌面", "XPOptical", "vm");
        Assert.Equal("system.qcow2", SafePath.MakeSafe(Path.Combine(vm, "system.qcow2"), vm));
        Assert.Equal(Path.Combine("..", "engine", "share", "qemu"),
            SafePath.MakeSafe(Path.Combine(vm, "..", "engine", "share", "qemu"), vm));
    }

    [Fact]
    public void ChinesePathOutsideWorkingDirIsRejectedWithGuidance()
    {
        if (OperatingSystem.IsWindows()) return; // Windows 上可能還能轉成短檔名
        var vm = Path.Combine(Path.GetTempPath(), "XPOptical", "vm");
        var iso = Path.Combine(Path.GetTempPath(), "下載", "xp.iso");
        Assert.Null(SafePath.TryMakeSafe(iso, vm));
        var ex = Assert.Throws<UnsafePathException>(() => SafePath.MakeSafe(iso, vm));
        Assert.Contains("C:\\XPOptical", ex.Message);
    }
}
