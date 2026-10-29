using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using XPOptical.Core.Diagnostics;
using XPOptical.Core.Profiles;
using XPOptical.Core.Qemu;
using XPOptical.Core.Vm;

namespace XPOptical.Core.Usb;

public enum UsbLinkState
{
    /// <summary>電腦上沒有偵測到這個裝置。</summary>
    NotPresent,
    Connecting,
    /// <summary>已接入 XP。</summary>
    Connected,
    Failed,
    /// <summary>XP 沒有在執行。</summary>
    Idle,
}

public sealed record UsbDeviceStatus(string Name, UsbLinkState State, string? Detail);

/// <summary>
/// 把符合規則的 USB 裝置接入 XP：在 QEMU 開一個 usb-redir 插槽，再啟動 usbredirect
/// （UsbDk + 專用事件執行緒，沒有 QEMU 內建直通的 10 ms 輪詢）把實體裝置轉進去。
/// usbredirect 結束（裝置拔除、斷電）時從 XP 移除，裝置重新出現時自動再接入。
/// </summary>
public sealed class UsbAttachManager : IAsyncDisposable
{
    /// <summary>usbredirect 持續執行超過這段時間才視為連線成功。</summary>
    internal static TimeSpan ConnectedAfter { get; set; } = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan DeviceDeletedTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

    private readonly string? _usbRedirect;
    private readonly IUsbEnumerator _enumerator;
    private readonly ILog _log;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly Dictionary<string, Attachment> _attached = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Backoff> _backoff = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _lastError = new();
    private IReadOnlyList<UsbDeviceRule> _rules = [];
    private IReadOnlyList<UsbDeviceInfo> _present = [];
    private QmpClient? _qmp;
    private volatile bool _paused;
    private int _counter;

    public UsbAttachManager(string? usbRedirectPath, IUsbEnumerator enumerator, ILog log)
    {
        _usbRedirect = usbRedirectPath;
        _enumerator = enumerator;
        _log = log;
    }

    public event Action? StatusChanged;

    public bool IsAvailable => _usbRedirect is not null;

    public void SetRules(IReadOnlyList<UsbDeviceRule> rules)
    {
        _rules = rules;
        StatusChanged?.Invoke();
    }

    /// <summary>XP 開始執行後呼叫。</summary>
    public void Bind(QmpClient qmp)
    {
        _qmp = qmp;
        _paused = false;
        StatusChanged?.Invoke();
    }

    /// <summary>休眠/關機期間暫停自動接入，避免在存檔途中又把裝置接進去。</summary>
    public void Pause() => _paused = true;

    public void Resume() => _paused = false;

    public IReadOnlyList<UsbDeviceStatus> GetStatus()
    {
        lock (_attached)
        {
            return _rules.Select(rule =>
            {
                var att = _attached.Values.FirstOrDefault(a => a.Rule == rule);
                if (att is not null)
                {
                    return DateTime.UtcNow - att.StartedAt >= ConnectedAfter
                        ? new UsbDeviceStatus(rule.Name, UsbLinkState.Connected, att.Device.IdText)
                        : new UsbDeviceStatus(rule.Name, UsbLinkState.Connecting, att.Device.IdText);
                }
                if (_lastError.TryGetValue(rule.Name, out var err))
                    return new UsbDeviceStatus(rule.Name, UsbLinkState.Failed, err);
                if (_qmp is null) return new UsbDeviceStatus(rule.Name, UsbLinkState.Idle, null);
                var present = _present.Any(d => rule.Matches(d.Vid, d.Pid));
                return new UsbDeviceStatus(rule.Name, present ? UsbLinkState.Connecting : UsbLinkState.NotPresent, null);
            }).ToList();
        }
    }

    /// <summary>列舉 USB 裝置並接入符合規則、尚未接入的裝置。定期呼叫，或在 Windows 通知裝置變動時呼叫。</summary>
    public async Task ScanAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            _present = _enumerator.GetPresentDevices();
            var qmp = _qmp;
            if (qmp is null || _usbRedirect is null || _paused) return;

