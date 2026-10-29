# Phase 0：可行性驗證步驟

在寫任何程式碼之前，先用**現成工具**在實際的 Win11 電腦 + 實際儀器上驗證整條路線可行。
這一步能在最早期發現「做不到」的情況（例如儀器其實依賴 PCI 卡），並取得之後最佳化要對照的基準數據。

> 指令格式為 Windows 命令提示字元（`^` 為換行接續）。各工具的參數請以實際版本的 `--help` 為準。

---

## 0. 準備

| 項目 | 說明 |
|---|---|
| Win11 電腦 | 在「開啟或關閉 Windows 功能」啟用 **Windows Hypervisor Platform**，重新開機 |
| QEMU for Windows | QEMU 官方網站 Windows 下載頁所連結的 64 位元安裝版 |
| UsbDk | Red Hat 釋出的 UsbDk 安裝檔（daynix/UsbDk） |
| usbredirect | usbredir 專案的工具（spice/usbredir）；若找不到 Windows 編譯版，Phase 0 可改用 virt-viewer for Windows 內建的 USB 轉送功能驗證同一條路徑 |
| XP 映像 | 見步驟 1 |

---

## 1. 取得 XP 映像（二選一）

### 方案 A（建議）：從現有 XP 儀器電腦直接轉換（P2V）

好處：光學軟體、驅動程式、校正檔、授權都已經裝好設定好，不需要找原始安裝光碟。

1. **在舊 XP 電腦上先處理磁碟控制器驅動程式**：XP 換到不同的磁碟控制器時常會藍底當機（STOP 0x0000007B）。轉換前先啟用 XP 內建的通用 IDE 驅動程式（一般稱為 MergeIDE 修正）。
2. 在舊電腦上執行 Sysinternals **Disk2vhd**，產生 `.vhd`（不要勾選 VHDX）。
3. 在 Win11 上轉檔：
   ```bat
   qemu-img convert -p -f vpc -O qcow2 xp.vhd base.qcow2
   ```

### 方案 B：全新安裝

用原始 XP 光碟（`.iso`）安裝到新磁碟映像，再安裝光學軟體與儀器驅動程式：
```bat
qemu-img create -f qcow2 base.qcow2 8G
```

---

## 2. 建立差異映像（保護 base）

```bat
qemu-img create -f qcow2 -b base.qcow2 -F qcow2 system.qcow2
```

之後所有測試都只寫入 `system.qcow2`；測壞了刪掉重建即可。

---

## 3. 啟動 XP（尚未接儀器）

```bat
qemu-system-i386.exe ^
  -accel whpx -accel tcg ^
  -machine pc -m 512 ^
  -drive file=system.qcow2,if=ide,cache=writeback ^
  -vga cirrus ^
  -device ich9-usb-ehci1,id=ehci,addr=1d.7,multifunction=on ^
  -device ich9-usb-uhci1,addr=1d.0,multifunction=on,masterbus=ehci.0,firstport=0 ^
  -device ich9-usb-uhci2,addr=1d.1,multifunction=on,masterbus=ehci.0,firstport=2 ^
  -device ich9-usb-uhci3,addr=1d.2,multifunction=on,masterbus=ehci.0,firstport=4 ^
  -device usb-tablet,bus=ehci.0 ^
  -nic none ^
  -qmp tcp:127.0.0.1:4444,server=on,wait=off
```

說明：
- `-accel whpx -accel tcg`：優先用硬體加速，失敗時退回軟體模擬。**記錄實際用的是哪一個**（QEMU 啟動訊息會顯示）。
- `-vga cirrus`：XP 內建驅動程式，免安裝即可 1024×768。
- EHCI + 3 組 UHCI：XP 相容的 USB 2.0/1.1 拓撲（不用 xHCI）。
- `usb-tablet`：滑鼠游標不會被鎖在視窗裡。
- `-nic none`：XP 不連網。

**記錄**：冷開機到軟體可操作的時間。

---

## 4. 接上 USB 儀器（本專案的 USB 路線）

1. 安裝 UsbDk。
2. 在 Win11 端查儀器的 VID:PID（裝置管理員 → 內容 → 詳細資料 → 硬體識別碼，例如 `USB\VID_1234&PID_5678`）。
3. 在第 3 步的 QEMU 參數加上：
   ```bat
   -chardev socket,id=ur1,host=127.0.0.1,port=4000,server=on,wait=off ^
   -device usb-redir,chardev=ur1,id=ur1dev,bus=ehci.0
   ```
4. QEMU 開好後，另開一個命令提示字元：
   ```bat
   usbredirect --device 1234:5678 --to 127.0.0.1:4000
   ```
