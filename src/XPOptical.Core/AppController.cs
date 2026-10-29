using XPOptical.Core.Diagnostics;
using XPOptical.Core.Media;
using XPOptical.Core.Profiles;
using XPOptical.Core.Qemu;
using XPOptical.Core.Setup;
using XPOptical.Core.Usb;
using XPOptical.Core.Vm;

namespace XPOptical.Core;

public enum CloseOutcome
{
    /// <summary>已休眠或已關機，可以結束程式。</summary>
    Closed,
    /// <summary>XP 沒有回應關機要求，由使用者決定是否強制關閉。</summary>
    ShutdownTimedOut,
}

/// <summary>
/// 把執行引擎、休眠檔管理與 USB 接入串在一起，提供給介面使用的單一入口。
/// 不依賴任何介面框架，可在測試中直接驅動。
/// </summary>
public sealed class AppController : IAsyncDisposable
{
    public static TimeSpan ShutdownTimeout { get; set; } = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(2);

    private readonly ILog _log;
    private CancellationTokenSource? _scanCts;
    private Task _scanLoop = Task.CompletedTask;

    public AppController(AppPaths paths, IUsbEnumerator usbEnumerator, ILog log)
    {
        Paths = paths;
        _log = log;
        Engine = EngineLocator.Find(paths.EngineDir);
        Profile = File.Exists(paths.DefaultProfile) ? DeviceProfile.Load(paths.DefaultProfile) : new DeviceProfile();
        Settings = AppSettings.Load(paths.SettingsFile);
        Store = new StateStore(paths.VmDir);
        Usb = new UsbAttachManager(Engine?.UsbRedirect, usbEnumerator, log);
        Usb.SetRules(Settings.EffectiveUsbRules(Profile));
        Usb.StatusChanged += () => Changed?.Invoke();
        if (Engine is not null)
        {
            Vm = new VmController(Engine, Store, log);
            Vm.StateChanged += OnVmStateChanged;
            Vm.EngineExited += code => EngineExited?.Invoke(code);
        }
    }

    public AppPaths Paths { get; }
    /// <summary>找不到執行引擎時為 null。</summary>
    public EngineLocator? Engine { get; }
    public DeviceProfile Profile { get; }
    public AppSettings Settings { get; }
    public StateStore Store { get; }
    public VmController? Vm { get; }
    public UsbAttachManager Usb { get; }

    /// <summary>正在從安裝光碟安裝 XP。</summary>
    public bool InstallMode { get; private set; }

    /// <summary>正在全自動安裝 XP（磁碟片上有 WINNT.SIF）。</summary>
    public bool UnattendedInstall => InstallMode && File.Exists(Store.SetupFloppy);

    /// <summary>程式資料夾中使用者自己放的 XP 安裝光碟（安裝檔會把旁邊的 xp.iso 複製到這裡）。</summary>
    public string BundledXpIso => Path.Combine(Paths.AppDir, "xp.iso");

    public VmState VmState => Vm?.State ?? VmState.Stopped;
    public bool IsRunning => VmState == VmState.Running;
    public bool CanResume => Store.ReadSavedState() is not null;
    /// <summary>有磁碟但尚未標記安裝完成（例如安裝到一半關閉了程式）。</summary>
    public bool HasUnfinishedInstall => !Store.IsInstalled && File.Exists(Store.BaseDisk);

    /// <summary>任何狀態改變（在背景執行緒觸發）。</summary>
    public event Action? Changed;
    /// <summary>執行引擎自行結束，例如在 XP 開始功能表選了關機。</summary>
    public event Action<int>? EngineExited;

    private VmController RequireVm() => Vm ?? throw new InvalidOperationException("找不到執行引擎（engine 資料夾）");

    /// <summary>開啟 XP：有可用的休眠檔就恢復，否則冷開機。</summary>
    public async Task<VmStartResult> StartAsync(CancellationToken ct = default)
    {
        var vm = RequireVm();
        if (!Store.IsInstalled) throw new InvalidOperationException("尚未建立 XP 環境");
        Store.EnsureSystemDisk(Engine!.QemuImg);
        InstallMode = false;

        var result = await vm.StartAsync(CurrentSpec(), allowResume: true, ct);
        Usb.Bind(vm.Qmp!);
        StartScanLoop();
        return result;
    }

