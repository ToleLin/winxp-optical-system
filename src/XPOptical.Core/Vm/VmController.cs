using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using XPOptical.Core.Diagnostics;
using XPOptical.Core.Qemu;

namespace XPOptical.Core.Vm;

public enum VmState { Stopped, Starting, Running, Saving, ShuttingDown }

public sealed record VmStartResult(bool Resumed, bool SoftwareEmulation, string? ResumeFailure);

/// <summary>XP 已停止且磁碟已保存，但記憶體狀態沒有完整存下來；下次開啟會冷開機。</summary>
public sealed class HibernateIncompleteException(Exception inner)
    : Exception("XP 的畫面狀態沒有完整保存，下次開啟會重新開機 XP（磁碟裡的資料已保存）。", inner);

/// <summary>XP 執行環境的生命週期：冷開機、從休眠檔恢復、休眠存檔、正常關機。</summary>
public sealed class VmController : IAsyncDisposable
{
    private static readonly TimeSpan QmpConnectTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MigrationTimeout = TimeSpan.FromMinutes(5);

    private readonly EngineLocator _engine;
    private readonly StateStore _store;
    private readonly ILog _log;
    private readonly object _gate = new();
    private QemuProcess? _process;
    private QmpClient? _qmp;
    private VmLaunchSpec? _spec;
    private VmState _state = VmState.Stopped;

    public VmController(EngineLocator engine, StateStore store, ILog log)
    {
        _engine = engine;
        _store = store;
        _log = log;
    }

    public VmState State => _state;
    public event Action<VmState>? StateChanged;

    /// <summary>執行引擎自行結束（XP 從開始功能表關機，或異常終止）。參數為結束代碼。</summary>
    public event Action<int>? EngineExited;

    public QmpClient? Qmp => _qmp;
    public int VncPort { get; private set; }
    public VmLaunchSpec? CurrentSpec => _spec;
    public bool SoftwareEmulation => _process?.FellBackToSoftwareEmulation ?? false;

    public async Task<VmStartResult> StartAsync(VmLaunchSpec spec, bool allowResume, CancellationToken ct = default)
    {
        if (_state != VmState.Stopped) throw new InvalidOperationException("XP 已在執行中");
        SetState(VmState.Starting);
        string? resumeFailure = null;
        try
        {
            var signature = QemuCommandBuilder.ComputeSignature(spec);
            if (allowResume && _store.CanResume(signature, _engine.Version))
            {
                try
                {
                    await LaunchAsync(spec, incoming: true, ct);
                    await ResumeFromStateAsync(ct);
                    SetState(VmState.Running);
                    return new VmStartResult(true, SoftwareEmulation, null);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log.Error("恢復休眠狀態失敗，改為冷開機", ex);
                    resumeFailure = ex.Message;
                    await TeardownAsync();
                }
            }
            else if (_store.ReadSavedState() is not null)
            {
                _log.Warn("休眠檔與目前的設定或引擎版本不符，改為冷開機");
            }

            // 冷開機：XP 一開始執行磁碟就會變動，舊休眠檔必須先作廢
            _store.BeginRun();
            await LaunchAsync(spec, incoming: false, ct);
            SetState(VmState.Running);
            return new VmStartResult(false, SoftwareEmulation, resumeFailure);
        }
        catch
        {
            await TeardownAsync();
            SetState(VmState.Stopped);
            throw;
        }
    }

