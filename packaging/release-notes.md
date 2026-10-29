在 Windows 10/11 上執行只能在 Windows XP 運作的光學軟體，並把 HORIBA iHR320 分光儀（USB）接進 XP。

## 極簡安裝（3 步）

1. 下載下方的 **XPOptical-Setup-{VERSION}.exe**。把自己的 Windows XP 安裝光碟 ISO 檔改名為 `xp.iso`，和安裝檔放在同一個資料夾。
2. 執行安裝檔，一路按「下一步」。（若出現「Windows 已保護您的電腦」：按「其他資訊」→「仍要執行」）
3. 程式自動開啟後輸入 XP 產品序號，按「開始自動安裝」。約 30–90 分鐘後自動進入 XP 桌面。

之後：把光學軟體的安裝檔資料夾拖進視窗，在 XP 裡安裝；按「設定」選擇 iHR320。關閉時直接按 ✕，下次開啟回到原本的畫面。

**需要**：64 位元 Windows 10/11、10 GB 以上可用空間、自備的 Windows XP 安裝光碟（ISO）與產品序號。本程式不附帶 Windows XP 與光學軟體。

詳細說明見安裝後的「使用說明」（開始功能表 → XP 光學系統 → 使用說明）。

## 下載檔案

| 檔案 | 說明 |
|---|---|
| **XPOptical-Setup-{VERSION}.exe** | 安裝檔（建議）：程式、執行引擎、USB 驅動一次裝好 |
| XPOptical-{VERSION}-win-x64.zip | 免安裝版：解壓縮到 `C:\XPOptical` 後執行 XPOptical.exe |
| XPOptical-{VERSION}-engine-sources.tar | 隨附執行引擎（QEMU 等）各元件的原始碼，依 GPL/LGPL 提供；一般使用者不需要下載 |
| SHA256SUMS.txt | 以上檔案的 SHA-256 檢查碼 |

## 目前狀態

已在 Win11 實機完成安裝檔、介面與流程測試；以真正的 XP SP3 全自動安裝與 iHR320 實機操作尚待驗證。遇到問題請到 [Issues](https://github.com/ToleLin/winxp-optical-system/issues) 回報；程式的「設定」→「產生問題回報…」會在桌面產生含紀錄檔與電腦環境資訊的 zip 檔，Issues 是公開的，附上前請先確認內容。
