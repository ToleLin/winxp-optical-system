namespace XPOptical.Core;

/// <summary>
/// 免安裝資料夾的路徑配置。程式所在資料夾可寫入時，所有資料都放在旁邊（可整包複製到別台電腦）；
/// 否則（例如放在 Program Files）改放到使用者的 LocalAppData。
/// </summary>
public sealed class AppPaths
{
    public AppPaths(string appDir, string? dataRoot = null)
    {
        AppDir = appDir;
        DataRoot = dataRoot ?? (IsWritable(appDir)
            ? appDir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XPOptical"));
    }

    public string AppDir { get; }
    public string DataRoot { get; }

    public string EngineDir => Path.Combine(AppDir, "engine");
    public string DriversDir => Path.Combine(AppDir, "drivers");
    public string ProfilesDir => Path.Combine(AppDir, "profiles");
    public string DefaultProfile => Path.Combine(ProfilesDir, "ihr320.json");

    public string VmDir => Path.Combine(DataRoot, "vm");
    public string LogsDir => Path.Combine(DataRoot, "logs");
    public string SettingsFile => Path.Combine(DataRoot, "settings.json");

    private static bool IsWritable(string dir)
    {
        try
        {
            var probe = Path.Combine(dir, $".write-test-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
