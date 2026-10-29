using DiscUtils.Iso9660;

namespace XPOptical.Core.Setup;

public enum XpEdition { Unknown, Home, Professional }

/// <summary>
/// 從 XP 安裝光碟根目錄的標記檔判斷版本：WIN51IP = 專業版、WIN51IC = 家用版，
/// 副檔名 .SP1/.SP2/.SP3 表示 Service Pack。
/// </summary>
public sealed record XpIsoInfo(bool IsXp, XpEdition Edition, int ServicePack)
{
    public string Description => !IsXp
        ? "這不是 Windows XP 安裝光碟"
        : $"Windows XP {(Edition switch { XpEdition.Home => "家用版", XpEdition.Professional => "專業版", _ => "" })}"
          + (ServicePack > 0 ? $" SP{ServicePack}" : "（無 Service Pack）");

    public static XpIsoInfo Inspect(string isoPath)
    {
        using var stream = File.OpenRead(isoPath);
        using var reader = new CDReader(stream, joliet: true);
        // ISO 9660 的檔名可能帶有版本號（例如 WIN51IP.SP3;1）
        var names = reader.GetFiles("\\").Select(f => f[(f.LastIndexOf('\\') + 1)..].Split(';')[0].ToUpperInvariant()).ToList();

        var tags = names.Where(n => n.StartsWith("WIN51", StringComparison.Ordinal)).ToList();
        if (tags.Count == 0) return new XpIsoInfo(false, XpEdition.Unknown, 0);

        var edition = tags.Any(t => t.StartsWith("WIN51IP", StringComparison.Ordinal)) ? XpEdition.Professional
            : tags.Any(t => t.StartsWith("WIN51IC", StringComparison.Ordinal)) ? XpEdition.Home
            : XpEdition.Unknown;
        var sp = tags.Select(t => t.LastIndexOf(".SP", StringComparison.Ordinal) is var i and >= 0
                && int.TryParse(t[(i + 3)..], out var n) ? n : 0).DefaultIfEmpty(0).Max();
        return new XpIsoInfo(true, edition, sp);
    }
}