5. XP 應出現「發現新硬體」，安裝/載入原本的 XP 驅動程式，然後用光學軟體實際操作儀器。

**若儀器會下載韌體後重新列舉**（XP 裡出現兩次「發現新硬體」、Win11 端看到 VID:PID 改變），記錄前後兩組 ID —— 這正是設定檔 `followReenumeration` 要處理的情況，Phase 0 先手動再執行一次 `usbredirect` 接上新 ID。

### 4.1 （選用）重現現有方案的 10 ms 輪詢問題

用 Zadig 把儀器驅動程式換成 WinUSB，改用 QEMU 內建直通：
```bat
-device usb-host,vendorid=0x1234,productid=0x5678,bus=ehci.0
```
用同一個軟體操作（例如連續擷取 100 筆資料）計時，和 usbredir 路線比較。測完記得在裝置管理員把驅動程式還原。

---

## 5. 接上 COM 儀器

1. 原版 QEMU **不會**把 XP 內的鮑率設定傳到實體 COM，啟動時會跳出 COM 設定對話框 —— 請直接在對話框中設定成儀器使用的鮑率/同位元/停止位元（這就是第 5 節要修補的問題）。
2. 在 QEMU 參數加上（以 Win11 端 COM9 為例，COM10 以上寫成 `\\.\COM10`）：
   ```bat
   -chardev serial,id=com1,path=COM9 ^
   -device isa-serial,chardev=com1,index=0
   ```
3. XP 裡的 COM1 即對應 Win11 的 COM9，用光學軟體測試通訊。

**記錄**：軟體是否會在執行中變更鮑率（若會，原版 QEMU 必定失敗，可直接證明修補的必要性）；是否用到 DTR/RTS 硬體握手。

---

## 6. 休眠 / 恢復測試

以 QMP 連到 `127.0.0.1:4444`（任何可收發文字的 TCP 工具皆可，連上後先送 `{"execute":"qmp_capabilities"}`）：

```json
{"execute":"stop"}
{"execute":"migrate","arguments":{"uri":"file:C:/xptest/state.bin"}}
{"execute":"query-migrate"}
{"execute":"quit"}
```
重複 `query-migrate` 直到狀態為 `completed` 再送 `quit`。

恢復：用**完全相同**的參數再加上 `-incoming defer` 啟動，然後：
```json
{"execute":"migrate-incoming","arguments":{"uri":"file:C:/xptest/state.bin"}}
{"execute":"cont"}
```

**存檔時 VM 的裝置組成必須和恢復時的啟動參數一致**，所以這個測試要把 USB 儀器改成用 QMP 熱插拔：從啟動參數中拿掉第 4 步加的 `-chardev socket…` 與 `-device usb-redir…` 兩行，改在 XP 開好後送出：

```json
{"execute":"chardev-add","arguments":{"id":"ur1","backend":{"type":"socket","data":{"addr":{"type":"inet","data":{"host":"127.0.0.1","port":"4000"}},"server":true,"wait":false}}}}
{"execute":"device_add","arguments":{"driver":"usb-redir","chardev":"ur1","id":"ur1dev","bus":"ehci.0"}}
```
再執行 `usbredirect`。存檔前先移除，恢復後再用同樣的指令接回，觀察軟體是否能重新連上儀器：
```json
{"execute":"device_del","arguments":{"id":"ur1dev"}}
{"execute":"chardev-remove","arguments":{"id":"ur1"}}
```

> `file:` 遷移需要較新版本的 QEMU；若不支援，改用 HMP 指令 `savevm` / `loadvm` 驗證概念即可。

**記錄**：存檔時間、`state.bin` 大小、恢復時間、軟體恢復後能否直接繼續使用儀器。

---

## 7. 回報表

請把結果填入下表（也回答 [`DESIGN.md`](DESIGN.md) 第 13 節的問題），據此決定 Phase 1 的範圍：

| 項目 | 結果 |
|---|---|
| 加速方式（WHPX / TCG） | |
| 冷開機到可操作時間 | |
| 每台 USB 儀器：XP 是否辨識、軟體是否能控制 | |
| 是否有韌體重新列舉（前後 VID:PID） | |
| （選用）usb-host vs. usbredir 同一操作耗時 | |
| 原 XP 電腦同一操作耗時（基準） | |
| 每台 COM 儀器：是否正常、是否執行中改鮑率、是否用硬體握手 | |
| 休眠存檔時間 / 檔案大小 / 恢復時間 | |
| 恢復後儀器是否需重新連線 | |
| 遇到的錯誤訊息或截圖 | |
