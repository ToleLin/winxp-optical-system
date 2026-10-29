# Phase 0：可行性驗證步驟（iHR320）

在開發主程式之前，先用**現成工具**在實際的 Win11 電腦 + iHR320 上手動跑一次，確認整條路線可行，並取得之後要對照的數據。
這份文件給負責測試的人使用；正式產品會把以下所有步驟自動化，使用者不需要接觸指令。

> 指令格式為 Windows 命令提示字元（`^` 為換行接續）。工具參數以實際版本的 `--help` 為準。

---

## 0. 準備

| 項目 | 取得方式 |
|---|---|
| Hypervisor Platform | 「開啟或關閉 Windows 功能」→ 勾選 **Windows Hypervisor Platform** → 重新開機 |
| QEMU + usbredirect | 安裝 MSYS2，在「MSYS2 UCRT64」視窗執行 `pacman -S mingw-w64-ucrt-x86_64-qemu`（會一併安裝 usbredir，內含 `usbredirect.exe`）。執行檔位於 `C:\msys64\ucrt64\bin`，以下指令請在該資料夾執行或把它加入 PATH |
| UsbDk | 從 daynix/UsbDk 的 GitHub Releases 下載 x64 安裝檔並安裝 |
| XP 安裝光碟 | 零售版光碟製成的 ISO 檔（以下稱 `xp.iso`）與序號 |
| 光學軟體與 iHR320 驅動安裝檔 | 放在一個資料夾，例如 `C:\xp-install` |

建立工作資料夾，例如 `C:\xptest`，以下指令都在這裡執行。

---

## 1. 安裝 XP（舊電腦無法開機，所以全新安裝）

```bat
qemu-img create -f qcow2 base.qcow2 10G

qemu-system-i386.exe ^
  -accel whpx -accel tcg ^
  -machine pc -m 512 ^
  -drive file=base.qcow2,if=ide ^
  -cdrom xp.iso -boot d ^
  -vga cirrus -nic none
```

- 依畫面完成 XP 安裝（會重新開機數次；第一次重開後若又從光碟開機，在提示時不要按任何鍵即可從硬碟繼續）。
- QEMU 啟動訊息會顯示用的是 WHPX 還是退回 TCG，**請記錄**。
- 安裝完成後在 XP 裡正常關機。

> 選用：若舊電腦的硬碟其實還能讀（只是主機板或電源壞了），可接到 Win11 上把整顆硬碟轉成映像，保留原本安裝好的軟體。需要時再另外提供步驟。

---

## 2. 建立差異映像（保護剛裝好的 XP）

```bat
qemu-img create -f qcow2 -b base.qcow2 -F qcow2 system.qcow2
```

之後所有測試只寫入 `system.qcow2`；測壞了刪掉重建即可回到剛裝好的 XP。

---

## 3. 查 iHR320 的 VID:PID

1. 把 iHR320 接到 Win11、開啟電源。
2. 開啟「裝置管理員」。iHR320 內建 USB hub，所以通常會看到一個「USB Hub」和一個分光儀裝置（可能顯示為未知裝置）。
3. 在**分光儀裝置**（不是 hub）上按右鍵 → 內容 → 詳細資料 → 硬體識別碼，記下 `USB\VID_xxxx&PID_yyyy`。

---

## 4. 啟動 XP 並接上 iHR320

```bat
qemu-system-i386.exe ^
  -accel whpx -accel tcg ^
  -machine pc -m 512 ^
  -drive file=system.qcow2,if=ide,cache=writeback ^
  -drive file=fat:C:/xp-install,format=raw,if=ide,index=1 ^
  -vga cirrus ^
  -device ich9-usb-ehci1,id=ehci,addr=1d.7,multifunction=on ^
  -device ich9-usb-uhci1,addr=1d.0,multifunction=on,masterbus=ehci.0,firstport=0 ^
  -device ich9-usb-uhci2,addr=1d.1,multifunction=on,masterbus=ehci.0,firstport=2 ^
  -device ich9-usb-uhci3,addr=1d.2,multifunction=on,masterbus=ehci.0,firstport=4 ^
  -device usb-tablet,bus=ehci.0 ^
  -nic none ^
  -qmp tcp:127.0.0.1:4444,server=on,wait=off
```

- `fat:C:/xp-install`：把安裝檔資料夾以唯讀磁碟的形式放進 XP（顯示為另一顆磁碟）。資料夾內容建議小於 500 MB。
- EHCI + 3 組 UHCI：XP 相容的 USB 拓撲（不用 xHCI）。
- `usb-tablet`：滑鼠不會被鎖在視窗裡。
- `-nic none`：XP 不連網。

XP 開好後，用第 6 節的 QMP 工具送出以下指令，把 USB 轉送插槽接進 XP：