    /// <summary>
    /// 從 XP 安裝光碟開機安裝。<paramref name="freshDisk"/> 為 true 時建立新磁碟（清除既有的 XP 環境），
    /// false 時沿用先前未完成的安裝。
    /// </summary>
    public async Task StartInstallAsync(string isoPath, bool freshDisk = true, CancellationToken ct = default)
    {
        var vm = RequireVm();
        if (freshDisk)
        {
            Store.CreateBlankBaseDisk(Engine!.QemuImg);
            DeleteSetupFloppy();
        }
        else if (!HasUnfinishedInstall) throw new InvalidOperationException("找不到先前未完成的安裝");
        isoPath = await EnsureEngineCanReadAsync(isoPath, ct);
        InstallMode = true;
        try
        {
            await vm.StartAsync(new VmLaunchSpec
            {
                SystemDisk = Store.BaseDisk,
                RamMB = Settings.EffectiveRamMB(Profile),
                FirmwareDir = Engine!.FirmwareDir,
                CdromImage = isoPath,
                BootFromCdrom = true,
                // 繼續未完成的全自動安裝時，磁碟片仍需接上（安裝後腳本在磁碟片上）
                FloppyImage = File.Exists(Store.SetupFloppy) ? Store.SetupFloppy : null,
            }, allowResume: false, ct);
        }
        catch
        {
            InstallMode = false;
            throw;
        }
        Changed?.Invoke();
    }

    /// <summary>
    /// 全自動安裝 XP：建立新磁碟與含 WINNT.SIF 的磁碟片，從安裝光碟開機。
    /// 安裝程式會自己分割磁碟、輸入序號、重新開機，完成後第一次登入時自動關機；
    /// 之後呼叫 <see cref="TryCompleteUnattendedInstall"/> 確認完成。
    /// </summary>
    public async Task StartUnattendedInstallAsync(string isoPath, string productKey, CancellationToken ct = default)
    {
        if (UnattendedSetup.NormalizeProductKey(productKey) is null)
            throw new ArgumentException("產品序號格式不正確（應為 25 碼，例如 XXXXX-XXXXX-XXXXX-XXXXX-XXXXX）");
        var info = XpIsoInfo.Inspect(isoPath);
        if (!info.IsXp) throw new InvalidOperationException("選擇的檔案不是 Windows XP 安裝光碟。");

        RequireVm();
        Store.CreateBlankBaseDisk(Engine!.QemuImg);
        UnattendedSetup.CreateFloppy(Store.SetupFloppy, productKey);
        _log.Info($"開始全自動安裝：{info.Description}");
        try
        {
            await StartInstallAsync(isoPath, freshDisk: false, ct);
        }
        catch
        {
            DeleteSetupFloppy();
            throw;
        }
    }

    /// <summary>
    /// 執行引擎在安裝模式下結束時呼叫：若是全自動安裝且 XP 已寫入完成標記，
    /// 標記安裝完成並清除磁碟片（含產品序號）。回傳是否已完成。
    /// </summary>
    public bool TryCompleteUnattendedInstall()
    {
        if (!InstallMode || VmState != VmState.Stopped) return false;
        bool done;
        try { done = UnattendedSetup.IsCompleted(Store.SetupFloppy); }
        catch (Exception ex) when (ex is IOException or InvalidDataException) { done = false; }
        if (!done) return false;

        Store.MarkInstalled();
        InstallMode = false;
        DeleteSetupFloppy();
        var isoCopy = Path.Combine(Store.VmDir, "install.iso");
        if (File.Exists(isoCopy)) File.Delete(isoCopy);
        _log.Info("全自動安裝完成");
        Changed?.Invoke();
        return true;
    }

    private void DeleteSetupFloppy()
    {
        try { if (File.Exists(Store.SetupFloppy)) File.Delete(Store.SetupFloppy); }
        catch (IOException ex) { _log.Warn($"無法刪除安裝磁碟片：{ex.Message}"); }
    }

