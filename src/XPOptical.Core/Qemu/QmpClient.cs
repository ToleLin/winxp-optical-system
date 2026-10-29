using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace XPOptical.Core.Qemu;

public sealed class QmpException(string command, string errorClass, string description)
    : Exception($"QMP {command} 失敗：{errorClass} {description}")
{
    public string ErrorClass { get; } = errorClass;
    public string Description { get; } = description;
}

public sealed record QmpEvent(string Name, JsonElement Data);

/// <summary>QEMU Machine Protocol 用戶端：送出指令、接收非同步事件。</summary>
public sealed class QmpClient : IAsyncDisposable
{
    private readonly TcpClient _tcp;
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly CancellationTokenSource _cts = new();
    private Task _readLoop = Task.CompletedTask;
    private long _nextId;

    private QmpClient(TcpClient tcp)
    {
        _tcp = tcp;
        var stream = tcp.GetStream();
        _reader = new StreamReader(stream, new UTF8Encoding(false));
        _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
    }

    public event Action<QmpEvent>? EventReceived;

    /// <summary>連線中斷（通常是 QEMU 結束）時完成。</summary>
    public Task Completion => _readLoop;

    /// <summary>連線並完成 QMP 握手。QEMU 剛啟動時可能還沒開始監聽，會重試直到逾時。</summary>
    public static async Task<QmpClient> ConnectAsync(int port, TimeSpan timeout, CancellationToken ct = default,
        Func<bool>? abort = null)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var tcp = new TcpClient { NoDelay = true };
            try
            {
                await tcp.ConnectAsync("127.0.0.1", port, ct);
                var client = new QmpClient(tcp);
                await client.HandshakeAsync(ct);
                return client;
            }
            catch (Exception ex) when (ex is SocketException or IOException)
            {
                tcp.Dispose();
                if (abort?.Invoke() == true) throw new InvalidOperationException("執行引擎已結束", ex);
                if (DateTime.UtcNow > deadline) throw new TimeoutException("無法連線到執行引擎的控制介面", ex);
                await Task.Delay(100, ct);
            }
        }
    }

    private async Task HandshakeAsync(CancellationToken ct)
    {
        var greeting = await _reader.ReadLineAsync(ct) ?? throw new IOException("QMP 連線在握手前中斷");
        if (!greeting.Contains("\"QMP\"")) throw new IOException($"不是 QMP 伺服器：{greeting}");
        _readLoop = Task.Run(ReadLoopAsync);
        await ExecuteAsync("qmp_capabilities", ct: ct);
    }

    public async Task<JsonElement> ExecuteAsync(string command, object? arguments = null, CancellationToken ct = default)
    {
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        var message = new JsonObject { ["execute"] = command, ["id"] = id };
        if (arguments is not null) message["arguments"] = JsonSerializer.SerializeToNode(arguments);

        await _writeLock.WaitAsync(ct);
        try
        {
            await _writer.WriteLineAsync(message.ToJsonString());
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            _pending.TryRemove(id, out _);
            throw new IOException("執行引擎的控制連線已中斷", ex);
        }
        finally
        {
            _writeLock.Release();
        }

        using var reg = ct.Register(() => tcs.TrySetCanceled(ct));
        var reply = await tcs.Task;
        if (reply.TryGetProperty("error", out var error))
        {
            throw new QmpException(command,
                error.TryGetProperty("class", out var c) ? c.GetString() ?? "" : "",
                error.TryGetProperty("desc", out var d) ? d.GetString() ?? "" : "");
        }
        return reply.TryGetProperty("return", out var ret) ? ret.Clone() : default;
    }

    /// <summary>等待指定事件；<paramref name="match"/> 可篩選事件內容。</summary>
    public async Task<QmpEvent> WaitForEventAsync(string name, Func<JsonElement, bool>? match, TimeSpan timeout,
        CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<QmpEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(QmpEvent e)
        {
            if (e.Name == name && (match?.Invoke(e.Data) ?? true)) tcs.TrySetResult(e);
        }
        EventReceived += Handler;
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);
            using var reg = timeoutCts.Token.Register(() => tcs.TrySetCanceled());
            return await tcs.Task;
        }
        finally
        {
            EventReceived -= Handler;
        }
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var line = await _reader.ReadLineAsync(_cts.Token);
                if (line is null) break;
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.TryGetProperty("event", out var ev))
                {
                    var data = root.TryGetProperty("data", out var d) ? d.Clone() : default;
                    EventReceived?.Invoke(new QmpEvent(ev.GetString() ?? "", data));
                }
                else if (root.TryGetProperty("id", out var idProp) && idProp.TryGetInt64(out var id)
                         && _pending.TryRemove(id, out var tcs))
                {
                    tcs.TrySetResult(root.Clone());
                }
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException
                                       or JsonException)
        {
        }
        finally
        {
            foreach (var (_, tcs) in _pending) tcs.TrySetException(new IOException("執行引擎的控制連線已中斷"));
            _pending.Clear();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _tcp.Dispose();
        try { await _readLoop; } catch { /* 已在讀取迴圈中處理 */ }
        _cts.Dispose();
        _writeLock.Dispose();
    }
}
