using XPOptical.Core.Qemu;

namespace XPOptical.Core.Tests;

public class QemuCommandBuilderTests
{
    private static readonly VmLaunchSpec Spec = new()
    {
        SystemDisk = @"C:\XPOptical\vm\system.qcow2",
        RamMB = 512,
        Accelerators = ["whpx", "tcg"],
        FirmwareDir = @"C:\XPOptical\engine\share\qemu",
    };

    private static string Arg(IReadOnlyList<string> args, string option, int occurrence = 0)
    {
        var indexes = args.Select((a, i) => (a, i)).Where(t => t.a == option).Select(t => t.i).ToList();
        return args[indexes[occurrence] + 1];
    }

    [Fact]
    public void BuildsXpCompatibleMachine()
    {
        var args = QemuCommandBuilder.Build(Spec, new VmEndpoints(4444, 5901));

        Assert.Equal("whpx", Arg(args, "-accel", 0));
        Assert.Equal("tcg", Arg(args, "-accel", 1));
        Assert.Equal("512", Arg(args, "-m"));
        Assert.Equal("cirrus", Arg(args, "-vga"));
        Assert.Equal("base=localtime", Arg(args, "-rtc"));
        Assert.Equal("none", Arg(args, "-nic"));
        Assert.Equal("127.0.0.1:1", Arg(args, "-vnc"));
        Assert.Equal("tcp:127.0.0.1:4444,server=on,wait=off", Arg(args, "-qmp"));
        Assert.DoesNotContain("-incoming", args);

        var devices = args.Select((a, i) => (a, i)).Where(t => t.a == "-device").Select(t => args[t.i + 1]).ToList();
        Assert.Contains(devices, d => d.StartsWith("ich9-usb-ehci1,id=ehci"));
        Assert.Equal(3, devices.Count(d => d.StartsWith("ich9-usb-uhci")));
        Assert.Contains("usb-tablet,bus=ehci.0", devices);
        Assert.DoesNotContain(devices, d => d.Contains("xhci"));
    }

    [Fact]
    public void EmptyCdromDriveIsAlwaysPresent()
    {
        var args = QemuCommandBuilder.Build(Spec, new VmEndpoints(1, 5900));
        Assert.Equal("if=ide,index=2,media=cdrom", Arg(args, "-drive", 1));
        Assert.Equal("order=c,menu=off", Arg(args, "-boot"));
    }

    [Fact]
    public void InstallModeBootsFromCdrom()
    {
        var args = QemuCommandBuilder.Build(Spec with { CdromImage = @"D:\iso\xp.iso", BootFromCdrom = true },
            new VmEndpoints(1, 5900));
        Assert.Equal(@"file=D:\iso\xp.iso,if=ide,index=2,media=cdrom,format=raw", Arg(args, "-drive", 1));
        Assert.Equal("order=dc,menu=off", Arg(args, "-boot"));
    }

    [Fact]
    public void IncomingAddsDefer()
    {
        var args = QemuCommandBuilder.Build(Spec, new VmEndpoints(1, 5900, Incoming: true));
        Assert.Equal("defer", Arg(args, "-incoming"));
    }

    [Fact]
    public void CommasInPathsAreEscaped()
    {
        var args = QemuCommandBuilder.Build(Spec with { SystemDisk = @"C:\a,b\system.qcow2" }, new VmEndpoints(1, 5900));
        Assert.StartsWith(@"file=C:\a,,b\system.qcow2,if=ide", Arg(args, "-drive", 0));
    }

    [Fact]
    public void SignatureIgnoresPortsAndFolderButNotMachineShape()
    {
        var a = QemuCommandBuilder.ComputeSignature(Spec);
        var moved = QemuCommandBuilder.ComputeSignature(Spec with
        {
            SystemDisk = @"E:\隨身碟\XPOptical\vm\system.qcow2",
            FirmwareDir = @"E:\隨身碟\XPOptical\engine\share\qemu",
        });
        var moreRam = QemuCommandBuilder.ComputeSignature(Spec with { RamMB = 1024 });

        Assert.Equal(a, moved);
        Assert.NotEqual(a, moreRam);
    }

    [Fact]
    public void RejectsVncPortBelowBase()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => QemuCommandBuilder.Build(Spec, new VmEndpoints(1, 80)));
    }
}
