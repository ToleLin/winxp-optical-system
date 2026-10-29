using System.Diagnostics;
using XPOptical.Core.Diagnostics;

namespace XPOptical.Core.Qemu;

/// <summary>執行引擎的行程：保留錯誤輸出供判斷加速方式與排除問題。</summary>
public sealed class QemuProcess : IDisposable
{
    private readonly Process _process;
    private readonly List<string> _stderr = [];
    private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private QemuProcess(Process process) => _process = process;

    /// <param name="workingDirectory">QEMU 收到的相對路徑以此為基準（見 <see cref="SafePath"/>）。</param>
    public static QemuProcess Start(string exe, IEnumerable<string> args, string workingDirectory, ILog log)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            WorkingDirectory = workingDirectory,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        log.Info($"啟動執行引擎：{exe} {string.Join(' ', psi.ArgumentList)}");
        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var qp = new QemuProcess(process);
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (qp._stderr) qp._stderr.Add(e.Data);
            log.Warn($"[qemu] {e.Data}");
        };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) log.Info($"[qemu] {e.Data}"); };
        process.Exited += (_, _) =>
        {
            // 確保非同步輸出已讀完，再回報結束
            process.WaitForExit();
            qp._exited.TrySetResult(process.ExitCode);
        };
        if (!process.Start()) throw new InvalidOperationException("無法啟動執行引擎");
        process.BeginErrorReadLine();
        process.BeginOutputReadLine();
        return qp;
    }

    public Task<int> Exited => _exited.Task;
    public bool HasExited => _exited.Task.IsCompleted;

    public IReadOnlyList<string> StandardError
    {
        get { lock (_stderr) return _stderr.ToList(); }
    }

    /// <summary>硬體加速失敗、改用軟體模擬時 QEMU 會輸出 "falling back to tcg"（一開始就指定 tcg 時不會輸出）。</summary>
    public bool FellBackToSoftwareEmulation =>
        StandardError.Any(l => l.Contains("falling back to tcg", StringComparison.OrdinalIgnoreCase));

    public void Kill()
    {
        try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
    }

    public void Dispose()
    {
        Kill();
        _process.Dispose();
    }
}
