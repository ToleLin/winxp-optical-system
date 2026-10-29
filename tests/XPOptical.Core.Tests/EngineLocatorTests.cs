using XPOptical.Core.Qemu;

namespace XPOptical.Core.Tests;

public class EngineLocatorTests
{
    private static string Exe(string name) => OperatingSystem.IsWindows() ? name + ".exe" : name;

    private static TempDir EngineWith(params string[] tools)
    {
        var dir = new TempDir();
        var bin = Path.Combine(dir.Path, "bin");
        Directory.CreateDirectory(bin);
        foreach (var tool in tools) File.WriteAllText(Path.Combine(bin, Exe(tool)), "");
        return dir;
    }

    [Fact]
    public void PrefersX86_64Engine()
    {
        using var dir = EngineWith("qemu-system-i386", "qemu-system-x86_64", "qemu-img");
        var engine = EngineLocator.Find(dir.Path);

        Assert.NotNull(engine);
        Assert.Equal(Exe("qemu-system-x86_64"), Path.GetFileName(engine!.QemuSystem));
    }

    [Fact]
    public void FallsBackToI386Engine()
    {
        using var dir = EngineWith("qemu-system-i386", "qemu-img");
        var engine = EngineLocator.Find(dir.Path);

        Assert.NotNull(engine);
        Assert.Equal(Exe("qemu-system-i386"), Path.GetFileName(engine!.QemuSystem));
    }

    [Fact]
    public void WindowsUsesSoftwareEmulationOnly()
    {
        // WHPX 不支援休眠（migrate 會被擋），Windows 上不能把它列為預設
        if (OperatingSystem.IsWindows())
            Assert.Equal(["tcg"], VmLaunchSpec.DefaultAccelerators);
        else
            Assert.Equal("tcg", VmLaunchSpec.DefaultAccelerators[^1]);
    }
}
