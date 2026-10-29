using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using XPOptical.Core;
using XPOptical.Core.Diagnostics;
using XPOptical.Core.Profiles;
using XPOptical.Core.Usb;
using XPOptical.Core.Vm;

namespace XPOptical.App;

public partial class MainWindow : Window
{
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;
    private const int WmDeviceChange = 0x0219;
    private const string AppTitle = "XP 光學系統";

    private readonly AppController _app;
    private readonly ILog _log;
    private string? _busyText;
    private string? _installIso;
    private bool _readyToClose;
    private bool _usbDkMissing;

    public MainWindow(AppController app, ILog log)
    {
        _app = app;
        _log = log;
        InitializeComponent();
        _app.Changed += () => Dispatcher.InvokeAsync(RefreshUi);
        _app.EngineExited += _ => Dispatcher.InvokeAsync(OnEngineExitedAsync);
        Display.Disconnected += () => Dispatcher.InvokeAsync(RefreshUi);
        Loaded += OnLoadedAsync;
        Closing += OnClosing;
    }

    private async void OnLoadedAsync(object sender, RoutedEventArgs e)
    {
        HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(WndProc);
        _usbDkMissing = !WindowsSetup.IsUsbDkInstalled();
        RefreshUi();
        // 已建立 XP 環境時，打開程式就直接開啟 XP
        if (_app.Engine is not null && _app.Store.IsInstalled) await StartVmAsync();
        // 安裝檔帶入了使用者的 xp.iso：直接進入自動安裝（只需輸入序號）
        else if (_app.Engine is not null && !_app.HasUnfinishedInstall && File.Exists(_app.BundledXpIso))
            await AutoInstallAsync();
    }

    // ───────────── 畫面狀態 ─────────────

    private void RefreshUi()
    {
        var state = _app.VmState;
        var running = state == VmState.Running;
        var stopped = state == VmState.Stopped;

        MissingEnginePanel.Visibility = Show(_app.Engine is null);
        SetupPanel.Visibility = Show(_app.Engine is not null && stopped && !_app.Store.IsInstalled);
        ContinueInstallButton.Visibility = Show(_app.HasUnfinishedInstall);
        AutoInstallHint.Text = File.Exists(_app.BundledXpIso)
            ? "已找到程式資料夾中的 xp.iso，只要輸入產品序號，其餘全部自動完成。"
            : "選擇 XP 安裝光碟（ISO 檔）並輸入產品序號，其餘全部自動完成。";
        StoppedPanel.Visibility = Show(_app.Engine is not null && stopped && _app.Store.IsInstalled);
        StoppedHint.Text = _app.CanResume ? "將回到上次關閉時的畫面。" : "XP 會重新開機。";
        Display.Visibility = Show(!stopped && Display.IsConnected);
        BusyPanel.Visibility = Show(_busyText is not null);
        BusyText.Text = _busyText ?? "";

        StartButton.IsEnabled = stopped && _app.Store.IsInstalled && _busyText is null;
        CtrlAltDelButton.IsEnabled = running;
        MediaButton.IsEnabled = running && !_app.InstallMode && _busyText is null;
        ShutdownButton.IsEnabled = running && !_app.InstallMode;
        InstallDoneButton.Visibility = Show(_app.InstallMode && running);
        SettingsButton.IsEnabled = _busyText is null;

        VmStatusText.Text = state switch
        {
            VmState.Running when _app.UnattendedInstall => "XP：自動安裝中（約 30–90 分鐘，完成後自動進入桌面）",
            VmState.Running when _app.InstallMode => "XP：安裝中",
            VmState.Running => "XP：執行中",
            VmState.Starting => "XP：開啟中…",
            VmState.Saving => "XP：保存中…",
            VmState.ShuttingDown => "XP：關機中…",
            _ => "XP：已關機",
        };
        RefreshUsbStatus();
        RefreshFixHint(running);
    }

