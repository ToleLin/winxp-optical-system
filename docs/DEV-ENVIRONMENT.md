# 開發環境規格

## 1. 雲端開發環境（Claude Code 雲端工作階段）

### 1.1 目前規格（實測）

| 項目 | 規格 |
|---|---|
| 作業系統 | Ubuntu 24.04.5 LTS（x86-64） |
| CPU | 4 核心 Intel Xeon 2.1 GHz，**無硬體虛擬化**（沒有 `/dev/kvm`） |
| 記憶體 | 15 GiB |
| 可用磁碟 | 約 30 GB（工作階段配額） |
| USB 裝置 | 無 |
| 網路 | 經代理伺服器對外連線（apt、NuGet、GitHub 原始碼、Microsoft 下載站可用） |
| 預裝工具 | gcc 13、clang 18、cmake、ninja、Python 3.13、Node 22、git、docker（CLI） |

### 1.2 能做 / 不能做

| 能做 | 不能做 |
|---|---|
| 編譯 Windows 主程式（WPF，自帶執行環境的單一 exe） | 執行或測試 Windows 程式畫面 |
| 交叉編譯 XP 代理程式（32 位元，目標 XP） | 執行 XP（沒有授權映像；且無硬體加速） |
| 用 QEMU（軟體模擬）測試 QMP 控制、休眠存檔/恢復、USB 熱插拔流程 | 測試實體 USB（iHR320）、UsbDk、WHPX |
| 單元測試、文件 | — |

**結論**：程式碼可以在雲端開發與編譯；凡是牽涉 Windows 執行、XP 實際運作與 iHR320 的測試，都要在 Win11 實機上進行（Phase 0 與之後的整合測試）。

### 1.3 建議的環境設定腳本

以下內容已在雲端逐項實測（安裝約 1 分鐘）。要讓每個新工作階段自動具備這些工具，請在工作階段標題列的雲端環境選單 → **Edit** → **Setup script** 貼上：

```bash
#!/bin/bash
set -e
export DEBIAN_FRONTEND=noninteractive
apt-get update -qq
# XP 代理程式交叉編譯（win32 執行緒模型，避免相依 winpthreads）+ QEMU 測試工具
apt-get install -y -qq --no-install-recommends \
  gcc-mingw-w64-i686-win32 gcc-mingw-w64-x86-64-win32 binutils-mingw-w64 mingw-w64-tools \
  qemu-system-x86 qemu-utils meson pkg-config
# Microsoft 官方 .NET 8 SDK（Ubuntu 套件版的 dotnet 缺少 WPF 編譯支援，不能用）
V=$(curl -fsSL https://builds.dotnet.microsoft.com/dotnet/release-metadata/8.0/releases.json \
    | python3 -c "import json,sys; print(json.load(sys.stdin)['latest-sdk'])")
mkdir -p /opt/dotnet
curl -fsSL "https://builds.dotnet.microsoft.com/dotnet/Sdk/$V/dotnet-sdk-$V-linux-x64.tar.gz" | tar -xz -C /opt/dotnet
ln -sf /opt/dotnet/dotnet /usr/local/bin/dotnet
```

### 1.4 實測紀錄

| 測試 | 結果 |
|---|---|
| Ubuntu 套件 `dotnet-sdk-8.0` 編譯 WPF | ❌ 缺少 `Microsoft.NET.Sdk.WindowsDesktop` |
| Microsoft 官方 .NET 8 SDK（8.0.427）編譯 WPF，`win-x64` 自帶執行環境單一檔案 | ✅ 產生約 72 MB 的 `PE32+ GUI` exe |
| `i686-w64-mingw32-gcc-win32` 編譯 XP 程式（`-D_WIN32_WINNT=0x0501 -static`，子系統版本 5.1） | ✅ 只匯入 KERNEL32 / USER32 / msvcrt，無 Vista 以後才有的 API |
| QEMU 8.2（TCG）：EHCI + 3 組 UHCI、QMP 熱插拔 `usb-redir` | ✅ |
| QEMU 8.2（TCG）：`migrate` 到 `file:` 存檔 → `-incoming defer` 恢復 → 重新接入 USB | ✅（未裝作業系統，僅驗證機制） |

---

## 2. Windows 測試環境（Phase 0 與整合測試）

| 項目 | 需求 |
|---|---|
| 作業系統 | Windows 10 或 11（64 位元） |
| CPU | 支援並已在 BIOS 開啟硬體虛擬化（Intel VT-x / AMD-V） |
| 記憶體 | 8 GB 以上（XP 只用 512 MB） |
| 磁碟 | 可用空間 10 GB 以上，建議 SSD |
| Windows 功能 | 啟用 Windows Hypervisor Platform |
| 權限 | 安裝 UsbDk 時需要一次系統管理員權限 |
| 儀器 | iHR320 + USB 線 |
| 其他 | XP 零售版安裝光碟（ISO）與序號；光學軟體與 iHR320 驅動安裝檔 |
