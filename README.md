# WinXP Optical System

在 Windows 10/11 上執行只能在 Windows XP 運作的光學軟體，首要支援 **HORIBA iHR320 分光儀（USB）**。

- **繁體中文、簡易上手**：首次使用精靈帶你建立 XP 環境；之後雙擊開啟、按 ✕ 自動存檔
- **可獨立運行**：免安裝資料夾、自帶執行環境的單一 exe，不需另裝 .NET
- **USB 最佳化**：事件驅動轉送（解決現有方案在 Windows 上的 10 ms 輪詢延遲）、iHR320 插拔自動接入、免手動換驅動
- **像關機一樣保存**：關閉時快速休眠，下次數秒內回到原畫面；可一鍵還原到出廠狀態
- **安全隔離**：XP 不連網，量測資料自動同步到 Win11 端

> 目前階段：**設計 v0.2**，等待 Phase 0（Win11 + iHR320 實機驗證）。

## 文件

| 文件 | 內容 |
|---|---|
| [docs/DESIGN.md](docs/DESIGN.md) | 系統設計：需求、架構、使用者流程、USB 設計、狀態保存、XP 建立精靈、已驗證事項、風險 |
| [docs/PHASE0-POC.md](docs/PHASE0-POC.md) | 可行性驗證步驟（Win11 + iHR320） |
| [docs/DEV-ENVIRONMENT.md](docs/DEV-ENVIRONMENT.md) | 雲端開發環境規格、設定腳本、Windows 測試環境需求 |
| [profiles/ihr320.json](profiles/ihr320.json) | iHR320 裝置設定檔 |

## 已知限制

- 不支援 PCI/PCIe 介面卡直通（Windows 10/11 的限制）
- 不附帶 Windows XP 與光學軟體，使用者須自備合法授權並自行安裝
