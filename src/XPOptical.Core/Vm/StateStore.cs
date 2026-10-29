using System.Diagnostics;
using System.Text.Json;
using XPOptical.Core.Profiles;

namespace XPOptical.Core.Vm;

/// <summary>vm 資料夾的整體狀態。</summary>
public sealed class VmMeta
{
    /// <summary>每次 XP 開始執行（冷開機或恢復）就加一；休眠檔記錄存檔當下的世代。</summary>
    public long Generation { get; set; }
    /// <summary>XP 已安裝完成（base.qcow2 可作為唯讀的出廠狀態）。</summary>
    public bool Installed { get; set; }
}

/// <summary>休眠檔的中繼資料。</summary>
public sealed class SavedStateMeta
{
    public long Generation { get; set; }
    public string Signature { get; set; } = "";
    public string EngineVersion { get; set; } = "";
    public DateTime SavedAt { get; set; }
}

/// <summary>
/// 管理 vm 資料夾：base.qcow2（唯讀出廠狀態）、system.qcow2（差異）、state.bin（休眠檔）。
/// 一致性規則：XP 一旦開始執行，舊的休眠檔立即作廢，避免把舊記憶體狀態套到已變更的磁碟上。
/// </summary>
public sealed class StateStore
{
    public StateStore(string vmDir)
    {
        VmDir = vmDir;
        Directory.CreateDirectory(vmDir);
        Meta = ReadJson<VmMeta>(MetaFile) ?? new VmMeta();
    }

    public string VmDir { get; }
    public string BaseDisk => Path.Combine(VmDir, "base.qcow2");
    public string SystemDisk => Path.Combine(VmDir, "system.qcow2");
    public string StateFile => Path.Combine(VmDir, "state.bin");
    public string StateTempFile => Path.Combine(VmDir, "state.bin.tmp");
    private string StateMetaFile => Path.Combine(VmDir, "state.json");
    private string MetaFile => Path.Combine(VmDir, "vm.json");

    public VmMeta Meta { get; }

    public bool IsInstalled => Meta.Installed && File.Exists(BaseDisk);

    public SavedStateMeta? ReadSavedState() =>
        File.Exists(StateFile) ? ReadJson<SavedStateMeta>(StateMetaFile) : null;

    public bool CanResume(string signature, string engineVersion)
    {
        var saved = ReadSavedState();
        return saved is not null
               && saved.Generation == Meta.Generation
               && saved.Signature == signature
               && saved.EngineVersion == engineVersion;
    }

    /// <summary>XP 即將開始執行（磁碟即將被修改）：作廢休眠檔並推進世代。</summary>
    public void BeginRun()
    {
        InvalidateSavedState();
        Meta.Generation++;
        SaveMeta();
    }

