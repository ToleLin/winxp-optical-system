using DiscUtils.Iso9660;
using XPOptical.Core.Media;

namespace XPOptical.Core.Tests;

public class IsoBuilderTests
{
    [Fact]
    public async Task BuildsJolietIsoWithChineseNames()
    {
        using var dir = new TempDir();
        var src = Path.Combine(dir.Path, "光學軟體安裝檔");
        Directory.CreateDirectory(Path.Combine(src, "驅動程式"));
        File.WriteAllText(Path.Combine(src, "安裝說明.txt"), "請執行 setup.exe");
        File.WriteAllBytes(Path.Combine(src, "驅動程式", "ihr320.inf"), [1, 2, 3]);
        var iso = Path.Combine(dir.Path, "import.iso");

        await IsoBuilder.BuildFromFolderAsync(src, iso);

        await using var stream = File.OpenRead(iso);
        using var reader = new CDReader(stream, joliet: true);
        Assert.True(reader.FileExists(@"安裝說明.txt"));
        Assert.True(reader.FileExists(@"驅動程式\ihr320.inf"));
        using var f = reader.OpenFile(@"驅動程式\ihr320.inf", FileMode.Open);
        Assert.Equal(3, f.Length);
    }

    [Fact]
    public async Task BuildsFromDroppedItems()
    {
        using var dir = new TempDir();
        var file = Path.Combine(dir.Path, "校正資料.csv");
        File.WriteAllText(file, "nm,counts");
        var folder = Path.Combine(dir.Path, "SynerJY");
        Directory.CreateDirectory(Path.Combine(folder, "bin"));
        File.WriteAllText(Path.Combine(folder, "bin", "setup.exe"), "MZ");
        var iso = Path.Combine(dir.Path, "import.iso");

        await IsoBuilder.BuildFromItemsAsync([file, folder], iso);

        await using var stream = File.OpenRead(iso);
        using var reader = new CDReader(stream, joliet: true);
        Assert.True(reader.FileExists(@"校正資料.csv"));
        Assert.True(reader.FileExists(@"SynerJY\bin\setup.exe"));
    }

    [Fact]
    public async Task RejectsNamesTooLongForJoliet()
    {
        using var dir = new TempDir();
        var src = Path.Combine(dir.Path, "src");
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, new string('長', 70) + ".txt"), "");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => IsoBuilder.BuildFromFolderAsync(src, Path.Combine(dir.Path, "x.iso")));
        Assert.Contains("檔名超過", ex.Message);
    }

    [Theory]
    [InlineData("光學軟體", "XPOPTICAL")]
    [InlineData("SynerJY v3.5", "SYNERJY_V3_5")]
    public void VolumeLabelIsSanitized(string name, string expected) =>
        Assert.Equal(expected, IsoBuilder.VolumeLabel(name));
}
