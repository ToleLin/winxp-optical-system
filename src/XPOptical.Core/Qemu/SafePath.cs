using System.Runtime.InteropServices;
using System.Text;

namespace XPOptical.Core.Qemu;

public sealed class UnsafePathException(string path) : Exception(
    $"這個路徑含有中文或特殊字元，執行引擎無法使用：\n{path}\n\n" +
    "請把 XPOptical 資料夾（或該檔案）移到只含英文與數字的位置，例如 C:\\XPOptical。")
{
    public string PathValue { get; } = path;
}

/// <summary>
/// QEMU 在 Windows 上以系統 ANSI 字碼頁讀取命令列與自身路徑（不是 Unicode），
/// 中文路徑會變成亂碼甚至被誤判成選項分隔符。傳給 QEMU 的路徑一律轉成純英數：
/// 先試相對於工作目錄的路徑，再試 Windows 的 8.3 短檔名。
/// </summary>
public static class SafePath
{
    public static bool IsAscii(string s) => s.All(c => c < 128);

    public static string? TryMakeSafe(string path, string workingDir)
    {
        if (IsAscii(path)) return path;
        var relative = Path.GetRelativePath(workingDir, path);
        if (IsAscii(relative)) return relative;
        if (OperatingSystem.IsWindows() && GetShortPath(path) is { } shortPath && IsAscii(shortPath)) return shortPath;
        return null;
    }

    public static string MakeSafe(string path, string workingDir) =>
        TryMakeSafe(path, workingDir) ?? throw new UnsafePathException(path);

    private static string? GetShortPath(string path)
    {
        var buffer = new StringBuilder(1024);
        var len = GetShortPathNameW(path, buffer, buffer.Capacity);
        return len > 0 && len < buffer.Capacity ? buffer.ToString() : null;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetShortPathNameW(string longPath, StringBuilder shortPath, int bufferLength);
}
