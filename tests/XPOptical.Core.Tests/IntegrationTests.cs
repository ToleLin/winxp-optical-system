using System.Diagnostics;
using System.Text.Json;
using XPOptical.Core.Diagnostics;
using XPOptical.Core.Display;
using XPOptical.Core.Profiles;
using XPOptical.Core.Qemu;
using XPOptical.Core.Usb;
using XPOptical.Core.Vm;
using Xunit.Abstractions;

namespace XPOptical.Core.Tests;

[CollectionDefinition("qemu", DisableParallelization = true)]
public class QemuCollection;

/// <summary>
/// 以真正的 QEMU（軟體模擬）驅動完整流程。磁碟是空的（沒有 XP），所以驗證的是機制：
/// 開機、畫面、USB 接入/拔除/重新接入、休眠存檔、恢復、關機逾時、強制關閉。
/// usbredirect 以假程式代替（雲端沒有實體 USB）。
/// </summary>
[Collection("qemu")]
public sealed class IntegrationTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);
    private static readonly UsbDeviceInfo FakeIhr320 = new(0x1234, 0x5678, @"USB\VID_1234&PID_5678\SN1", "Fake iHR320");

    private readonly ITestOutputHelper _output;
    private readonly TempDir _dir = new();
    private readonly FakeUsbEnumerator _usb = new();
    private readonly string _control;
    private readonly TimeSpan _originalShutdownTimeout = AppController.ShutdownTimeout;

    public IntegrationTests(ITestOutputHelper output)
    {
        _output = output;
        _control = Path.Combine(_dir.Path, "fake-usb");
        Directory.CreateDirectory(_control);
    }

    public void Dispose()
    {
        AppController.ShutdownTimeout = _originalShutdownTimeout;
        var log = Path.Combine(_dir.Path, "logs", "test.log");
        if (File.Exists(log)) _output.WriteLine(File.ReadAllText(log));
        _dir.Dispose();
    }

    private string Present => Path.Combine(_control, "present");
    private string Fail => Path.Combine(_control, "fail");
    private string Connections => Path.Combine(_control, "connections.log");

    /// <summary>建立免安裝資料夾：假的 usbredirect、使用者設定（勾選假 iHR320）、空白 XP 磁碟。</summary>
    private AppController CreateApp(bool installed = true)
    {
        var bin = Path.Combine(_dir.Path, "engine", "bin");
        Directory.CreateDirectory(bin);
        var fake = Path.Combine(bin, "usbredirect");
        File.WriteAllText(fake, $$"""
            #!/usr/bin/env python3
            import os, socket, sys, time
            ctl = {{JsonSerializer.Serialize(_control)}}
            args = sys.argv[1:]
            device = args[args.index("--device") + 1]
            host, port = args[args.index("--to") + 1].rsplit(":", 1)
            if os.path.exists(os.path.join(ctl, "fail")):
                print("Failed to open device!", file=sys.stderr, flush=True)
                sys.exit(1)
            s = socket.create_connection((host, int(port)))
            with open(os.path.join(ctl, "connections.log"), "a") as f:
                f.write(device + "\n")
            while os.path.exists(os.path.join(ctl, "present")):
                time.sleep(0.05)
            """);
        File.SetUnixFileMode(fake, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        new AppSettings
        {
            UsbDevices = [new UsbDeviceRule { Name = "iHR320", Match = [new UsbId(0x1234, 0x5678)] }],
        }.Save(Path.Combine(_dir.Path, "settings.json"));

        var paths = new AppPaths(_dir.Path);
        var app = new AppController(paths, _usb, new FileLog(Path.Combine(paths.LogsDir, "test.log")));
        Assert.NotNull(app.Engine);
        Assert.Equal(fake, app.Engine!.UsbRedirect);
        if (installed)
        {
            RunQemuImg(app.Engine.QemuImg, paths.VmDir, "create", "-f", "qcow2", "base.qcow2", "64M");
            app.Store.MarkInstalled();
        }
        return app;
    }

    private static void RunQemuImg(string qemuImg, string dir, params string[] args)
    {
        Directory.CreateDirectory(dir);
        var psi = new ProcessStartInfo(qemuImg) { WorkingDirectory = dir, RedirectStandardOutput = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.WaitForExit();
        Assert.Equal(0, p.ExitCode);
    }

    private static async Task<List<string>> PeripheralsAsync(AppController app)
    {
        var list = await app.Vm!.Qmp!.ExecuteAsync("qom-list", new { path = "/machine/peripheral" });
        return list.EnumerateArray().Select(e => e.GetProperty("name").GetString()!).ToList();
    }

    private static UsbDeviceStatus UsbStatus(AppController app) => Assert.Single(app.Usb.GetStatus());

    [SkippableFact]
    public async Task FullLifecycle()
    {
        Skip.IfNot(QemuTest.Available, "需要 qemu-system-i386");
        await using var app = CreateApp();
        File.WriteAllText(Present, "");
        _usb.Set(FakeIhr320);

        // 1. 冷開機
        var started = await app.StartAsync();
        Assert.False(started.Resumed);
        Assert.Equal(VmState.Running, app.VmState);
        Assert.True(File.Exists(app.Store.SystemDisk));

        // 2. XP 畫面（此處是 BIOS 畫面）可以透過 VNC 取得，鍵盤滑鼠可送出
        await using (var rfb = await RfbClient.ConnectAsync(app.Vm!.VncPort, Wait))
        {
            await QemuTest.WaitUntilAsync(() =>
            {
                lock (rfb.FrameLock) return rfb.FrameBuffer.Any(b => b != 0);
            }, Wait, "畫面出現內容");
            Assert.True(rfb.Width >= 640, $"解析度 {rfb.Width}x{rfb.Height}");
            await QemuTest.WaitUntilAsync(() => rfb.ExtendedKeyEventSupported, Wait, "QEMU 擴充按鍵事件");
            rfb.SendPointer(100, 100, 0);
            rfb.SendScancode(true, 0x1E, 'a');
            rfb.SendScancode(false, 0x1E, 'a');
            rfb.SendCtrlAltDel();
            Assert.False(rfb.Completion.IsCompleted);
        }

        // 3. 符合規則的 USB 裝置自動接入 XP
        await QemuTest.WaitUntilAsync(() => UsbStatus(app).State == UsbLinkState.Connected, Wait, "USB 已連線");
        Assert.Contains("usbdev1", await PeripheralsAsync(app));
        Assert.Equal("1234:5678", File.ReadAllLines(Connections).Single());

        // 4. 拔除：usbredirect 結束 -> 從 XP 移除
        File.Delete(Present);
        _usb.Set();
        await QemuTest.WaitUntilAsync(async () => !(await PeripheralsAsync(app)).Contains("usbdev1"), Wait, "從 XP 移除");
        await QemuTest.WaitUntilAsync(() => UsbStatus(app).State == UsbLinkState.NotPresent, Wait, "顯示未偵測到");

        // 5. 重新插上：自動再接入
        File.WriteAllText(Present, "");
        _usb.Set(FakeIhr320);
        app.NotifyDevicesChanged();
        await QemuTest.WaitUntilAsync(() => UsbStatus(app).State == UsbLinkState.Connected, Wait, "重新連線");
        Assert.Contains("usbdev2", await PeripheralsAsync(app));

        // 6. 關閉視窗 = 休眠：USB 先移除，記憶體狀態存檔
        var generationBeforeSave = app.Store.Meta.Generation;
        Assert.Equal(CloseOutcome.Closed, await app.CloseAsync());
        Assert.Equal(VmState.Stopped, app.VmState);
        Assert.True(File.Exists(app.Store.StateFile));
        Assert.Equal(generationBeforeSave, app.Store.ReadSavedState()!.Generation);
        Assert.True(app.CanResume);

        // 7. 再開啟 = 從休眠恢復；休眠檔立即作廢，USB 重新接入
        var resumed = await app.StartAsync();
        Assert.True(resumed.Resumed, resumed.ResumeFailure);
        Assert.Null(app.Store.ReadSavedState());
        Assert.Equal(generationBeforeSave + 1, app.Store.Meta.Generation);
        await QemuTest.WaitUntilAsync(() => UsbStatus(app).State == UsbLinkState.Connected, Wait, "恢復後重新連線");
        Assert.Single(await PeripheralsAsync(app), p => p.StartsWith("usbdev"));

        // 8. 正常關機：沒有作業系統回應電源鍵 -> 逾時，XP 繼續執行且 USB 接回
        AppController.ShutdownTimeout = TimeSpan.FromSeconds(2);
        Assert.Equal(CloseOutcome.ShutdownTimedOut, await app.ShutdownAsync());
        Assert.Equal(VmState.Running, app.VmState);
        await QemuTest.WaitUntilAsync(() => UsbStatus(app).State == UsbLinkState.Connected, Wait, "關機逾時後重新連線");

        // 9. 強制關閉
        await app.PowerOffAsync();
        Assert.Equal(VmState.Stopped, app.VmState);
        Assert.False(app.CanResume);
        await QemuTest.WaitUntilAsync(() => UsbStatus(app).State == UsbLinkState.Idle, Wait, "停止後顯示閒置");
    }

    [SkippableFact]
    public async Task ChangingRamDiscardsSavedState()
    {
        Skip.IfNot(QemuTest.Available, "需要 qemu-system-i386");
        await using var app = CreateApp();
        await app.StartAsync();
        await app.CloseAsync();
        Assert.True(app.CanResume);

        // 換了記憶體大小 -> 組成不同 -> 冷開機並作廢舊休眠檔
        app.Settings.RamMB = 256;
        var started = await app.StartAsync();
        Assert.False(started.Resumed);
        Assert.False(app.CanResume);
        await app.PowerOffAsync();
    }

    [SkippableFact]
    public async Task UsbOpenFailureIsReportedInChineseAndRetried()
    {
        Skip.IfNot(QemuTest.Available, "需要 qemu-system-i386");
        await using var app = CreateApp();
        File.WriteAllText(Fail, "");
        File.WriteAllText(Present, "");
        _usb.Set(FakeIhr320);
        await app.StartAsync();

        await QemuTest.WaitUntilAsync(() => UsbStatus(app).State == UsbLinkState.Failed, Wait, "顯示失敗");
        Assert.Contains("無法開啟裝置", UsbStatus(app).Detail);
        await QemuTest.WaitUntilAsync(async () => (await PeripheralsAsync(app)).All(p => !p.StartsWith("usbdev")),
            Wait, "失敗的插槽已移除");

        File.Delete(Fail);
        await QemuTest.WaitUntilAsync(() => UsbStatus(app).State == UsbLinkState.Connected, Wait, "重試後連線");
        await app.PowerOffAsync();
    }

    [SkippableFact]
    public async Task GuestInitiatedExitIsReported()
    {
        Skip.IfNot(QemuTest.Available, "需要 qemu-system-i386");
        await using var app = CreateApp();
        var exited = new TaskCompletionSource<int>();
        app.EngineExited += code => exited.TrySetResult(code);
        await app.StartAsync();

        // 等同在 XP 開始功能表選「關機」：引擎自行結束
        // quit 時引擎可能在回覆前就關閉連線
        try { await app.Vm!.Qmp!.ExecuteAsync("quit"); }
        catch (IOException) { }
        await exited.Task.WaitAsync(Wait);
        await QemuTest.WaitUntilAsync(() => app.VmState == VmState.Stopped, Wait, "狀態變為停止");
    }

    [SkippableFact]
    public async Task InstallFromIsoThenComplete()
    {
        Skip.IfNot(QemuTest.Available, "需要 qemu-system-i386");
        await using var app = CreateApp(installed: false);
        var iso = Path.Combine(_dir.Path, "xp,安裝光碟.iso");
        File.WriteAllBytes(iso, new byte[1024 * 1024]);

        await app.StartInstallAsync(iso);
        Assert.True(app.InstallMode);
        // 光碟路徑含中文，引擎讀不到 -> 自動複製到 vm 資料夾
        Assert.Equal(Path.Combine(app.Store.VmDir, "install.iso"), app.Vm!.CurrentSpec!.CdromImage);
        Assert.Equal(new FileInfo(iso).Length, new FileInfo(app.Vm.CurrentSpec.CdromImage!).Length);
        Assert.False(app.Store.IsInstalled);

        // 安裝中關閉視窗：無法休眠，直接關閉
        Assert.Equal(CloseOutcome.Closed, await app.CloseAsync());
        Assert.False(app.CanResume);

        await app.StartInstallAsync(iso);
        AppController.ShutdownTimeout = TimeSpan.FromSeconds(2);
        Assert.Equal(CloseOutcome.ShutdownTimedOut, await app.CompleteInstallAsync());
        await app.PowerOffAsync();
        Assert.Equal(CloseOutcome.Closed, await app.CompleteInstallAsync());
        Assert.True(app.Store.IsInstalled);
        Assert.False(app.InstallMode);
    }

    [SkippableFact]
    public async Task ImportExistingImageReportsProgress()
    {
        Skip.IfNot(QemuTest.Available, "需要 qemu-system-i386");
        await using var app = CreateApp(installed: false);
        var source = Path.Combine(_dir.Path, "舊電腦硬碟.img");
        await using (var f = File.Create(source)) f.SetLength(32 * 1024 * 1024);

        var reports = new List<double>();
        await app.ImportImageAsync(source, new SyncProgress(reports.Add));

        Assert.True(app.Store.IsInstalled);
        Assert.Equal(100, reports.Last());
        Assert.True(File.Exists(app.Store.BaseDisk));
    }

    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
