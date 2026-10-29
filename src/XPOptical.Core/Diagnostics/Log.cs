namespace XPOptical.Core.Diagnostics;

public interface ILog
{
    void Info(string message);
    void Warn(string message);
    void Error(string message, Exception? ex = null);
}

/// <summary>寫入文字檔的紀錄，供使用者回報問題時附上。</summary>
public sealed class FileLog : ILog
{
    private readonly string _path;
    private readonly object _gate = new();

    public FileLog(string path)
    {
        _path = path;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    }

    public void Info(string message) => Write("INFO", message);
    public void Warn(string message) => Write("WARN", message);
    public void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message}: {ex}");

    private void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}";
        lock (_gate)
        {
            try { File.AppendAllText(_path, line); }
            catch (IOException) { /* 紀錄失敗不應影響主流程 */ }
        }
    }
}

public sealed class NullLog : ILog
{
    public static readonly NullLog Instance = new();
    public void Info(string message) { }
    public void Warn(string message) { }
    public void Error(string message, Exception? ex = null) { }
}
