using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;

namespace XPOptical.Core.Display;

public readonly record struct RfbRect(int X, int Y, int Width, int Height)
{
    public bool IsEmpty => Width <= 0 || Height <= 0;

    public RfbRect Union(RfbRect other)
    {
        if (IsEmpty) return other;
        if (other.IsEmpty) return this;
        var x = Math.Min(X, other.X);
        var y = Math.Min(Y, other.Y);
        return new RfbRect(x, y, Math.Max(X + Width, other.X + other.Width) - x,
            Math.Max(Y + Height, other.Y + other.Height) - y);
    }
}

/// <summary>
/// 精簡的 VNC（RFB 3.8）用戶端，用來把 XP 畫面畫在主視窗裡。只連本機的 QEMU，
/// 因此只實作 Raw/CopyRect 編碼；鍵盤使用 QEMU 擴充按鍵事件直接送掃描碼，避免鍵盤配置問題。
/// </summary>
public sealed class RfbClient : IAsyncDisposable
{
    private const int EncodingRaw = 0;
    private const int EncodingCopyRect = 1;
    private const int EncodingDesktopSize = -223;
    private const int EncodingQemuPointerMotionChange = -257;
    private const int EncodingQemuExtendedKeyEvent = -258;
    private const int BytesPerPixel = 4;

    private readonly TcpClient _tcp;
    private readonly NetworkStream _stream;
    private readonly object _writeLock = new();
    private readonly CancellationTokenSource _cts = new();
    private Task _readLoop = Task.CompletedTask;
    private RfbRect _dirty;

    private RfbClient(TcpClient tcp)
    {
        _tcp = tcp;
        _stream = tcp.GetStream();
    }

    public int Width { get; private set; }
    public int Height { get; private set; }
    public string DesktopName { get; private set; } = "";

    /// <summary>BGRX 32 位元畫面緩衝區（與 WPF Bgr32 相同排列）。讀取時須鎖定 <see cref="FrameLock"/>。</summary>
    public byte[] FrameBuffer { get; private set; } = [];
    public object FrameLock { get; } = new();

    public bool ExtendedKeyEventSupported { get; private set; }

    /// <summary>畫面有更新（在背景執行緒觸發）。</summary>
    public event Action? Updated;
    /// <summary>畫面解析度改變（在背景執行緒觸發）。</summary>
    public event Action? Resized;

    /// <summary>連線中斷時完成；正常關閉或錯誤都會完成。</summary>
    public Task Completion => _readLoop;

