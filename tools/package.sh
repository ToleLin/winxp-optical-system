#!/usr/bin/env bash
# 產生：
#   dist/XPOptical/                         免安裝版資料夾（整個資料夾即可在 Win10/11 使用）
#   dist/XPOptical-<版本>-win-x64.zip        免安裝版壓縮檔
#   dist/XPOptical-Setup-<版本>.exe          單一安裝檔（程式 + 執行引擎 + USB 驅動）
# 需要：.NET 10 SDK（Microsoft 官方版，含 WPF 編譯支援）、python3、zstd；安裝檔需要 makensis（NSIS 3）
set -euo pipefail
ROOT=$(cd "$(dirname "$0")/.." && pwd)
VERSION=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$ROOT/Directory.Build.props")
DIST="$ROOT/dist"
OUT="$DIST/XPOptical"
CACHE="${XPOPTICAL_CACHE:-$HOME/.cache/xpoptical}"
USBDK_URL=https://github.com/daynix/UsbDk/releases/download/v1.00-22/UsbDk_1.0.22_x64.msi
USBDK_SHA256=91f6f695e1e13c656024e6d3b55620bf08d8835ef05ee0496935ba6bb62466a5
USBDK_LICENSE_URL=https://raw.githubusercontent.com/daynix/UsbDk/master/LICENSE

rm -rf "$DIST"
mkdir -p "$OUT/drivers" "$OUT/profiles" "$CACHE"

echo "== 1/5 編譯主程式（自帶執行環境的單一 exe）=="
dotnet publish "$ROOT/src/XPOptical.App" -c Release -o "$DIST/publish" -p:DebugType=None -p:Version="$VERSION"
cp "$DIST/publish/XPOptical.exe" "$OUT/"

echo "== 2/5 組裝執行引擎 =="
python3 "$ROOT/tools/build-engine.py" --out "$OUT/engine" --cache "$CACHE"

echo "== 3/5 USB 驅動 =="
MSI="$CACHE/$(basename "$USBDK_URL")"
if ! echo "$USBDK_SHA256  $MSI" | sha256sum -c --quiet 2>/dev/null; then
  curl -fsSL -o "$MSI" "$USBDK_URL"
  echo "$USBDK_SHA256  $MSI" | sha256sum -c --quiet
fi
cp "$MSI" "$OUT/drivers/"
curl -fsSL -o "$OUT/drivers/UsbDk-LICENSE.txt" "$USBDK_LICENSE_URL"

echo "== 4/5 設定檔、說明與壓縮 =="
cp "$ROOT/profiles/ihr320.json" "$OUT/profiles/"
cp "$ROOT/packaging/使用說明.txt" "$ROOT/packaging/THIRD-PARTY-NOTICES.txt" "$OUT/"
cp "$ROOT/LICENSE" "$OUT/LICENSE.txt"
ZIP="$DIST/XPOptical-$VERSION-win-x64.zip"
(cd "$DIST" && python3 -m zipfile -c "$(basename "$ZIP")" XPOptical)
rm -rf "$DIST/publish"

echo "== 5/5 安裝檔 =="
SETUP="$DIST/XPOptical-Setup-$VERSION.exe"
if command -v makensis >/dev/null; then
  # makensis 在非 UTF-8 語系下處理中文會當掉
  LC_ALL=C.UTF-8 makensis -V2 -INPUTCHARSET UTF8 -DVERSION="$VERSION" -DSRC="$OUT" -DICON="$ROOT/src/XPOptical.App/app.ico" \
    -DUSBDK_MSI="$(basename "$USBDK_URL")" -DOUTFILE="$SETUP" "$ROOT/packaging/installer.nsi"
else
  echo "找不到 makensis（NSIS），略過安裝檔"
  SETUP=
fi

echo "完成：$OUT"
du -sh "$OUT" "$ZIP" $SETUP