    /// <summary>
    /// 刪除休眠檔。先刪中繼資料（沒有它就不會恢復）；檔案若暫時被鎖住則略過，
    /// 由 <see cref="BeginRun"/> 推進世代確保它不會再被使用。
    /// </summary>
    public void InvalidateSavedState()
    {
        foreach (var f in new[] { StateMetaFile, StateFile, StateTempFile })
        {
            try { if (File.Exists(f)) File.Delete(f); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>存檔完成後呼叫：暫存檔轉正，並記錄世代、組成指紋與引擎版本。</summary>
    public void CommitSavedState(string signature, string engineVersion)
    {
        File.Move(StateTempFile, StateFile, overwrite: true);
        WriteJson(StateMetaFile, new SavedStateMeta
        {
            Generation = Meta.Generation,
            Signature = signature,
            EngineVersion = engineVersion,
            SavedAt = DateTime.Now,
        });
    }

    public void MarkInstalled()
    {
        Meta.Installed = true;
        SaveMeta();
    }

    /// <summary>建立空白磁碟，供從安裝光碟安裝 XP。會清除既有的 XP 環境，呼叫前須經使用者確認。</summary>
    public void CreateBlankBaseDisk(string qemuImg, int sizeGB = 10)
    {
        InvalidateSavedState();
        if (File.Exists(SystemDisk)) File.Delete(SystemDisk);
        if (File.Exists(BaseDisk)) File.Delete(BaseDisk);
        RunQemuImg(qemuImg, "create", "-f", "qcow2", Path.GetFileName(BaseDisk), $"{sizeGB}G");
        Meta.Installed = false;
        SaveMeta();
    }

    /// <summary>
    /// 確保差異磁碟存在。使用相對路徑記錄 base.qcow2，整個資料夾搬移後仍可使用。
    /// </summary>
    public void EnsureSystemDisk(string qemuImg)
    {
        if (File.Exists(SystemDisk)) return;
        RunQemuImg(qemuImg, "create", "-f", "qcow2", "-b", Path.GetFileName(BaseDisk), "-F", "qcow2",
            Path.GetFileName(SystemDisk));
    }

    /// <summary>
    /// 匯入既有的 XP 磁碟映像（qcow2、vhd、vmdk、raw…）並轉成 base.qcow2。
    /// <paramref name="progress"/> 回報 0–100。
    /// </summary>
    public async Task ImportBaseDiskAsync(string qemuImg, string source, IProgress<double>? progress,
        CancellationToken ct)
    {
        var tmp = BaseDisk + ".importing";
        if (File.Exists(tmp)) File.Delete(tmp);
        // 來源在中文路徑時，先在 vm 資料夾建立英文檔名的連結（同一磁碟機不佔空間），不行才複製
        var full = Path.GetFullPath(source);
        var safeSource = Qemu.SafePath.TryMakeSafe(full, VmDir);
        string? staged = null;
        if (safeSource is null)
        {
            staged = Path.Combine(VmDir, "import-source" + Path.GetExtension(full));
            if (File.Exists(staged)) File.Delete(staged);
            if (!TryCreateHardLink(staged, full)) File.Copy(full, staged);
            safeSource = Path.GetFileName(staged);
        }
        try
        {
            await ConvertAsync(qemuImg, safeSource, tmp, progress, ct);
        }
        finally
        {
            if (staged is not null && File.Exists(staged)) File.Delete(staged);
        }

        InvalidateSavedState();
        if (File.Exists(SystemDisk)) File.Delete(SystemDisk);
        File.Move(tmp, BaseDisk, overwrite: true);
        progress?.Report(100);
        MarkInstalled();
    }

    private async Task ConvertAsync(string qemuImg, string source, string tmp, IProgress<double>? progress,
        CancellationToken ct)
    {
        var psi = NewQemuImgStartInfo(qemuImg, "convert", "-p", "-O", "qcow2", source, Path.GetFileName(tmp));
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("無法啟動 qemu-img");
        var stderrTask = p.StandardError.ReadToEndAsync(ct);
        // qemu-img -p 以 "(12.34/100%)" 的格式回報進度，用 \r 分隔
        var buffer = new char[256];
        var pending = "";
        int n;
        while ((n = await p.StandardOutput.ReadAsync(buffer, ct)) > 0)
        {
            pending += new string(buffer, 0, n);
            var open = pending.LastIndexOf('(');
            var slash = open >= 0 ? pending.IndexOf("/100%", open, StringComparison.Ordinal) : -1;
            if (slash > open && double.TryParse(pending.AsSpan(open + 1, slash - open - 1),
                    System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture,
                    out var pct))
            {
                progress?.Report(pct);
                pending = "";
            }
        }
        await p.WaitForExitAsync(ct);
        var stderr = await stderrTask;
        if (p.ExitCode != 0)
        {
            if (File.Exists(tmp)) File.Delete(tmp);
            throw new InvalidOperationException($"磁碟映像轉換失敗：{stderr.Trim()}");
        }
    }

    private static bool TryCreateHardLink(string link, string target) =>
        OperatingSystem.IsWindows() && CreateHardLinkW(link, target, IntPtr.Zero);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode,
        SetLastError = true)]
    private static extern bool CreateHardLinkW(string fileName, string existingFileName, IntPtr securityAttributes);

    private void RunQemuImg(string qemuImg, params string[] args)
    {
        using var p = Process.Start(NewQemuImgStartInfo(qemuImg, args))
                      ?? throw new InvalidOperationException("無法啟動 qemu-img");
        var stderr = p.StandardError.ReadToEnd();
        p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException($"qemu-img {args[0]} 失敗：{stderr.Trim()}");
    }

    private ProcessStartInfo NewQemuImgStartInfo(string qemuImg, params string[] args)
    {
        var psi = new ProcessStartInfo(qemuImg)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            WorkingDirectory = VmDir,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        return psi;
    }

    private void SaveMeta() => WriteJson(MetaFile, Meta);

    private static T? ReadJson<T>(string path) where T : class
    {
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json.Options); }
        catch (JsonException) { return null; }
    }

    private static void WriteJson<T>(string path, T value)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, Json.Options));
        File.Move(tmp, path, overwrite: true);
    }
}