            foreach (var rule in _rules)
            {
                lock (_attached)
                {
                    if (_attached.Values.Any(a => a.Rule == rule)) continue;
                }
                var device = _present.FirstOrDefault(d => rule.Matches(d.Vid, d.Pid));
                if (device is null)
                {
                    // 裝置拔掉了：清除舊錯誤，重新插上時立即再試
                    _lastError.TryRemove(rule.Name, out _);
                    _backoff.Remove(rule.Name);
                    continue;
                }
                if (_backoff.TryGetValue(rule.Name, out var b) && DateTime.UtcNow < b.RetryAfter) continue;
                await AttachAsync(qmp, rule, device, ct);
            }
        }
        finally
        {
            _lock.Release();
            StatusChanged?.Invoke();
        }
    }

    /// <summary>休眠或關機前呼叫：從 XP 移除所有轉送中的裝置，裝置回到 Windows。</summary>
    public async Task DetachAllAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            List<Attachment> all;
            lock (_attached)
            {
                all = _attached.Values.ToList();
                _attached.Clear();
            }
            foreach (var att in all)
            {
                att.Detaching = true;
                if (_qmp is { } qmp) await RemoveFromGuestAsync(qmp, att, waitForEvent: true);
                att.Process.Kill();
                _log.Info($"已從 XP 移除 USB 裝置 {att.Rule.Name}（{att.Device.IdText}）");
            }
            _backoff.Clear();
            _lastError.Clear();
        }
        finally
        {
            _lock.Release();
            StatusChanged?.Invoke();
        }
    }

    /// <summary>XP 已停止執行（不需要再操作 QEMU）。</summary>
    public async Task UnbindAsync()
    {
        await _lock.WaitAsync();
        try
        {
            _qmp = null;
            lock (_attached)
            {
                foreach (var att in _attached.Values)
                {
                    att.Detaching = true;
                    att.Process.Kill();
                }
                _attached.Clear();
            }
            _backoff.Clear();
            _lastError.Clear();
        }
        finally
        {
            _lock.Release();
            StatusChanged?.Invoke();
        }
    }

    private async Task AttachAsync(QmpClient qmp, UsbDeviceRule rule, UsbDeviceInfo device, CancellationToken ct)
    {
        var n = Interlocked.Increment(ref _counter);
        var chardev = $"usbchr{n}";
        var devId = $"usbdev{n}";
        var port = PortAllocator.GetFreePort();
        _log.Info($"接入 USB 裝置 {rule.Name}（{device.IdText}）到 XP");

        try
        {
            await qmp.ExecuteAsync("chardev-add", new
            {
                id = chardev,
                backend = new
                {
                    type = "socket",
                    data = new
                    {
                        addr = new { type = "inet", data = new { host = "127.0.0.1", port = port.ToString() } },
                        server = true,
                        wait = false,
                    },
                },
            }, ct);
            try
            {
                await qmp.ExecuteAsync("device_add", new Dictionary<string, object>
                {
                    ["driver"] = "usb-redir",
                    ["chardev"] = chardev,
                    ["id"] = devId,
                    ["bus"] = QemuCommandBuilder.UsbBus,
                }, ct);
            }
            catch
            {
                await TryExecuteAsync(qmp, "chardev-remove", new { id = chardev });
                throw;
            }

            var att = new Attachment(rule, device, devId, chardev, StartRedirect(device, port));
            lock (_attached) _attached[device.InstanceId] = att;
            _ = att.Process.Exited.ContinueWith(_ => OnRedirectExitedAsync(att), TaskScheduler.Default).Unwrap();
            _ = Task.Delay(ConnectedAfter + TimeSpan.FromMilliseconds(100), CancellationToken.None)
                .ContinueWith(_ => StatusChanged?.Invoke(), TaskScheduler.Default);
        }
        catch (Exception ex) when (ex is QmpException or IOException or InvalidOperationException
                                       or System.ComponentModel.Win32Exception)
        {
            _log.Error($"接入 {rule.Name} 失敗", ex);
            RecordFailure(rule.Name, "無法接入 XP：" + ex.Message);
        }
    }

    private RedirectProcess StartRedirect(UsbDeviceInfo device, int port)
    {
        var psi = new ProcessStartInfo(_usbRedirect!)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        psi.ArgumentList.Add("--device");
        psi.ArgumentList.Add(device.IdText);
        psi.ArgumentList.Add("--to");
        psi.ArgumentList.Add($"127.0.0.1:{port}");
        return RedirectProcess.Start(psi, _log);
    }

    private async Task OnRedirectExitedAsync(Attachment att)
    {
        if (att.Detaching) return;
        var ranFor = DateTime.UtcNow - att.StartedAt;
        _log.Warn($"USB 轉送結束：{att.Rule.Name}（代碼 {att.Process.ExitCode}，執行 {ranFor.TotalSeconds:F1} 秒）"
                  + $" {string.Join(" | ", att.Process.OutputTail)}");

        await _lock.WaitAsync();
        try
        {
            lock (_attached)
            {
                if (!_attached.Remove(att.Device.InstanceId)) return;
            }
            if (_qmp is { } qmp) await RemoveFromGuestAsync(qmp, att, waitForEvent: false);

            if (ranFor < ConnectedAfter)
                RecordFailure(att.Rule.Name, DescribeFailure(att.Process.OutputTail));
            else
                _backoff[att.Rule.Name] = new Backoff(0, DateTime.UtcNow + TimeSpan.FromSeconds(1));
        }
        finally
        {
            _lock.Release();
            StatusChanged?.Invoke();
        }
    }

    private void RecordFailure(string ruleName, string message)
    {
        var failures = _backoff.TryGetValue(ruleName, out var b) ? b.Failures + 1 : 1;
        var delay = TimeSpan.FromSeconds(Math.Min(MaxBackoff.TotalSeconds, 2 * Math.Pow(2, failures - 1)));
        _backoff[ruleName] = new Backoff(failures, DateTime.UtcNow + delay);
        _lastError[ruleName] = message;
    }

    private static string DescribeFailure(IReadOnlyList<string> output)
    {
        var text = string.Join(" ", output);
        if (text.Contains("open", StringComparison.OrdinalIgnoreCase))
            return "無法開啟裝置：可能正被 Windows 上的其他程式使用，或 USB 驅動（UsbDk）尚未安裝";
        return output.Count > 0 ? $"USB 轉送失敗：{output[^1]}" : "USB 轉送失敗";
    }

    private async Task RemoveFromGuestAsync(QmpClient qmp, Attachment att, bool waitForEvent)
    {
        Task? deleted = null;
        if (waitForEvent)
        {
            deleted = qmp.WaitForEventAsync("DEVICE_DELETED",
                d => d.ValueKind == JsonValueKind.Object && d.TryGetProperty("device", out var dev)
                     && dev.GetString() == att.DeviceId,
                DeviceDeletedTimeout);
        }
        // usb-redir 移除時 QEMU 會一併刪除它的 chardev，只有 device_del 失敗時才需另外移除
        if (!await TryExecuteAsync(qmp, "device_del", new { id = att.DeviceId }))
            await TryExecuteAsync(qmp, "chardev-remove", new { id = att.ChardevId });
        else if (deleted is not null)
        {
            try { await deleted; }
            catch (OperationCanceledException) { _log.Warn($"等待 {att.DeviceId} 移除逾時"); }
        }
    }

    private async Task<bool> TryExecuteAsync(QmpClient qmp, string command, object args)
    {
        try
        {
            await qmp.ExecuteAsync(command, args);
            return true;
        }
        catch (Exception ex) when (ex is QmpException or IOException)
        {
            _log.Warn($"{command} 失敗：{ex.Message}");
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await UnbindAsync();
        _lock.Dispose();
    }

    private sealed class Attachment(UsbDeviceRule rule, UsbDeviceInfo device, string deviceId, string chardevId,
        RedirectProcess process)
    {
        public UsbDeviceRule Rule { get; } = rule;
        public UsbDeviceInfo Device { get; } = device;
        public string DeviceId { get; } = deviceId;
        public string ChardevId { get; } = chardevId;
        public RedirectProcess Process { get; } = process;
        public DateTime StartedAt { get; } = DateTime.UtcNow;
        public volatile bool Detaching;
    }

    private readonly record struct Backoff(int Failures, DateTime RetryAfter);
}