    private void RefreshUsbStatus()
    {
        var all = _app.Usb.GetStatus();
        var status = all.FirstOrDefault();
        (Brush dot, string text, string? tip) = (status, _app.Usb.IsAvailable) switch
        {
            (_, false) => (Brushes.Gray, "USB 轉送元件缺少（engine 資料夾不完整）", null),
            (null, _) => (Brushes.Gray, "iHR320：尚未設定，請到「設定」選擇裝置", null),
            ({ State: UsbLinkState.Connected }, _) => (Brushes.LimeGreen, $"{status.Name}：已連線", null),
            ({ State: UsbLinkState.Connecting }, _) => (Brushes.Gold, $"{status.Name}：連線中…", null),
            ({ State: UsbLinkState.NotPresent }, _) =>
                (Brushes.Gray, $"{status.Name}：未偵測到（請確認 USB 線與電源）", null),
            ({ State: UsbLinkState.Failed }, _) => (Brushes.OrangeRed, $"{status.Name}：連線失敗", status.Detail),
            _ => (Brushes.Gray, $"{status.Name}：XP 未開啟", null),
        };
        var others = all.Skip(1).Count(s => s.State == UsbLinkState.Connected);
        if (others > 0) text += $"　（另有 {others} 個 USB 裝置連到 XP）";
        UsbDot.Fill = dot;
        UsbStatusText.Text = text;
        UsbStatusText.ToolTip = tip;
    }

    private void RefreshFixHint(bool running)
    {
        string? fix = null;
        if (_usbDkMissing && OperatingSystem.IsWindows()) fix = "安裝 USB 驅動";

        AccelText.Text = running ? (_app.Vm!.SoftwareEmulation ? "軟體模擬" : "硬體加速") : "";
        AccelSeparator.Visibility = Show(running);
        FixButton.Content = fix;
        FixButton.Visibility = Show(fix is not null);
        FixButton.ToolTip = fix switch
        {
            "安裝 USB 驅動" => "第一次使用需要安裝 UsbDk，iHR320 才能連到 XP（需要系統管理員權限，只需一次）",
            _ => null,
        };
    }