    /// <summary>暫停 XP 並把記憶體狀態寫入休眠檔，然後結束執行引擎。失敗時讓 XP 繼續執行。</summary>
    public async Task HibernateAsync(CancellationToken ct = default)
    {
        var qmp = RequireRunning();
        if (_spec!.CdromImage is not null)
            throw new InvalidOperationException("光碟機中有光碟時無法休眠");

        SetState(VmState.Saving);
        using var receiveCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        StateReceiver? receiver = null;
        var started = DateTime.UtcNow;
        try
        {
            await qmp.ExecuteAsync("stop", ct: ct);
            if (File.Exists(_store.StateTempFile)) File.Delete(_store.StateTempFile);
            receiver = StateStream.ReceiveToFile(_store.StateTempFile, receiveCts.Token);
            await qmp.ExecuteAsync("migrate", new { channels = StateStream.SocketChannel(receiver.Port) }, ct);
            await WaitForMigrationAsync(qmp, ct);
            // 確定 QEMU 送出的資料都已讀完，再結束引擎（結束時連線可能被重設）
            await receiver.WaitForIdleAsync(TimeSpan.FromMilliseconds(300), MigrationTimeout, ct);
        }
        catch (Exception ex)
        {
            receiveCts.Cancel();
            if (receiver is not null) await receiver.Completion.ContinueWith(_ => { }, TaskScheduler.Default);
            _log.Error("休眠存檔失敗，XP 繼續執行", ex);
            try
            {
                await qmp.ExecuteAsync("cont", ct: CancellationToken.None);
                SetState(VmState.Running);
            }
            catch (Exception contEx)
            {
                _log.Error("無法讓 XP 繼續執行", contEx);
            }
            throw;
        }

        // 遷移已完成、磁碟已寫入。Windows 版 QEMU 不會主動關閉連線，結束引擎讓資料流結束
        await QuitAndWaitAsync();
        try
        {
            var bytes = await receiver.Completion.WaitAsync(TimeSpan.FromSeconds(30), ct);
            if (bytes == 0) throw new InvalidOperationException("休眠檔是空的");
            _store.CommitSavedState(QemuCommandBuilder.ComputeSignature(_spec), _engine.Version);
            _log.Info($"休眠存檔完成：{bytes / 1048576.0:F1} MB，{(DateTime.UtcNow - started).TotalSeconds:F1} 秒");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _store.InvalidateSavedState();
            _log.Error("休眠檔寫入不完整", ex);
            throw new HibernateIncompleteException(ex);
        }
    }

