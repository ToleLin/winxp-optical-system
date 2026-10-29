using System.IO.Compression;
using System.Text;
using XPOptical.Core.Usb;

namespace XPOptical.Core.Diagnostics;

/// <summary>
/// 一鍵產生問題回報：紀錄檔、設定、vm 資料夾的狀態檔與環境資訊壓成一個 zip，方便傳給開發者。
/// 不包含 XP 磁碟映像與休眠檔（體積大，且可能含個人資料）。
/// </summary>
public static class ProblemReport
{
    /// <summary>每個紀錄檔最多保留結尾這麼多位元組。</summary>
    internal const int MaxLogBytes = 2 * 1024 * 1024;
    private const int MaxLogFiles = 5;
    private static readonly string[] VmStateFiles = ["vm.json", "state.json"];

    public static string Create(AppPaths paths, string outputDir, IEnumerable<(string Name, string Value)> environment,
        IEnumerable<UsbDeviceInfo> usbDevices)
    {
        Directory.CreateDirectory(outputDir);
        // 檔名用英數：透過通訊軟體、email 傳送或在不同語系的電腦解壓縮時不會變成亂碼
        var zipPath = Path.Combine(outputDir, $"XPOptical-report-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);

        AddText(zip, "summary.txt", BuildSummary(paths, environment, usbDevices));

        if (Directory.Exists(paths.LogsDir))
        {
            foreach (var log in new DirectoryInfo(paths.LogsDir).GetFiles()
                         .OrderByDescending(f => f.LastWriteTimeUtc).Take(MaxLogFiles))
            {
                AddTail(zip, $"logs/{log.Name}", log.FullName);
            }
        }
        AddIfExists(zip, "settings.json", paths.SettingsFile);
        AddIfExists(zip, "profiles/ihr320.json", paths.DefaultProfile);
        foreach (var name in VmStateFiles) AddIfExists(zip, $"vm/{name}", Path.Combine(paths.VmDir, name));
        AddIfExists(zip, "engine/engine-manifest.json", Path.Combine(paths.EngineDir, "engine-manifest.json"));
        return zipPath;
    }

    private static string BuildSummary(AppPaths paths, IEnumerable<(string Name, string Value)> environment,
        IEnumerable<UsbDeviceInfo> usbDevices)
    {
        var sb = new StringBuilder();
        sb.AppendLine("XP 光學系統 問題回報");
        sb.AppendLine($"產生時間：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine();
        sb.AppendLine("[環境]");
        foreach (var (name, value) in environment) sb.AppendLine($"{name}：{value}");
        sb.AppendLine($"程式資料夾：{paths.AppDir}");
        sb.AppendLine($"資料資料夾：{paths.DataRoot}");
        sb.AppendLine();
        sb.AppendLine("[目前接在電腦上的 USB 裝置]");
        foreach (var d in usbDevices.OrderBy(d => d.IdText))
            sb.AppendLine($"{d.IdText}  {d.Description ?? "（無名稱）"}  {d.InstanceId}");
        sb.AppendLine();
        sb.AppendLine("[vm 資料夾]");
        if (Directory.Exists(paths.VmDir))
        {
            foreach (var f in new DirectoryInfo(paths.VmDir).GetFiles().OrderBy(f => f.Name))
                sb.AppendLine($"{f.Name}  {f.Length / 1048576.0:F1} MB  {f.LastWriteTime:yyyy-MM-dd HH:mm}");
        }
        else
        {
            sb.AppendLine("（尚未建立）");
        }
        return sb.ToString();
    }

    private static void AddText(ZipArchive zip, string entryName, string text)
    {
        using var w = new StreamWriter(zip.CreateEntry(entryName).Open(), new UTF8Encoding(true));
        w.Write(text);
    }

    private static void AddIfExists(ZipArchive zip, string entryName, string path)
    {
        if (File.Exists(path)) AddTail(zip, entryName, path);
    }

    /// <summary>加入檔案（過大時只取結尾）。檔案正被寫入也能讀取。</summary>
    private static void AddTail(ZipArchive zip, string entryName, string path)
    {
        try
        {
            using var src = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (src.Length > MaxLogBytes) src.Seek(-MaxLogBytes, SeekOrigin.End);
            using var dst = zip.CreateEntry(entryName).Open();
            src.CopyTo(dst);
        }
        catch (IOException ex)
        {
            AddText(zip, entryName + ".read-error.txt", ex.Message);
        }
    }
}
