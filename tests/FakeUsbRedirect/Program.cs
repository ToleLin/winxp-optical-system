using System.Net.Sockets;

// 行為與真正的 usbredirect 相同的部分：解析 --device / --to、連到 QEMU 的 usb-redir socket，
// 裝置拔除（present 檔不存在）時結束。控制資料夾的位置寫在執行檔旁的 usbredirect.ctl。
var ctl = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "usbredirect.ctl")).Trim();
var device = args[Array.IndexOf(args, "--device") + 1];
var to = args[Array.IndexOf(args, "--to") + 1];
var sep = to.LastIndexOf(':');

if (File.Exists(Path.Combine(ctl, "fail")))
{
    Console.Error.WriteLine("Failed to open device!");
    return 1;
}

using var socket = new TcpClient(to[..sep], int.Parse(to[(sep + 1)..]));
File.AppendAllText(Path.Combine(ctl, "connections.log"), device + "\n");
while (File.Exists(Path.Combine(ctl, "present")))
    Thread.Sleep(50);
return 0;