    /// <summary>要求 XP 正常關機（等同按下電源鍵）。逾時回傳 false，由呼叫端決定是否強制關閉。</summary>
    public async Task<bool> ShutdownAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        var qmp = RequireRunning();
        var process = _process!;
        SetState(VmState.ShuttingDown);
        await qmp.ExecuteAsync("system_powerdown", ct: ct);
        var finished = await Task.WhenAny(process.Exited, Task.Delay(timeout, ct));
        if (finished != process.Exited)
        {
            SetState(VmState.Running);
            return false;
        }
        await TeardownAsync();
        SetState(VmState.Stopped);
        return true;
    }

    /// <summary>強制關閉（等同拔電源）。磁碟內容保留，未存檔的資料會遺失。</summary>
    public async Task PowerOffAsync()
    {
        if (_state == VmState.Stopped) return;
        SetState(VmState.ShuttingDown);
        await QuitAndWaitAsync();
    }

    private async Task LaunchAsync(VmLaunchSpec spec, bool incoming, CancellationToken ct)
    {
        var qmpPort = PortAllocator.GetFreePort();
        var vncPort = PortAllocator.GetFreePort(QemuCommandBuilder.VncBasePort, QemuCommandBuilder.VncBasePort + 99);
        var process = QemuProcess.Start(_engine.QemuSystem,
            QemuCommandBuilder.Build(ToSafePaths(spec), new VmEndpoints(qmpPort, vncPort, incoming)),
            _store.VmDir, _log);
        lock (_gate)
        {
            _process = process;
            _spec = spec;
            VncPort = vncPort;
        }

        try
        {
            _qmp = await QmpClient.ConnectAsync(qmpPort, QmpConnectTimeout, ct, () => process.HasExited);
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
        {
            var detail = string.Join(Environment.NewLine, process.StandardError.TakeLast(5));
            throw new InvalidOperationException($"執行引擎啟動失敗。{detail}", ex);
        }
        _ = process.Exited.ContinueWith(t => OnEngineExited(process, t.Result), TaskScheduler.Default);
    }

    private async Task ResumeFromStateAsync(CancellationToken ct)
    {
        var qmp = _qmp!;
        var started = DateTime.UtcNow;
        var port = PortAllocator.GetFreePort();
        await qmp.ExecuteAsync("migrate-incoming", new { channels = StateStream.SocketChannel(port) }, ct);
        var send = StateStream.SendFileAsync(port, _store.StateFile, QmpConnectTimeout, ct);

        // 存檔前已先暫停，所以讀取完成後會停在 paused，不會自行開始執行
        var deadline = DateTime.UtcNow + MigrationTimeout;
        while (true)
        {
            if (_process!.HasExited) throw new InvalidOperationException("執行引擎在讀取休眠檔時結束");
            if (send.IsFaulted) await send;
            var status = (await qmp.ExecuteAsync("query-status", ct: ct)).GetProperty("status").GetString();
            if (status == "paused") break;
            var mig = await qmp.ExecuteAsync("query-migrate", ct: ct);
            if (mig.TryGetProperty("status", out var s) && s.GetString() == "failed")
                throw new InvalidOperationException("讀取休眠檔失敗");
            if (DateTime.UtcNow > deadline) throw new TimeoutException("讀取休眠檔逾時");
            await Task.Delay(50, ct);
        }

        await send.WaitAsync(TimeSpan.FromSeconds(30), ct);

        // XP 即將繼續執行：先作廢休眠檔，再讓它跑
        _store.BeginRun();
        await qmp.ExecuteAsync("cont", ct: ct);
        _log.Info($"已從休眠檔恢復：{(DateTime.UtcNow - started).TotalSeconds:F1} 秒");
    }

    private static async Task WaitForMigrationAsync(QmpClient qmp, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + MigrationTimeout;
        while (true)
        {
            var info = await qmp.ExecuteAsync("query-migrate", ct: ct);
            var status = info.TryGetProperty("status", out var s) ? s.GetString() : null;
            if (status == "completed") return;
            if (status is "failed" or "cancelled")
            {
                var reason = info.TryGetProperty("error-desc", out var d) ? d.GetString() : status;
                throw new InvalidOperationException($"休眠存檔失敗：{reason}");
            }
            if (DateTime.UtcNow > deadline) throw new TimeoutException("休眠存檔逾時");
            await Task.Delay(50, ct);
        }
    }

    private VmLaunchSpec ToSafePaths(VmLaunchSpec spec) => spec with
    {
        SystemDisk = SafePath.MakeSafe(spec.SystemDisk, _store.VmDir),
        CdromImage = spec.CdromImage is null ? null : SafePath.MakeSafe(spec.CdromImage, _store.VmDir),
        FirmwareDir = spec.FirmwareDir is null ? null : SafePath.MakeSafe(spec.FirmwareDir, _store.VmDir),
    };

    private async Task QuitAndWaitAsync()
    {
        var process = _process;
        if (_qmp is { } qmp)
        {
            try { await qmp.ExecuteAsync("quit"); }
            catch (Exception ex) when (ex is IOException or QmpException) { /* 引擎可能已結束 */ }
        }
        if (process is not null)
        {
            var finished = await Task.WhenAny(process.Exited, Task.Delay(TimeSpan.FromSeconds(10)));
            if (finished != process.Exited) process.Kill();
        }
        await TeardownAsync();
        SetState(VmState.Stopped);
    }

    private void OnEngineExited(QemuProcess process, int exitCode)
    {
        bool expected;
        lock (_gate)
        {
            if (!ReferenceEquals(process, _process)) return;
            expected = _state is VmState.Saving or VmState.ShuttingDown or VmState.Stopped;
        }
        if (expected) return;

        _log.Warn($"執行引擎自行結束（代碼 {exitCode}）");
        _ = Task.Run(async () =>
        {
            await TeardownAsync();
            SetState(VmState.Stopped);
            EngineExited?.Invoke(exitCode);
        });
    }

    private QmpClient RequireRunning()
    {
        if (_state != VmState.Running || _qmp is null) throw new InvalidOperationException("XP 沒有在執行");
        return _qmp;
    }

    private async Task TeardownAsync()
    {
        QmpClient? qmp;
        QemuProcess? process;
        lock (_gate)
        {
            qmp = _qmp;
            process = _process;
            _qmp = null;
            _process = null;
        }
        if (qmp is not null) await qmp.DisposeAsync();
        process?.Dispose();
    }

    private void SetState(VmState state)
    {
        if (_state == state) return;
        _state = state;
        _log.Info($"XP 狀態：{state}");
        StateChanged?.Invoke(state);
    }

    public async ValueTask DisposeAsync() => await TeardownAsync();
}

internal static class PortAllocator
{
    public static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public static int GetFreePort(int from, int to)
    {
        for (var port = from; port <= to; port++)
        {
            try
            {
                var listener = new TcpListener(IPAddress.Loopback, port);
                listener.Start();
                listener.Stop();
                return port;
            }
            catch (SocketException) { }
        }
        throw new InvalidOperationException($"連接埠 {from}–{to} 都被佔用");
    }
}
