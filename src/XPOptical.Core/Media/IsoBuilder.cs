using DiscUtils.Iso9660;

namespace XPOptical.Core.Media;

/// <summary>
/// 把 Windows 上的資料夾做成光碟映像，放進 XP 的光碟機。XP 不連網，這是把安裝檔送進去最簡單、
/// 也最安全（XP 只能讀）的方式。使用 Joliet 延伸格式，中文長檔名在 XP 裡可正常顯示。
/// </summary>
public static class IsoBuilder
{
    /// <summary>Joliet 規格的檔名長度上限。</summary>
    public const int MaxNameLength = 64;

    /// <summary>把資料夾的「內容」放在光碟根目錄（用於「匯入資料夾」）。</summary>
    public static Task BuildFromFolderAsync(string folder, string outputIso, CancellationToken ct = default)
    {
        var root = Path.GetFullPath(folder);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException($"找不到資料夾：{folder}");
        var entries = Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
            .Select(p => (Source: p, IsoPath: ToIsoPath(Path.GetRelativePath(root, p))));
        return BuildAsync(entries, VolumeLabel(Path.GetFileName(root)), outputIso, ct);
    }

    /// <summary>把拖進來的檔案與資料夾放在光碟根目錄（資料夾保留名稱）。</summary>
    public static Task BuildFromItemsAsync(IReadOnlyList<string> items, string outputIso, CancellationToken ct = default)
    {
        var entries = new List<(string, string)>();
        foreach (var item in items.Select(Path.GetFullPath))
        {
            var name = Path.GetFileName(item.TrimEnd(Path.DirectorySeparatorChar));
            entries.Add((item, name));
            if (Directory.Exists(item))
            {
                entries.AddRange(Directory.EnumerateFileSystemEntries(item, "*", SearchOption.AllDirectories)
                    .Select(p => (p, ToIsoPath(Path.Combine(name, Path.GetRelativePath(item, p))))));
            }
            else if (!File.Exists(item))
            {
                throw new FileNotFoundException($"找不到：{item}");
            }
        }
        var label = items.Count == 1 ? VolumeLabel(Path.GetFileNameWithoutExtension(items[0])) : "IMPORT";
        return BuildAsync(entries, label, outputIso, ct);
    }

    private static async Task BuildAsync(IEnumerable<(string Source, string IsoPath)> entries, string label,
        string outputIso, CancellationToken ct)
    {
        var list = entries.ToList();
        var tooLong = list.Where(e => Path.GetFileName(e.Source).Length > MaxNameLength)
            .Select(e => e.IsoPath).Take(5).ToList();
        if (tooLong.Count > 0)
        {
            throw new InvalidOperationException(
                $"以下檔名超過 {MaxNameLength} 個字，光碟格式無法容納，請先改短：\n" + string.Join("\n", tooLong));
        }

        var builder = new CDBuilder { UseJoliet = true, VolumeIdentifier = label };
        foreach (var (source, isoPath) in list)
        {
            if (Directory.Exists(source)) builder.AddDirectory(isoPath);
            else builder.AddFile(isoPath, source);
        }

        var tmp = outputIso + ".building";
        await Task.Run(() => builder.Build(tmp), ct);
        File.Move(tmp, outputIso, overwrite: true);
    }

    private static string ToIsoPath(string relative) => relative.Replace(Path.DirectorySeparatorChar, '\\');

    /// <summary>ISO 9660 卷標只能用大寫英數與底線，最多 32 字。</summary>
    internal static string VolumeLabel(string name)
    {
        var chars = name.ToUpperInvariant().Select(c => c is >= 'A' and <= 'Z' or >= '0' and <= '9' ? c : '_').ToArray();
        var label = new string(chars).Trim('_');
        if (label.Length == 0) label = "XPOPTICAL";
        return label.Length > 32 ? label[..32] : label;
    }
}