    public static async Task<RfbClient> ConnectAsync(int port, TimeSpan timeout, CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            var tcp = new TcpClient { NoDelay = true };
            try
            {
                await tcp.ConnectAsync("127.0.0.1", port, ct);
                var client = new RfbClient(tcp);
                await client.HandshakeAsync(ct);
                client._readLoop = Task.Run(client.ReadLoopAsync);
                return client;
            }
            catch (Exception ex) when (ex is SocketException or IOException)
            {
                tcp.Dispose();
                if (DateTime.UtcNow > deadline) throw new TimeoutException("無法連線到 XP 畫面", ex);
                await Task.Delay(100, ct);
            }
        }
    }

    private async Task HandshakeAsync(CancellationToken ct)
    {
        var version = Encoding.ASCII.GetString(await ReadExactAsync(12, ct));
        if (!version.StartsWith("RFB 003.", StringComparison.Ordinal)) throw new IOException($"不是 VNC 伺服器：{version}");
        await _stream.WriteAsync("RFB 003.008\n"u8.ToArray(), ct);

        var count = (await ReadExactAsync(1, ct))[0];
        if (count == 0) throw new IOException($"VNC 伺服器拒絕連線：{await ReadReasonAsync(ct)}");
        var types = await ReadExactAsync(count, ct);
        if (!types.Contains((byte)1)) throw new IOException("VNC 伺服器要求密碼，但本程式只連線本機無密碼的伺服器");
        await _stream.WriteAsync(new byte[] { 1 }, ct);
        if (BinaryPrimitives.ReadUInt32BigEndian(await ReadExactAsync(4, ct)) != 0)
            throw new IOException($"VNC 驗證失敗：{await ReadReasonAsync(ct)}");

        await _stream.WriteAsync(new byte[] { 1 }, ct); // ClientInit：共用連線
        var init = await ReadExactAsync(24, ct);
        var width = BinaryPrimitives.ReadUInt16BigEndian(init.AsSpan(0));
        var height = BinaryPrimitives.ReadUInt16BigEndian(init.AsSpan(2));
        var nameLength = BinaryPrimitives.ReadUInt32BigEndian(init.AsSpan(20));
        DesktopName = Encoding.UTF8.GetString(await ReadExactAsync(checked((int)nameLength), ct));
        Resize(width, height);

        // 32 位元、小端序、R/G/B 位移 16/8/0 => 記憶體中為 B,G,R,X
        var setPixelFormat = new byte[20];
        setPixelFormat[0] = 0;
        setPixelFormat[4] = 32;  // bits per pixel
        setPixelFormat[5] = 24;  // depth
        setPixelFormat[6] = 0;   // little endian
        setPixelFormat[7] = 1;   // true color
        BinaryPrimitives.WriteUInt16BigEndian(setPixelFormat.AsSpan(8), 255);
        BinaryPrimitives.WriteUInt16BigEndian(setPixelFormat.AsSpan(10), 255);
        BinaryPrimitives.WriteUInt16BigEndian(setPixelFormat.AsSpan(12), 255);
        setPixelFormat[14] = 16;
        setPixelFormat[15] = 8;
        setPixelFormat[16] = 0;

        int[] encodings = [EncodingCopyRect, EncodingRaw, EncodingDesktopSize, EncodingQemuExtendedKeyEvent];
        var setEncodings = new byte[4 + 4 * encodings.Length];
        setEncodings[0] = 2;
        BinaryPrimitives.WriteUInt16BigEndian(setEncodings.AsSpan(2), (ushort)encodings.Length);
        for (var i = 0; i < encodings.Length; i++)
            BinaryPrimitives.WriteInt32BigEndian(setEncodings.AsSpan(4 + 4 * i), encodings[i]);

        Send(setPixelFormat);
        Send(setEncodings);
        RequestUpdate(incremental: false);
    }

    private async Task ReadLoopAsync()
    {
        var ct = _cts.Token;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var type = (await ReadExactAsync(1, ct))[0];
                switch (type)
                {
                    case 0:
                        await ReadFramebufferUpdateAsync(ct);
                        RequestUpdate(incremental: true);
                        break;
                    case 1: // SetColourMapEntries（真彩模式下不會用到）
                        var cm = await ReadExactAsync(5, ct);
                        await ReadExactAsync(BinaryPrimitives.ReadUInt16BigEndian(cm.AsSpan(3)) * 6, ct);
                        break;
                    case 2: // Bell
                        break;
                    case 3: // ServerCutText
                        var cut = await ReadExactAsync(7, ct);
                        await ReadExactAsync(checked((int)BinaryPrimitives.ReadUInt32BigEndian(cut.AsSpan(3))), ct);
                        break;
                    default:
                        throw new IOException($"不支援的 VNC 訊息類型 {type}");
                }
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException
                                       or SocketException)
        {
        }
    }

    private async Task ReadFramebufferUpdateAsync(CancellationToken ct)
    {
        var header = await ReadExactAsync(3, ct);
        int rects = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(1));
        var dirty = default(RfbRect);
        for (var i = 0; i < rects; i++)
        {
            var r = await ReadExactAsync(12, ct);
            int x = BinaryPrimitives.ReadUInt16BigEndian(r.AsSpan(0));
            int y = BinaryPrimitives.ReadUInt16BigEndian(r.AsSpan(2));
            int w = BinaryPrimitives.ReadUInt16BigEndian(r.AsSpan(4));
            int h = BinaryPrimitives.ReadUInt16BigEndian(r.AsSpan(6));
            var encoding = BinaryPrimitives.ReadInt32BigEndian(r.AsSpan(8));
            switch (encoding)
            {
                case EncodingRaw:
                    var pixels = await ReadExactAsync(w * h * BytesPerPixel, ct);
                    lock (FrameLock) BlitRaw(pixels, x, y, w, h);
                    dirty = dirty.Union(new RfbRect(x, y, w, h));
                    break;
                case EncodingCopyRect:
                    var src = await ReadExactAsync(4, ct);
                    lock (FrameLock)
                        CopyRect(BinaryPrimitives.ReadUInt16BigEndian(src.AsSpan(0)),
                            BinaryPrimitives.ReadUInt16BigEndian(src.AsSpan(2)), x, y, w, h);
                    dirty = dirty.Union(new RfbRect(x, y, w, h));
                    break;
                case EncodingDesktopSize:
                    lock (FrameLock) Resize(w, h);
                    dirty = new RfbRect(0, 0, w, h);
                    Resized?.Invoke();
                    break;
                case EncodingQemuExtendedKeyEvent:
                    ExtendedKeyEventSupported = true;
                    break;
                case EncodingQemuPointerMotionChange:
                    break;
                default:
                    throw new IOException($"不支援的 VNC 編碼 {encoding}");
            }
        }

        if (!dirty.IsEmpty)
        {
            lock (FrameLock) _dirty = _dirty.Union(dirty);
            Updated?.Invoke();
        }
    }

    /// <summary>取出自上次呼叫以來變動的區域。呼叫端須已鎖定 <see cref="FrameLock"/>。</summary>
    public bool TryTakeDirty(out RfbRect rect)
    {
        rect = Clip(_dirty);
        _dirty = default;
        return !rect.IsEmpty;
    }

    private RfbRect Clip(RfbRect r)
    {
        var x = Math.Clamp(r.X, 0, Width);
        var y = Math.Clamp(r.Y, 0, Height);
        return new RfbRect(x, y, Math.Clamp(r.X + r.Width, 0, Width) - x, Math.Clamp(r.Y + r.Height, 0, Height) - y);
    }

    private void Resize(int width, int height)
    {
        Width = width;
        Height = height;
        FrameBuffer = new byte[width * height * BytesPerPixel];
    }

    private void BlitRaw(byte[] pixels, int x, int y, int w, int h)
    {
        var rowBytes = w * BytesPerPixel;
        var stride = Width * BytesPerPixel;
        for (var row = 0; row < h; row++)
        {
            if (y + row >= Height) break;
            var count = Math.Min(rowBytes, (Width - x) * BytesPerPixel);
            if (count <= 0) break;
            Buffer.BlockCopy(pixels, row * rowBytes, FrameBuffer, (y + row) * stride + x * BytesPerPixel, count);
        }
    }

    private void CopyRect(int sx, int sy, int dx, int dy, int w, int h)
    {
        var stride = Width * BytesPerPixel;
        var rowBytes = w * BytesPerPixel;
        var temp = new byte[rowBytes * h];
        for (var row = 0; row < h; row++)
            Buffer.BlockCopy(FrameBuffer, (sy + row) * stride + sx * BytesPerPixel, temp, row * rowBytes, rowBytes);
        for (var row = 0; row < h; row++)
            Buffer.BlockCopy(temp, row * rowBytes, FrameBuffer, (dy + row) * stride + dx * BytesPerPixel, rowBytes);
    }

    private void RequestUpdate(bool incremental)
    {
        var msg = new byte[10];
        msg[0] = 3;
        msg[1] = (byte)(incremental ? 1 : 0);
        BinaryPrimitives.WriteUInt16BigEndian(msg.AsSpan(6), (ushort)Width);
        BinaryPrimitives.WriteUInt16BigEndian(msg.AsSpan(8), (ushort)Height);
        Send(msg);
    }

    /// <summary>滑鼠事件。按鍵遮罩：左 1、中 2、右 4、滾輪上 8、滾輪下 16。</summary>
    public void SendPointer(int x, int y, byte buttonMask)
    {
        var msg = new byte[6];
        msg[0] = 5;
        msg[1] = buttonMask;
        BinaryPrimitives.WriteUInt16BigEndian(msg.AsSpan(2), (ushort)Math.Clamp(x, 0, Math.Max(0, Width - 1)));
        BinaryPrimitives.WriteUInt16BigEndian(msg.AsSpan(4), (ushort)Math.Clamp(y, 0, Math.Max(0, Height - 1)));
        Send(msg);
    }

    /// <summary>
    /// 以 PC/XT 掃描碼送出按鍵（QEMU 擴充按鍵事件）。E0 前綴的延伸鍵以最高位元表示，
    /// 例如右 Ctrl（E0 1D）為 0x9D。
    /// </summary>
    public void SendScancode(bool down, uint keycode, uint keysym = 0)
    {
        if (!ExtendedKeyEventSupported)
        {
            if (keysym != 0) SendKeysym(down, keysym);
            return;
        }
        var msg = new byte[12];
        msg[0] = 255;
        msg[1] = 0;
        BinaryPrimitives.WriteUInt16BigEndian(msg.AsSpan(2), (ushort)(down ? 1 : 0));
        BinaryPrimitives.WriteUInt32BigEndian(msg.AsSpan(4), keysym);
        BinaryPrimitives.WriteUInt32BigEndian(msg.AsSpan(8), keycode);
        Send(msg);
    }

    public void SendKeysym(bool down, uint keysym)
    {
        var msg = new byte[8];
        msg[0] = 4;
        msg[1] = (byte)(down ? 1 : 0);
        BinaryPrimitives.WriteUInt32BigEndian(msg.AsSpan(4), keysym);
        Send(msg);
    }

    /// <summary>送出 Ctrl+Alt+Del（Windows 本身會攔截這組按鍵，所以需要工具列按鈕）。</summary>
    public void SendCtrlAltDel()
    {
        const uint ctrl = 0x1D, alt = 0x38, del = 0xD3; // Delete 為 E0 53
        SendScancode(true, ctrl, 0xFFE3);
        SendScancode(true, alt, 0xFFE9);
        SendScancode(true, del, 0xFFFF);
        SendScancode(false, del, 0xFFFF);
        SendScancode(false, alt, 0xFFE9);
        SendScancode(false, ctrl, 0xFFE3);
    }

    private void Send(byte[] data)
    {
        try
        {
            lock (_writeLock) _stream.Write(data);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException)
        {
            // 連線已中斷；讀取迴圈會結束並回報
        }
    }

    private async Task<byte[]> ReadExactAsync(int count, CancellationToken ct)
    {
        var buffer = new byte[count];
        await _stream.ReadExactlyAsync(buffer, ct);
        return buffer;
    }

    private async Task<string> ReadReasonAsync(CancellationToken ct)
    {
        var len = BinaryPrimitives.ReadUInt32BigEndian(await ReadExactAsync(4, ct));
        return Encoding.UTF8.GetString(await ReadExactAsync(checked((int)Math.Min(len, 4096)), ct));
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _tcp.Dispose();
        try { await _readLoop; } catch { /* 已在讀取迴圈中處理 */ }
        _cts.Dispose();
    }
}