```json
{"execute":"chardev-add","arguments":{"id":"ur1","backend":{"type":"socket","data":{"addr":{"type":"inet","data":{"host":"127.0.0.1","port":"4000"}},"server":true,"wait":false}}}}
{"execute":"device_add","arguments":{"driver":"usb-redir","chardev":"ur1","id":"ur1dev","bus":"ehci.0"}}
```

再開另一個命令提示字元，把 iHR320 轉進去（換成第 3 步查到的 ID）：

```bat
usbredirect --device xxxx:yyyy --to 127.0.0.1:4000
```

XP 應出現「發現新硬體」。從安裝檔磁碟安裝光學軟體與 iHR320 驅動，然後實際操作儀器。

**請觀察**：XP 裡是否出現兩次「發現新硬體」，或 Win11 端看到 iHR320 的 VID:PID 改變（韌體重新列舉）。若有，記下新的 ID，結束 `usbredirect` 後用新 ID 再執行一次。

---

## 5. 速度測試

在光學軟體中執行一次**平常會用的掃描**（例如固定的波長範圍與步距），記錄：
- 掃描總時間
- 若記得舊 XP 電腦做同樣掃描的時間，一併記下作為基準

（選用）用 Zadig 把 iHR320 的驅動換成 WinUSB，改用 QEMU 內建直通重測同一個掃描，可以看出現有方案 10 ms 輪詢造成的差距：
```bat
-device usb-host,vendorid=0xXXXX,productid=0xYYYY,bus=ehci.0
```
測完務必在裝置管理員把驅動還原。

---

## 6. 休眠 / 恢復測試

### 6.1 QMP 工具（PowerShell）

開一個 PowerShell 視窗貼上以下內容，之後用 `Q '<JSON>'` 送指令：

```powershell
$c = New-Object Net.Sockets.TcpClient('127.0.0.1', 4444)
$r = New-Object IO.StreamReader($c.GetStream())
$w = New-Object IO.StreamWriter($c.GetStream()); $w.AutoFlush = $true
$r.ReadLine()
function Q($j) { $w.WriteLine($j); do { $l = $r.ReadLine(); $l } while ($l -notmatch '"return"|"error"') }
Q '{"execute":"qmp_capabilities"}'
```

### 6.2 存檔

1. 先在 XP 裡讓光學軟體停在某個畫面。
2. 移除 USB（XP 會看到 iHR320 被拔除）：
   ```powershell
   Q '{"execute":"device_del","arguments":{"id":"ur1dev"}}'
   ```
   注意：`device_del` 會一併刪除 `ur1` 這個 chardev，不需要另外移除。
3. 存檔並結束：
   ```powershell
   Q '{"execute":"stop"}'
   Q '{"execute":"migrate","arguments":{"uri":"file:C:/xptest/state.bin"}}'
   Q '{"execute":"query-migrate"}'
   ```
   重複 `query-migrate` 直到 `"status": "completed"`，再送 `Q '{"execute":"quit"}'`。

### 6.3 恢復

1. 用第 4 節**完全相同**的參數，最後再加上 `-incoming defer` 啟動 QEMU。
2. 重新連上 QMP（再貼一次 6.1），然後：
   ```powershell
   Q '{"execute":"migrate-incoming","arguments":{"uri":"file:C:/xptest/state.bin"}}'
   Q '{"execute":"query-status"}'
   ```
   等狀態為 `"paused"` 後送 `Q '{"execute":"cont"}'`。
3. 用第 4 節的 `chardev-add`、`device_add` 兩行重新接入 USB，再執行一次 `usbredirect`。
4. 觀察光學軟體是否能直接繼續控制 iHR320，或需要重新連線/重新開啟軟體。
5. 另外測一次：**休眠期間把 iHR320 關電源再開**，恢復後軟體的反應。

---

## 7. 回報表

| 項目 | 結果 |
|---|---|
| 加速方式（WHPX / TCG） | |
| XP 安裝光碟版本（SP 版本、語言） | |
| iHR320 的 VID:PID | |
| 是否有韌體重新列舉（新的 VID:PID） | |
| XP 是否辨識 iHR320、光學軟體能否控制 | |
| 掃描時間（usbredir） | |
| 掃描時間（舊 XP 電腦，若有） | |
| （選用）掃描時間（usb-host 直通） | |
| 休眠存檔時間 / `state.bin` 大小 / 恢復時間 | |
| 恢復後軟體能否直接繼續使用 iHR320 | |
| 休眠期間 iHR320 斷電後恢復的反應 | |
| UsbDk 是否需要系統管理員權限才能轉送 | |
| 遇到的錯誤訊息或截圖 | |
