using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using XPOptical.Core.Display;

namespace XPOptical.App;

/// <summary>
/// 顯示 XP 畫面並把滑鼠/鍵盤送進去。畫面以「實體像素的整數倍」放大，在高解析度螢幕上保持清晰；
/// 視窗比 XP 畫面小時才等比例縮小。
/// </summary>
public sealed class VncDisplay : FrameworkElement
{
    private const int VkNumLock = 0x90;
    private const int VkPause = 0x13;
    private const int VkSnapshot = 0x2C;

    private RfbClient? _client;
    private WriteableBitmap? _bitmap;
    private Rect _dest;
    private byte _buttons;
    private readonly HashSet<uint> _pressed = [];

    public VncDisplay()
    {
        Focusable = true;
        FocusVisualStyle = null;
        ClipToBounds = true;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
        // 中文輸入法由 XP 內處理，Windows 這端不攔截按鍵
        InputMethod.SetIsInputMethodEnabled(this, false);
    }

    public bool IsConnected => _client is not null;

    /// <summary>連線中斷（XP 引擎結束）時觸發，在背景執行緒。</summary>
    public event Action? Disconnected;

    public async Task ConnectAsync(int port)
    {
        await DisconnectAsync();
        var client = await RfbClient.ConnectAsync(port, TimeSpan.FromSeconds(15));
        _client = client;
        CompositionTarget.Rendering += OnRendering;
        _ = client.Completion.ContinueWith(_ => Disconnected?.Invoke(), TaskScheduler.Default);
        Focus();
    }

    public async Task DisconnectAsync()
    {
        var client = _client;
        if (client is null) return;
        ReleaseAllKeys();
        CompositionTarget.Rendering -= OnRendering;
        _client = null;
        _bitmap = null;
        InvalidateVisual();
        await client.DisposeAsync();
    }

    public void SendCtrlAltDel() => _client?.SendCtrlAltDel();

    /// <summary>由主視窗的訊息攔截呼叫，參數取自 WM_KEYDOWN/WM_KEYUP 的 lParam。</summary>
    public void SendKey(bool down, int virtualKey, uint scancode, bool extended)
    {
        if (_client is null) return;
        uint keycode = virtualKey switch
        {
            VkNumLock => 0x45,  // Windows 把 NumLock 標成延伸鍵，XT 掃描碼其實是 45
            VkPause => 0xC6,    // QEMU 以 0xC6 表示 Pause（E1 1D 45）
            _ => extended ? 0x80 | scancode : scancode,
        };
        if (virtualKey == VkSnapshot)
        {
            // Print Screen 在 Windows 只會收到放開事件
            if (!down)
            {
                _client.SendScancode(true, 0xB7);
                _client.SendScancode(false, 0xB7);
            }
            return;
        }
        if (scancode == 0 && virtualKey != VkPause) return;

        if (down) _pressed.Add(keycode);
        else _pressed.Remove(keycode);
        _client.SendScancode(down, keycode);
    }

    private void ReleaseAllKeys()
    {
        foreach (var k in _pressed) _client?.SendScancode(false, k);
        _pressed.Clear();
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        ReleaseAllKeys();
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        var client = _client;
        if (client is null) return;
        lock (client.FrameLock)
        {
            if (client.Width == 0 || client.Height == 0) return;
            if (_bitmap is null || _bitmap.PixelWidth != client.Width || _bitmap.PixelHeight != client.Height)
            {
                _bitmap = new WriteableBitmap(client.Width, client.Height, 96, 96, PixelFormats.Bgr32, null);
                _bitmap.WritePixels(new Int32Rect(0, 0, client.Width, client.Height), client.FrameBuffer,
                    client.Width * 4, 0);
                client.TryTakeDirty(out _);
                UpdateDestination();
                InvalidateVisual();
            }
            else if (client.TryTakeDirty(out var r))
            {
                _bitmap.WritePixels(new Int32Rect(r.X, r.Y, r.Width, r.Height), client.FrameBuffer,
                    client.Width * 4, (r.Y * client.Width + r.X) * 4);
            }
        }
    }

    private void UpdateDestination()
    {
        if (_bitmap is null) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        double fw = _bitmap.PixelWidth, fh = _bitmap.PixelHeight;
        // 以實體像素計算縮放比例，能整數倍就用整數倍
        var scale = Math.Min(ActualWidth * dpi.DpiScaleX / fw, ActualHeight * dpi.DpiScaleY / fh);
        if (scale >= 1) scale = Math.Floor(scale);
        if (scale <= 0) scale = 1;
        var w = fw * scale / dpi.DpiScaleX;
        var h = fh * scale / dpi.DpiScaleY;
        _dest = new Rect((ActualWidth - w) / 2, (ActualHeight - h) / 2, w, h);
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        UpdateDestination();
        InvalidateVisual();
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        UpdateDestination();
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Black, null, new Rect(RenderSize));
        if (_bitmap is not null) dc.DrawImage(_bitmap, _dest);
    }

    private bool TryMap(Point p, out int x, out int y)
    {
        x = y = 0;
        if (_bitmap is null || _dest.Width <= 0) return false;
        x = (int)((p.X - _dest.X) / _dest.Width * _bitmap.PixelWidth);
        y = (int)((p.Y - _dest.Y) / _dest.Height * _bitmap.PixelHeight);
        var inside = x >= 0 && y >= 0 && x < _bitmap.PixelWidth && y < _bitmap.PixelHeight;
        x = Math.Clamp(x, 0, _bitmap.PixelWidth - 1);
        y = Math.Clamp(y, 0, _bitmap.PixelHeight - 1);
        return inside;
    }

    private static byte ButtonMask(MouseEventArgs e) =>
        (byte)((e.LeftButton == MouseButtonState.Pressed ? 1 : 0)
               | (e.MiddleButton == MouseButtonState.Pressed ? 2 : 0)
               | (e.RightButton == MouseButtonState.Pressed ? 4 : 0));

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var inside = TryMap(e.GetPosition(this), out var x, out var y);
        // XP 會自己畫游標，畫面範圍內隱藏 Windows 游標以免出現兩個
        Cursor = inside && _client is not null ? Cursors.None : Cursors.Arrow;
        if (_client is null || (!inside && !IsMouseCaptured)) return;
        _buttons = ButtonMask(e);
        _client.SendPointer(x, y, _buttons);
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        if (_client is null) return;
        CaptureMouse();
        TryMap(e.GetPosition(this), out var x, out var y);
        _buttons = ButtonMask(e);
        _client.SendPointer(x, y, _buttons);
        e.Handled = true;
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (_client is null) return;
        TryMap(e.GetPosition(this), out var x, out var y);
        _buttons = ButtonMask(e);
        _client.SendPointer(x, y, _buttons);
        if (_buttons == 0) ReleaseMouseCapture();
        e.Handled = true;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (_client is null) return;
        TryMap(e.GetPosition(this), out var x, out var y);
        var wheel = (byte)(e.Delta > 0 ? 8 : 16);
        _client.SendPointer(x, y, (byte)(_buttons | wheel));
        _client.SendPointer(x, y, _buttons);
        e.Handled = true;
    }
}
