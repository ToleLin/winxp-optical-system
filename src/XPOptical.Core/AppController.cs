using XPOptical.Core.Diagnostics;
using XPOptical.Core.Profiles;
using XPOptical.Core.Qemu;
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

    public VmState VmState => Vm?.State ?? VmState.Stopped;
    public bool IsRunning => VmState == VmState.Running;
    public bool CanResume => Store.ReadSavedState() is not null;

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

    /// <summary>從 XP 安裝光碟開始安裝。會清除既有的 XP 環境。</summary>
    public async Task StartInstallAsync(string isoPath, CancellationToken ct = default)
    {
        var vm = RequireVm();
        Store.CreateBlankBaseDisk(Engine!.QemuImg);
        InstallMode = true;
        try
        {
            await vm.StartAsync(new VmLaunchSpec
            {
                SystemDisk = Store.BaseDisk,
                RamMB = Settings.EffectiveRamMB(Profile),
                FirmwareDir = Engine.FirmwareDir,
                CdromImage = isoPath,
                BootFromCdrom = true,
            }, allowResume: false, ct);
        }
        catch
        {
            InstallMode = false;
            throw;
        }
        Changed?.Invoke();
    }

    /// <summary>使用者確認 XP 已安裝完成。若 XP 還在執行，先要求關機。</summary>
    public async Task<CloseOutcome> CompleteInstallAsync(CancellationToken ct = default)
    {
        if (IsRunning && !await RequireVm().ShutdownAsync(ShutdownTimeout, ct)) return CloseOutcome.ShutdownTimedOut;
        Store.MarkInstalled();
        InstallMode = false;
        Changed?.Invoke();
        return CloseOutcome.Closed;
    }

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
            catch (Exception ex) when (ex is not OperationCanceledException)
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
