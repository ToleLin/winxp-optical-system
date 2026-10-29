using System.Net;
using System.Net.Sockets;

namespace XPOptical.Core.Vm;

/// <summary>
/// 以本機 TCP 傳送休眠檔。QEMU 在 Windows 上不支援直接遷移到檔案
/// （io/channel-file.c 的非阻塞模式在 WIN32 未實作），所以存檔時 QEMU 連到本程式、由本程式寫檔；
/// 恢復時 QEMU 監聽、由本程式把檔案送回去。
/// 注意：Windows 版 QEMU 傳完後不一定會關閉連線，因此不能以「連線關閉」判斷傳輸結束。
/// </summary>
internal static class StateStream
{
    private const int BufferSize = 1 << 20;

    public static StateReceiver ReceiveToFile(string path, CancellationToken ct) => new(path, ct);

    /// <summary>連到正在等待恢復的 QEMU，把休眠檔送完後關閉送出方向（不等待 QEMU 關閉連線）。</summary>
    public static async Task SendFileAsync(int port, string path, TimeSpan connectTimeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + connectTimeout;
        while (true)
        {
            var client = new TcpClient { NoDelay = true };
            try
            {
                await client.ConnectAsync(IPAddress.Loopback, port, ct);
            }
            catch (SocketException) when (DateTime.UtcNow < deadline)
            {
                client.Dispose();
                await Task.Delay(50, ct);
                continue;
            }

            using (client)
            {
                await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                    BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
                await file.CopyToAsync(client.GetStream(), BufferSize, ct);
                client.Client.Shutdown(SocketShutdown.Send);
            }
            return;
        }
    }

    public static object[] SocketChannel(int port) =>
    [
        new Dictionary<string, object>
        {
            ["channel-type"] = "main",
            ["addr"] = new Dictionary<string, object>
            {
                ["transport"] = "socket",
                ["type"] = "inet",
                ["host"] = "127.0.0.1",
                ["port"] = port.ToString(),
            },
        },
    ];
}

/// <summary>接收 QEMU 送來的休眠資料並寫入檔案。</summary>
internal sealed class StateReceiver
{
    private const int BufferSize = 1 << 20;
    private readonly TcpListener _listener;
    private long _bytes;
    private long _lastActivityTicks = DateTime.UtcNow.Ticks;

    public StateReceiver(string path, CancellationToken ct)
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        Completion = Task.Run(() => ReceiveAsync(path, ct), CancellationToken.None);
    }

    public int Port { get; }

    /// <summary>連線結束（正常關閉或被重設）時完成，結果為檔案大小。</summary>
    public Task<long> Completion { get; }

    public long BytesReceived => Interlocked.Read(ref _bytes);

    /// <summary>等到已有資料且一段時間沒有新資料（QEMU 已送完），或連線已結束。</summary>
    public async Task WaitForIdleAsync(TimeSpan idle, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!Completion.IsCompleted)
        {
            var quietFor = DateTime.UtcNow - new DateTime(Interlocked.Read(ref _lastActivityTicks), DateTimeKind.Utc);
            if (BytesReceived > 0 && quietFor >= idle) return;
            if (DateTime.UtcNow > deadline) throw new TimeoutException("接收休眠資料逾時");
            await Task.Delay(50, ct);
        }
        await Completion;
    }

    private async Task<long> ReceiveAsync(string path, CancellationToken ct)
    {
        try
        {
            using var client = await _listener.AcceptTcpClientAsync(ct);
            await using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None,
                BufferSize, FileOptions.Asynchronous);
            var stream = client.GetStream();
            var buffer = new byte[BufferSize];
            while (true)
            {
                int n;
                try
                {
                    n = await stream.ReadAsync(buffer, ct);
                }
                catch (IOException) when (BytesReceived > 0)
                {
                    break; // QEMU 結束時連線可能被重設；資料已在結束前讀完
                }
                if (n == 0) break;
                await file.WriteAsync(buffer.AsMemory(0, n), ct);
                Interlocked.Add(ref _bytes, n);
                Interlocked.Exchange(ref _lastActivityTicks, DateTime.UtcNow.Ticks);
            }
            await file.FlushAsync(ct);
            file.Flush(flushToDisk: true);
            return file.Length;
        }
        finally
        {
            _listener.Stop();
        }
    }
}
