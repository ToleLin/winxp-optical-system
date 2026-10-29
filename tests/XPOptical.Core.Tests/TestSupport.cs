using XPOptical.Core.Qemu;
using XPOptical.Core.Usb;

namespace XPOptical.Core.Tests;

public sealed class TempDir : IDisposable
{
    public TempDir()
    {
        // 路徑刻意包含空白與中文，確認不會被引擎參數解析弄錯
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"xpo 測試 {Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); }
        catch (IOException) { }
    }
}

public sealed class FakeUsbEnumerator : IUsbEnumerator
{
    private volatile IReadOnlyList<UsbDeviceInfo> _devices = [];

    public void Set(params UsbDeviceInfo[] devices) => _devices = devices;
    public IReadOnlyList<UsbDeviceInfo> GetPresentDevices() => _devices;
}

public static class QemuTest
{
    /// <summary>測試環境是否有 QEMU（qemu-system-x86_64 或 i386）與 qemu-img（從 PATH 尋找）。</summary>
    public static bool Available { get; } = EngineLocator.Find("/nonexistent") is not null;

    public static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, string what)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"等待逾時：{what}");
            await Task.Delay(50);
        }
    }

    public static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout, string what)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"等待逾時：{what}");
            await Task.Delay(50);
        }
    }
}
