using XPOptical.Core.Profiles;
using XPOptical.Core.Usb;

namespace XPOptical.Core.Tests;

public class ProfileTests
{
    [Fact]
    public void BundledIhr320ProfileLoads()
    {
        var profile = DeviceProfile.Load(Path.Combine(AppContext.BaseDirectory, "profiles", "ihr320.json"));

        Assert.Equal("HORIBA iHR320 分光儀", profile.ProfileName);
        Assert.Equal(CloseAction.Hibernate, profile.Vm.CloseAction);
        Assert.Equal(512, profile.Vm.RamMB);
        var rule = Assert.Single(profile.Usb);
        Assert.Equal("iHR320", rule.Name);
        // VID:PID 尚未填入（待在 Win11 裝置管理員讀取），所以不會產生有效規則
        Assert.Empty(rule.AllIds);
        Assert.Empty(new AppSettings().EffectiveUsbRules(profile));
    }

    [Theory]
    [InlineData("1234", "abcd", 0x1234, 0xabcd, true)]
    [InlineData("1234", "ABCD", 0x1234, 0xabcd, true)]
    [InlineData("1234", "abcd", 0x1234, 0xabce, false)]
    [InlineData("", "", 0, 0, false)]
    [InlineData("123", "abcd", 0x123, 0xabcd, false)]
    [InlineData("zzzz", "abcd", 0, 0xabcd, false)]
    public void UsbIdMatching(string vid, string pid, ushort deviceVid, ushort devicePid, bool expected)
    {
        Assert.Equal(expected, new UsbId { Vid = vid, Pid = pid }.Matches(deviceVid, devicePid));
    }

    [Fact]
    public void RuleMatchesReenumeratedId()
    {
        var rule = new UsbDeviceRule
        {
            Name = "儀器",
            Match = [new UsbId(0x1234, 0x0001)],
            FollowReenumeration = [new UsbId(0x1234, 0x0002)],
        };
        Assert.True(rule.Matches(0x1234, 0x0001));
        Assert.True(rule.Matches(0x1234, 0x0002));
        Assert.False(rule.Matches(0x1234, 0x0003));
    }

    [Fact]
    public void SettingsOverrideProfileAndRoundTrip()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "settings.json");
        var profile = new DeviceProfile { Usb = [new UsbDeviceRule { Name = "未設定", Match = [new UsbId()] }] };

        var settings = new AppSettings
        {
            CloseAction = CloseAction.Shutdown,
            RamMB = 256,
            UsbDevices = [new UsbDeviceRule { Name = "iHR320", Match = [new UsbId(0x1234, 0x5678)] }],
        };
        settings.Save(path);
        var loaded = AppSettings.Load(path);

        Assert.Equal(CloseAction.Shutdown, loaded.EffectiveCloseAction(profile));
        Assert.Equal(256, loaded.EffectiveRamMB(profile));
        var rule = Assert.Single(loaded.EffectiveUsbRules(profile));
        Assert.Equal("iHR320", rule.Name);
        Assert.Contains("\"closeAction\": \"shutdown\"", File.ReadAllText(path));
    }

    [Fact]
    public void MissingOrCorruptSettingsFallBackToDefaults()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "settings.json");
        Assert.Null(AppSettings.Load(path).CloseAction);
        File.WriteAllText(path, "{ 這不是 JSON");
        Assert.Null(AppSettings.Load(path).CloseAction);
    }

    [Theory]
    [InlineData(@"USB\VID_1234&PID_ABCD\SN001", true, 0x1234, 0xabcd)]
    [InlineData(@"USB\VID_0403&PID_6001\A600XYZ", true, 0x0403, 0x6001)]
    [InlineData(@"USB\VID_1234&PID_ABCD&MI_00\7&1A2B&0&0000", false, 0, 0)]
    [InlineData(@"USB\ROOT_HUB30\4&1234&0&0", false, 0, 0)]
    [InlineData(@"HID\VID_1234&PID_ABCD\1", false, 0, 0)]
    public void ParsesUsbInstanceIds(string id, bool ok, ushort vid, ushort pid)
    {
        Assert.Equal(ok, UsbInstanceId.TryParse(id, out var v, out var p));
        Assert.Equal(vid, v);
        Assert.Equal(pid, p);
    }
}
