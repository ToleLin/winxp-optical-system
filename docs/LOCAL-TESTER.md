# 給本機測試端（Win11 + iHR320 上的 Claude）的說明

這個專案由兩端合作完成：

| 角色 | 位置 | 負責 |
|---|---|---|
| 開發端 | 雲端 Claude 工作階段 `winxp-optical-system-55` | 修改程式、跑自動化測試、推送新版 |
| 測試端（你） | 使用者的 Win11 電腦，接著 iHR320 | 下載新版、實機測試、收集結果回報給開發端 |

開發端在雲端，沒有 Windows 也沒有 iHR320；凡是需要真實硬體的驗證都靠測試端。
開發端**無法主動傳訊息給你**，修正結果會以新的提交（分支 `claude/adoring-gauss-k2di4m`）與 GitHub Actions 新版呈現，並在使用者的對話中說明。

## 1. 取得最新版

```powershell
# 需要 GitHub CLI（gh）並已登入可讀取 ToleLin/winxp-optical-system 的帳號
$dest = "C:\XPOptical-new"
gh run download -R ToleLin/winxp-optical-system -n XPOptical-win-x64 -D $dest
```

`gh run download` 不指定 run 時會讓你選擇；選分支 `claude/adoring-gauss-k2di4m` 最新且成功的那一次。

- **第一次**：把 `$dest` 的內容複製到 `C:\XPOptical`。
- **更新**：先在程式中按「■ 關機」讓 XP 正常關機並關閉程式，再用新版覆蓋 `XPOptical.exe`、`engine`、`drivers`、`profiles` 與說明檔。**保留** `vm` 資料夾與 `settings.json`（XP 不需重裝）。

## 2. 要測什麼

依 [`PHASE0-POC.md`](PHASE0-POC.md) 第 3 節的 14 項。操作畫面需要使用者本人（安裝 XP、操作光學軟體與 iHR320），你可以協助：

- 開始前檢查環境：

  ```powershell
  # BIOS 虛擬化、Hypervisor Platform、記憶體完整性、UsbDk
  (Get-CimInstance Win32_Processor).VirtualizationFirmwareEnabled
  (Get-WindowsOptionalFeature -Online -FeatureName HypervisorPlatform).State
  Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity' -ErrorAction SilentlyContinue | Select-Object Enabled
  Get-Service UsbDk -ErrorAction SilentlyContinue
  # 目前的 USB 裝置（找出 iHR320 的 VID:PID）
  Get-PnpDevice -PresentOnly | Where-Object InstanceId -like 'USB\VID_*' | Select-Object FriendlyName, InstanceId, Status
  ```

- 測試後整理紀錄：`C:\XPOptical\logs\XPOptical-*.log` 中的 `[ERROR]`、`[WARN]` 行，以及「休眠存檔完成」「已從休眠檔恢復」「接入 USB 裝置」「USB 轉送結束」等行。

## 3. 怎麼回報給開發端

優先順序：

1. **傳訊息給工作階段 `winxp-optical-system-55`**（可用時）。訊息只能是文字，請包含：
   - 測試的版本（`XPOptical.exe` 內容或 Actions run 編號）
   - 回報表 14 項的結果
   - 程式「設定」→「產生問題回報…」產生的 zip 中 `summary.txt` 全文
   - 相關紀錄檔片段（錯誤前後各約 20 行）
   - 使用者描述的現象（例如哪個畫面、做了什麼操作）
2. **請使用者把以上內容與 zip、截圖貼到他和開發端的對話中**。

請不要把問題回報 zip 或紀錄檔提交到儲存庫（內含電腦名稱、使用者路徑等資訊）。

## 4. 定版

所有項目通過後，由使用者確認定版；開發端整理合併到 `main` 並請使用者把儲存庫改為公開。
