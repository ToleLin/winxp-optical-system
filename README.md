# WinXP Optical System

在 Windows 10/11 上執行只能在 Windows XP 運作的舊版光學軟體與儀器。

- **一個程式**：雙擊開啟，直接看到光學軟體，不需要建立或管理虛擬機
- **USB 最佳化**：事件驅動轉送（解決現有方案在 Windows 上的 10 ms 輪詢延遲）、熱插拔自動掛載、免手動換驅動程式、追蹤儀器韌體重新列舉
- **COM 埠最佳化**：XP 內的鮑率/控制線設定同步到實體 COM，固定 COM 號碼對應（Win11 的 COM9 → XP 的 COM1）
- **像關機一樣保存**：關閉時快速休眠，下次數秒內回到原畫面；磁碟只存差異，可一鍵還原
- **安全隔離**：XP 預設不連網，量測資料自動同步到 Win11 端

> 目前階段：**設計草案**。尚未開始實作。

## 文件

| 文件 | 內容 |
|---|---|
| [docs/DESIGN.md](docs/DESIGN.md) | 系統設計：架構、USB/COM 最佳化、狀態保存、安全性、開發階段、待確認問題 |
| [docs/PHASE0-POC.md](docs/PHASE0-POC.md) | 可行性驗證步驟：用現成工具在實際電腦與儀器上驗證 |
| [profiles/example-profile.json](profiles/example-profile.json) | 裝置設定檔範例 |

## 已知限制

- 不支援 PCI/PCIe 介面卡直通（Windows 10/11 用戶端版本的限制）
- 不支援實體並列埠（LPT）
- 不附帶 Windows XP，使用者須自備合法授權
