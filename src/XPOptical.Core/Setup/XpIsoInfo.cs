using DiscUtils.Iso9660;

namespace XPOptical.Core.Setup;

public enum XpEdition { Unknown, Home, Professional }

/// <summary>
/// 從 XP 安裝光碟根目錄的標記檔判斷版本：WIN51IP = 專業版、WIN51IC = 家用版，
/// 副檔名 .SP1/.SP2/.SP3 表示 Service Pack。
/// <paramref name="HasOwnAnswerFile"/>：光碟的 I386 內已有 WINNT.SIF（修改過的光碟常見），
/// XP 安裝程式會優先使用它，本程式的自動安裝設定與完成偵測就不會生效。
/// </summary>
public sealed record XpIsoInfo(bool IsXp, XpEdition Edition, int ServicePack, bool HasOwnAnswerFile = false)
{
    /// <summary>微軟正式發行到 SP3，之後的「SP4」都是第三方修改版。</summary>
    public const int LastOfficialServicePack = 3;

    /// <summary>看起來不是微軟原版光碟時的提醒；原版為 null。</summary>
    public string? Warning
    {
        get
        {
            var reasons = new List<string>();
            if (IsXp && ServicePack > LastOfficialServicePack) reasons.Add($"微軟沒有發行過 SP{ServicePack}，這是修改過的光碟");
            if (IsXp && HasOwnAnswerFile) reasons.Add("光碟內含自己的自動安裝設定（I386\\WINNT.SIF）");
            return reasons.Count == 0 ? null
                : $"注意：{string.Join("；", reasons)}。全自動安裝可能停在某個畫面或無法完成，建議改用微軟原版光碟（例如 SP3）。";
        }
    }

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
        var ownSif = reader.DirectoryExists("\\I386") && reader.GetFiles("\\I386")
            .Any(f => f[(f.LastIndexOf('\\') + 1)..].Split(';')[0].Equals("WINNT.SIF", StringComparison.OrdinalIgnoreCase));
        return new XpIsoInfo(true, edition, sp, ownSif);
    }
}