/// <summary>usbredirect 行程，保留最後幾行輸出供錯誤說明。</summary>
internal sealed class RedirectProcess
{
    private const int TailLines = 10;
    private readonly Process _process;
    private readonly Queue<string> _tail = new();
    private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private RedirectProcess(Process process) => _process = process;

    public static RedirectProcess Start(ProcessStartInfo psi, ILog log)
    {
        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var rp = new RedirectProcess(process);
        DataReceivedEventHandler onLine = (_, e) =>
        {
            if (e.Data is null) return;
            log.Info($"[usbredirect] {e.Data}");
            lock (rp._tail)
            {
                rp._tail.Enqueue(e.Data);
                while (rp._tail.Count > TailLines) rp._tail.Dequeue();
            }
        };
        process.OutputDataReceived += onLine;
        process.ErrorDataReceived += onLine;
        process.Exited += (_, _) =>
        {
            process.WaitForExit();
            rp._exited.TrySetResult();
        };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return rp;
    }

    public Task Exited => _exited.Task;
    public int ExitCode => _process.HasExited ? _process.ExitCode : -1;

    public IReadOnlyList<string> OutputTail
    {
        get { lock (_tail) return _tail.ToList(); }
    }

    public void Kill()
    {
        try { if (!_process.HasExited) _process.Kill(); }
        catch (InvalidOperationException) { }
    }
}
