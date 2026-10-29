# 開發環境規格

## 1. 雲端開發環境（Claude Code 雲端工作階段）

### 1.1 目前規格（實測）

| 項目 | 規格 |
|---|---|
| 作業系統 | Ubuntu 24.04.5 LTS（x86-64） |
| CPU | 4 核心 Intel Xeon 2.1 GHz，**無硬體虛擬化**（沒有 `/dev/kvm`，QEMU 以軟體模擬執行） |
| 記憶體 | 15 GiB |
| 可用磁碟 | 約 30 GB（工作階段配額） |
| USB 裝置 | 無 |
| 網路 | 經代理伺服器對外連線（apt、NuGet、GitHub、MSYS2、Microsoft 下載站可用） |
| 預裝工具 | gcc 13、clang 18、cmake、ninja、Python 3.13、Node 22、git |

### 1.2 能做 / 不能做

| 能做 | 不能做 |
|---|---|
| 編譯 Windows 主程式（WPF，自帶執行環境的單一 exe） | 在真正的 Windows 上執行 |
| 組裝 Windows 版執行引擎（MSYS2 套件） | 執行 XP（沒有授權映像；且無硬體加速） |
| 單元測試、以真實 QEMU 執行的整合測試（Linux 版與透過 Wine 的 Windows 版） | 測試實體 USB（iHR320）、UsbDk、WHPX |
| 用 Wine + 虛擬螢幕執行主程式並截圖檢查畫面 | — |

**結論**：程式可以在雲端開發、建置、測試與打包；牽涉實體硬體（iHR320、UsbDk、WHPX）與真正 Windows 的部分，在 Win11 實機上驗證（Phase 0）。

### 1.3 建議的環境設定腳本

以下內容已在雲端逐項實測。要讓每個新工作階段自動具備這些工具，請在工作階段標題列的雲端環境選單 → **Edit** → **Setup script** 貼上：

```bash
#!/bin/bash
set -e
export DEBIAN_FRONTEND=noninteractive
apt-get update -qq
# QEMU（整合測試）、zstd（解開 MSYS2 套件）
apt-get install -y -qq --no-install-recommends qemu-system-x86 qemu-utils zstd
# Microsoft 官方 .NET 10 SDK（Ubuntu 套件版的 dotnet 缺少 WPF 編譯支援，不能用）
V=$(curl -fsSL https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json \
    | python3 -c "import json,sys; print(json.load(sys.stdin)['latest-sdk'])")
mkdir -p /opt/dotnet
curl -fsSL "https://builds.dotnet.microsoft.com/dotnet/Sdk/$V/dotnet-sdk-$V-linux-x64.tar.gz" | tar -xz -C /opt/dotnet
ln -sf /opt/dotnet/dotnet /usr/local/bin/dotnet
# （選用）用 Wine 測試隨附的 Windows 版引擎與主程式畫面
# apt-get install -y -qq --no-install-recommends wine64 xvfb x11-apps imagemagick xdotool fonts-noto-cjk
```

### 1.4 實測紀錄

| 測試 | 結果 |
|---|---|
| Ubuntu 套件 `dotnet-sdk-8.0` 編譯 WPF | ❌ 缺少 `Microsoft.NET.Sdk.WindowsDesktop` |
| Microsoft 官方 .NET 10 SDK（10.0.403）編譯 WPF，`win-x64` 自帶執行環境單一檔案 | ✅ 約 62 MB 的 `XPOptical.exe` |
| `tools/build-engine.py` 組裝 Windows 引擎 | ✅ 109 個檔案、87 個 MSYS2 套件、135 MB；所有 DLL 皆已打包或為 Windows 內建 |
| `dotnet test`（Linux 版 QEMU 8.2） | ✅ 43 項 |
| `tools/test-windows-engine.sh`（Windows 版 QEMU 11.1.2，透過 Wine） | ✅ 8 項整合測試 |
| 主程式在 Wine + Xvfb 執行 | ✅ 中文介面、XP 畫面內嵌、關閉休眠、再開恢復 |

> 在 Wine 中執行主程式需要把中文字型登錄到 Wine 的 `C:\windows\Fonts`；真正的 Windows 有微軟正黑體，不需要這一步。

---

## 2. Windows 測試環境（Phase 0 與整合測試）

| 項目 | 需求 |
|---|---|
| 作業系統 | Windows 10 或 11（64 位元） |
| CPU | 不需要硬體虛擬化（XP 以 TCG 軟體模擬執行） |
| 記憶體 | 8 GB 以上（XP 只用 512 MB） |
| 磁碟 | 可用空間 10 GB 以上，建議 SSD |
| Windows 功能 | 不需要（不用開 Hyper-V / Hypervisor Platform） |
| 權限 | 安裝 UsbDk 時需要一次系統管理員權限 |
| 儀器 | iHR320 + USB 線 |
| 其他 | XP 零售版安裝光碟（ISO）與序號；光學軟體與 iHR320 驅動安裝檔 |