    /// <summary>
    /// 安裝光碟放在含中文的路徑（例如「下載」資料夾在中文帳號下）時，執行引擎讀不到；
    /// 這種情況把它複製到 vm 資料夾。
    /// </summary>
    private async Task<string> EnsureEngineCanReadAsync(string isoPath, CancellationToken ct)
    {
        if (SafePath.TryMakeSafe(Path.GetFullPath(isoPath), Store.VmDir) is not null) return isoPath;
        var copy = Path.Combine(Store.VmDir, "install.iso");
        if (!string.Equals(Path.GetFullPath(isoPath), copy, StringComparison.OrdinalIgnoreCase))
        {
            _log.Info($"安裝光碟路徑含中文，複製到 {copy}");
            await using var src = File.OpenRead(isoPath);
            await using var dst = File.Create(copy);
            await src.CopyToAsync(dst, ct);
        }
        return copy;
    }

    /// <summary>使用者確認 XP 已安裝完成。若 XP 還在執行，先要求關機。</summary>
    public async Task<CloseOutcome> CompleteInstallAsync(CancellationToken ct = default)
    {
        if (IsRunning && !await RequireVm().ShutdownAsync(ShutdownTimeout, ct)) return CloseOutcome.ShutdownTimedOut;
        Store.MarkInstalled();
        InstallMode = false;
        DeleteSetupFloppy();
        Changed?.Invoke();
        return CloseOutcome.Closed;
    }

    /// <summary>「放入檔案」產生的光碟映像。</summary>
    public string ImportIsoPath => Path.Combine(Store.VmDir, "import.iso");

    /// <summary>把資料夾做成光碟放進 XP（例如光學軟體的安裝檔）。</summary>
    public async Task InsertFolderAsync(string folder, CancellationToken ct = default)
    {
        var vm = RequireRunningVm();
        await vm.EjectMediaAsync(ct); // 釋放舊的 import.iso 才能覆寫
        await IsoBuilder.BuildFromFolderAsync(folder, ImportIsoPath, ct);
        await vm.InsertMediaAsync(ImportIsoPath, ct);
    }

    /// <summary>把光碟映像檔放進 XP。路徑含中文時先複製到 vm 資料夾。</summary>
    public async Task InsertIsoAsync(string isoPath, CancellationToken ct = default)
    {
        var vm = RequireRunningVm();
        await vm.EjectMediaAsync(ct);
        var path = isoPath;
        if (SafePath.TryMakeSafe(Path.GetFullPath(isoPath), Store.VmDir) is null)
        {
            await using var src = File.OpenRead(isoPath);
            await using var dst = File.Create(ImportIsoPath);
            await src.CopyToAsync(dst, ct);
            path = ImportIsoPath;
        }
        await vm.InsertMediaAsync(path, ct);
    }

    /// <summary>
    /// 拖進視窗的項目：單一 ISO 直接放入；單一資料夾放入其內容；其他（檔案、多個項目）做成一片光碟。
    /// </summary>
    public async Task InsertItemsAsync(IReadOnlyList<string> items, CancellationToken ct = default)
    {
        if (items.Count == 1 && File.Exists(items[0])
            && Path.GetExtension(items[0]).Equals(".iso", StringComparison.OrdinalIgnoreCase))
        {
            await InsertIsoAsync(items[0], ct);
        }
        else if (items.Count == 1 && Directory.Exists(items[0]))
        {
            await InsertFolderAsync(items[0], ct);
        }
        else
        {
            var vm = RequireRunningVm();
            await vm.EjectMediaAsync(ct);
            await IsoBuilder.BuildFromItemsAsync(items, ImportIsoPath, ct);
            await vm.InsertMediaAsync(ImportIsoPath, ct);
        }
    }

    public Task EjectMediaAsync(CancellationToken ct = default) => RequireRunningVm().EjectMediaAsync(ct);

    private VmController RequireRunningVm() =>
        IsRunning ? Vm! : throw new InvalidOperationException("請先開啟 XP");

    public async Task ImportImageAsync(string sourcePath, IProgress<double>? progress, CancellationToken ct = default)
    {
        if (Engine is null) throw new InvalidOperationException("找不到執行引擎（engine 資料夾）");
        if (VmState != VmState.Stopped) throw new InvalidOperationException("請先關閉 XP");
        await Store.ImportBaseDiskAsync(Engine.QemuImg, sourcePath, progress, ct);
        Changed?.Invoke();
    }