    private static Visibility Show(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;

    private async Task RunBusyAsync(string text, Func<Task> action)
    {
        _busyText = text;
        RefreshUi();
        try
        {
            await action();
        }
        finally
        {
            _busyText = null;
            RefreshUi();
        }
    }

    // ───────────── 開啟 / 關閉 ─────────────

    private async Task StartVmAsync()
    {
        try
        {
            await RunBusyAsync(_app.CanResume ? "正在回到上次的畫面…" : "正在開啟 XP…", async () =>
            {
                var result = await _app.StartAsync();
                await Display.ConnectAsync(_app.Vm!.VncPort);
                if (result.ResumeFailure is not null)
                    Info("無法回到上次關閉時的畫面，已重新開機 XP。");
            });
        }
        catch (Exception ex)
        {
            _log.Error("開啟 XP 失敗", ex);
            Error($"無法開啟 XP：\n{ex.Message}");
        }
    }

    private async void OnStartClick(object sender, RoutedEventArgs e) => await StartVmAsync();

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_readyToClose || _app.VmState == VmState.Stopped) return;
        e.Cancel = true;
        if (_busyText is null) _ = CloseFlowAsync();
    }

    private async Task CloseFlowAsync()
    {
        if (_app.InstallMode &&
            !Confirm("XP 還在安裝中。現在關閉的話，下次開啟可以選擇「繼續先前未完成的安裝」。\n\n確定要關閉嗎？"))
            return;

        var hibernate = !_app.InstallMode && _app.Settings.EffectiveCloseAction(_app.Profile) == CloseAction.Hibernate;
        try
        {
            CloseOutcome outcome = CloseOutcome.Closed;
            await RunBusyAsync(hibernate ? "正在保存 XP 狀態，請稍候…" : "正在關閉 XP…", async () =>
            {
                await Display.DisconnectAsync();
                outcome = await _app.CloseAsync();
            });
            if (outcome == CloseOutcome.ShutdownTimedOut && !await AskForcePowerOffAsync())
            {
                await ReconnectDisplayAsync();
                return;
            }
        }
        catch (HibernateIncompleteException ex)
        {
            Info(ex.Message);
        }
        catch (Exception ex)
        {
            _log.Error("保存 XP 狀態失敗", ex);
            await ReconnectDisplayAsync();
            if (!Confirm($"無法保存 XP 狀態：\n{ex.Message}\n\n要改為讓 XP 正常關機嗎？")) return;
            if (!await ShutdownGuestAsync()) return;
        }
        _readyToClose = true;
        Close();
    }

    /// <summary>XP 沒有回應關機要求時詢問是否強制關閉；回傳 XP 是否已停止。</summary>
    private async Task<bool> AskForcePowerOffAsync()
    {
        if (!Confirm("XP 沒有回應關機要求（可能有程式正在等待儲存）。\n\n要強制關閉嗎？未儲存的資料會遺失。")) return false;
        await RunBusyAsync("正在關閉 XP…", _app.PowerOffAsync);
        return true;
    }

    private async Task<bool> ShutdownGuestAsync()
    {
        CloseOutcome outcome = CloseOutcome.Closed;
        await RunBusyAsync("正在關閉 XP…", async () =>
        {
            await Display.DisconnectAsync();
            outcome = await _app.ShutdownAsync();
        });
        if (outcome == CloseOutcome.Closed) return true;
        if (await AskForcePowerOffAsync()) return true;
        await ReconnectDisplayAsync();
        return false;
    }

    private async Task ReconnectDisplayAsync()
    {
        if (_app.IsRunning && !Display.IsConnected) await Display.ConnectAsync(_app.Vm!.VncPort);
        RefreshUi();
    }

    private async void OnShutdownClick(object sender, RoutedEventArgs e)
    {
        if (!Confirm("要讓 XP 正常關機嗎？")) return;
        await ShutdownGuestAsync();
    }

    private async Task OnEngineExitedAsync()
    {
        await Display.DisconnectAsync();
        if (_app.TryCompleteUnattendedInstall())
        {
            var iso = new FileInfo(_app.BundledXpIso);
            var isoNote = iso.Exists
                ? $"\n\n程式資料夾裡的 xp.iso（{iso.Length / (1024 * 1024)} MB）只有重新安裝 XP 時才會用到，要節省空間可以刪除。"
                : "";
            Info("XP 安裝完成！\n\n接下來會開啟 XP。請把光學軟體與 iHR320 驅動的安裝檔資料夾拖進視窗，在 XP 裡安裝。" + isoNote);
            await StartVmAsync();
            return;
        }
        if (_app.InstallMode)
        {
            if (Confirm("XP 已關機。\n\nXP 安裝完成了嗎？（已經看到 XP 桌面就是完成了）"))
                await _app.CompleteInstallAsync();
            else
                Info("下次可以按「繼續先前未完成的安裝」接著安裝。");
        }
        else
        {
            StoppedTitle.Text = "XP 已關機";
        }
        RefreshUi();
    }

    // ───────────── 建立 XP 環境 ─────────────

    private async void OnAutoInstallClick(object sender, RoutedEventArgs e) => await AutoInstallAsync();

    private async Task AutoInstallAsync()
    {
        if (_app.HasUnfinishedInstall &&
            !Confirm("重新安裝會清除先前未完成的安裝。確定要從頭開始嗎？")) return;
        var dlg = new InstallXpWindow(File.Exists(_app.BundledXpIso) ? _app.BundledXpIso : null) { Owner = this };
        if (dlg.ShowDialog() != true) return;

        _installIso = dlg.IsoPath;
        try
        {
            await RunBusyAsync("正在準備自動安裝 XP…", async () =>
            {
                await _app.StartUnattendedInstallAsync(dlg.IsoPath, dlg.ProductKey);
                await Display.ConnectAsync(_app.Vm!.VncPort);
            });
            Info("XP 開始自動安裝。\n\n" +
                 "• 約需 30–90 分鐘，期間會重新開機數次，不需要操作。\n" +
                 "• 出現「Press any key to boot from CD」時不要按任何鍵。\n" +
                 "• 可以最小化視窗，但請不要關閉程式；完成後會自動進入 XP 桌面。");
        }
        catch (Exception ex)
        {
            _log.Error("開始自動安裝失敗", ex);
            Error($"無法開始自動安裝：\n{ex.Message}");
        }
    }

    private async void OnInstallClick(object sender, RoutedEventArgs e)
    {
        var iso = PickIso();
        if (iso is null) return;
        if (_app.HasUnfinishedInstall &&
            !Confirm("重新安裝會清除先前未完成的安裝。確定要從頭開始嗎？")) return;
        await StartInstallAsync(iso, freshDisk: true);
    }

    private async void OnContinueInstallClick(object sender, RoutedEventArgs e)
    {
        var iso = _installIso ?? (File.Exists(_app.BundledXpIso) ? _app.BundledXpIso : PickIso());
        if (iso is null) return;
        await StartInstallAsync(iso, freshDisk: false);
    }

    private string? PickIso()
    {
        var dlg = new OpenFileDialog
        {
            Title = "選擇 Windows XP 安裝光碟的 ISO 檔",
            Filter = "光碟映像檔 (*.iso)|*.iso|所有檔案 (*.*)|*.*",
        };
        return dlg.ShowDialog(this) == true ? dlg.FileName : null;
    }

    private async Task StartInstallAsync(string iso, bool freshDisk)
    {
        _installIso = iso;
        try
        {
            await RunBusyAsync("正在準備安裝 XP…", async () =>
            {
                await _app.StartInstallAsync(iso, freshDisk);
                await Display.ConnectAsync(_app.Vm!.VncPort);
            });
            Info("XP 安裝程式即將出現。\n\n" +
                 "• 照著 XP 安裝畫面操作（選擇磁碟、輸入序號等）。\n" +
                 "• 安裝過程會自動重新開機數次，出現「Press any key to boot from CD」時不要按任何鍵。\n" +
                 "• 看到 XP 桌面後，按工具列的「✔ XP 安裝完成」。");
        }
        catch (Exception ex)
        {
            _log.Error("開始安裝失敗", ex);
            Error($"無法開始安裝：\n{ex.Message}");
        }
    }

    private async void OnInstallDoneClick(object sender, RoutedEventArgs e)
    {
        if (!Confirm("確定 XP 已安裝完成（已經看到 XP 桌面）了嗎？\n\nXP 會先關機，之後就可以開始使用。")) return;
        CloseOutcome outcome = CloseOutcome.Closed;
        await RunBusyAsync("正在關閉 XP…", async () =>
        {
            await Display.DisconnectAsync();
            outcome = await _app.CompleteInstallAsync();
        });
        if (outcome == CloseOutcome.ShutdownTimedOut)
        {
            if (await AskForcePowerOffAsync()) await _app.CompleteInstallAsync();
            else await ReconnectDisplayAsync();
        }
        if (_app.Store.IsInstalled)
            Info("XP 環境建立完成！\n\n接下來按「開啟 XP」，再把光學軟體與 iHR320 驅動安裝到 XP 裡。");
        RefreshUi();
    }

    private async void OnImportClick(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "選擇 XP 磁碟映像",
            Filter = "磁碟映像 (*.qcow2;*.vhd;*.vhdx;*.vmdk;*.img;*.raw)|*.qcow2;*.vhd;*.vhdx;*.vmdk;*.img;*.raw|所有檔案 (*.*)|*.*",
        };
        if (dlg.ShowDialog(this) != true) return;

        ImportProgress.Value = 0;
        ImportProgress.Visibility = Visibility.Visible;
        ImportButton.IsEnabled = InstallButton.IsEnabled = AutoInstallButton.IsEnabled = false;
        try
        {
            await _app.ImportImageAsync(dlg.FileName, new Progress<double>(p => ImportProgress.Value = p));
            Info("XP 磁碟映像匯入完成。");
            await StartVmAsync();
        }
        catch (Exception ex)
        {
            _log.Error("匯入映像失敗", ex);
            Error($"無法匯入磁碟映像：\n{ex.Message}");
        }
        finally
        {
            ImportProgress.Visibility = Visibility.Collapsed;
            ImportButton.IsEnabled = InstallButton.IsEnabled = AutoInstallButton.IsEnabled = true;
            RefreshUi();
        }
    }

    // ───────────── 放入檔案（光碟） ─────────────

    private void OnMediaClick(object sender, RoutedEventArgs e)
    {
        var menu = MediaButton.ContextMenu!;
        menu.PlacementTarget = MediaButton;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private async void OnInsertFolderClick(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "選擇要放進 XP 的資料夾（例如光學軟體的安裝檔）" };
        if (dlg.ShowDialog(this) == true) await InsertAsync([dlg.FolderName]);
    }

    private async void OnInsertIsoClick(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "選擇光碟映像（ISO 檔）",
            Filter = "光碟映像檔 (*.iso)|*.iso|所有檔案 (*.*)|*.*",
        };
        if (dlg.ShowDialog(this) == true) await InsertAsync([dlg.FileName]);
    }

    private async void OnEjectClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await _app.EjectMediaAsync();
        }
        catch (Exception ex)
        {
            Error($"無法退出光碟：\n{ex.Message}");
        }
        Display.Focus();
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } items) return;
        if (!_app.IsRunning || _app.InstallMode)
        {
            Info("請先開啟 XP，再把檔案拖進來。");
            return;
        }
        await InsertAsync(items);
    }

    private async Task InsertAsync(IReadOnlyList<string> items)
    {
        try
        {
            await RunBusyAsync("正在把檔案放進 XP 的光碟機…", () => _app.InsertItemsAsync(items));
            Info("已放進 XP 的光碟機。\n\n請在 XP 的「我的電腦」開啟光碟機；用完可從「放入檔案 ▼」選「退出光碟」。");
        }
        catch (Exception ex)
        {
            _log.Error("放入檔案失敗", ex);
            Error($"無法放入檔案：\n{ex.Message}");
        }
        Display.Focus();
    }

    // ───────────── 其他按鈕 ─────────────

    private void OnCtrlAltDelClick(object sender, RoutedEventArgs e)
    {
        Display.SendCtrlAltDel();
        Display.Focus();
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        var dlg = new SettingsWindow(_app) { Owner = this };
        if (dlg.ShowDialog() == true) _app.SaveSettings();
        RefreshUi();
        if (Display.IsConnected) Display.Focus();
    }

    private async void OnFixClick(object sender, RoutedEventArgs e)
    {
        if (_usbDkMissing)
        {
            var msi = WindowsSetup.FindUsbDkInstaller(_app.Paths.DriversDir);
            if (msi is null)
            {
                Error("drivers 資料夾裡找不到 UsbDk 安裝檔。請確認程式資料夾完整。");
                return;
            }
            if (await WindowsSetup.InstallUsbDkAsync(msi))
            {
                _usbDkMissing = !WindowsSetup.IsUsbDkInstalled();
                Info(_usbDkMissing ? "USB 驅動安裝未完成，請再試一次。" : "USB 驅動安裝完成。");
                _app.NotifyDevicesChanged();
            }
        }
        RefreshUi();
    }

    // ───────────── 鍵盤與裝置通知 ─────────────

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WmKeyDown or WmKeyUp or WmSysKeyDown or WmSysKeyUp when Display.IsKeyboardFocused:
                var lp = lParam.ToInt64();
                var scancode = (uint)((lp >> 16) & 0xFF);
                var extended = ((lp >> 24) & 1) != 0;
                Display.SendKey(msg is WmKeyDown or WmSysKeyDown, wParam.ToInt32(), scancode, extended);
                handled = true;
                break;
            case WmDeviceChange:
                _app.NotifyDevicesChanged();
                break;
        }
        return IntPtr.Zero;
    }

    // ───────────── 對話框 ─────────────

    private bool Confirm(string message) =>
        MessageBox.Show(this, message, AppTitle, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    private void Info(string message) =>
        MessageBox.Show(this, message, AppTitle, MessageBoxButton.OK, MessageBoxImage.Information);

    private void Error(string message) =>
        MessageBox.Show(this, message, AppTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
}
