using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using XPOptical.Core;
using XPOptical.Core.Diagnostics;
using XPOptical.Core.Usb;

namespace XPOptical.App;

public partial class App : Application
{
    private Mutex? _singleInstance;
    private ILog _log = NullLog.Instance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var paths = new AppPaths(AppContext.BaseDirectory);
        _log = new FileLog(Path.Combine(paths.LogsDir, $"XPOptical-{DateTime.Now:yyyyMMdd}.log"));
        DispatcherUnhandledException += OnUnhandledException;

        // 同一個資料夾只能開一個，避免兩個程式同時使用同一份 XP 磁碟
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(paths.DataRoot.ToUpperInvariant())))[..16];
        _singleInstance = new Mutex(true, $"Local\\XPOptical-{key}", out var isFirst);
        if (!isFirst)
        {
            MessageBox.Show("XP 光學系統已經在執行中。", "XP 光學系統", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        _log.Info($"程式啟動，資料夾：{paths.DataRoot}");
        var controller = new AppController(paths, new WindowsUsbEnumerator(), _log);
        MainWindow = new MainWindow(controller, _log);
        MainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _log.Error("未預期的錯誤", e.Exception);
        MessageBox.Show($"發生未預期的錯誤：\n{e.Exception.Message}\n\n詳細內容已記錄在 logs 資料夾。",
            "XP 光學系統", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