    /// <summary>
    /// 關閉主視窗時呼叫：依設定休眠或正常關機。安裝模式下強制關閉（安裝中無法休眠）。
    /// </summary>
    public async Task<CloseOutcome> CloseAsync(CancellationToken ct = default)
    {
        if (Vm is null || VmState == VmState.Stopped) return CloseOutcome.Closed;
        if (InstallMode)
        {
            await Vm.PowerOffAsync();
            return CloseOutcome.Closed;
        }

        await SuspendUsbAsync(ct);
        if (Settings.EffectiveCloseAction(Profile) == CloseAction.Hibernate)
        {
            try
            {
                await Vm.HibernateAsync(ct);
                return CloseOutcome.Closed;
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not HibernateIncompleteException)
            {
                // XP 會繼續執行；恢復 USB 自動接入，再把錯誤交給介面
                ResumeUsb();
                throw;
            }
        }
        return await ShutdownVmAsync(ct);
    }

    /// <summary>不論設定為何，要求 XP 正常關機。</summary>
    public async Task<CloseOutcome> ShutdownAsync(CancellationToken ct = default)
    {
        if (!IsRunning) return CloseOutcome.Closed;
        await SuspendUsbAsync(ct);
        return await ShutdownVmAsync(ct);
    }

    private async Task<CloseOutcome> ShutdownVmAsync(CancellationToken ct)
    {
        if (await RequireVm().ShutdownAsync(ShutdownTimeout, ct)) return CloseOutcome.Closed;
        ResumeUsb();
        return CloseOutcome.ShutdownTimedOut;
    }

    /// <summary>停止自動接入並把 USB 裝置從 XP 移除（休眠/關機前）。</summary>
    private async Task SuspendUsbAsync(CancellationToken ct)
    {
        _scanCts?.Cancel();
        try { await _scanLoop; } catch (OperationCanceledException) { }
        Usb.Pause();
        await Usb.DetachAllAsync(ct);
    }

    private void ResumeUsb()
    {
        Usb.Resume();
        if (IsRunning) StartScanLoop();
    }

    /// <summary>強制關閉 XP（未儲存的資料會遺失）。</summary>
    public async Task PowerOffAsync()
    {
        if (Vm is null) return;
        await SuspendUsbAsync(CancellationToken.None);
        await Vm.PowerOffAsync();
    }

    /// <summary>Windows 通知裝置有變動時呼叫，立即重新掃描。</summary>
    public void NotifyDevicesChanged()
    {
        if (IsRunning) _ = Usb.ScanAsync();
    }

    public void SaveSettings()
    {
        Settings.Save(Paths.SettingsFile);
        Usb.SetRules(Settings.EffectiveUsbRules(Profile));
        NotifyDevicesChanged();
    }

    private VmLaunchSpec CurrentSpec() => new()
    {
        SystemDisk = Store.SystemDisk,
        RamMB = Settings.EffectiveRamMB(Profile),
        FirmwareDir = Engine!.FirmwareDir,
    };

    private void StartScanLoop()
    {
        _scanCts?.Cancel();
        var cts = _scanCts = new CancellationTokenSource();
        _scanLoop = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(ScanInterval);
            try
            {
                do
                {
                    try { await Usb.ScanAsync(cts.Token); }
                    catch (Exception ex) when (ex is not OperationCanceledException) { _log.Error("USB 掃描失敗", ex); }
                } while (await timer.WaitForNextTickAsync(cts.Token));
            }
            catch (OperationCanceledException) { }
        });
    }

    private void OnVmStateChanged(VmState state)
    {
        if (state == VmState.Stopped)
        {
            _scanCts?.Cancel();
            _ = Usb.UnbindAsync();
            try { if (File.Exists(ImportIsoPath)) File.Delete(ImportIsoPath); }
            catch (IOException) { }
        }
        Changed?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        _scanCts?.Cancel();
        try { await _scanLoop; } catch (OperationCanceledException) { }
        await Usb.DisposeAsync();
        if (Vm is not null) await Vm.DisposeAsync();
    }
}
