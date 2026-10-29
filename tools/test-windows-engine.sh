#!/usr/bin/env bash
# 在 Linux 上用 Wine 執行「實際要隨附的」Windows 版 QEMU，跑整合測試。
# 可抓出只在 Windows 版才有的問題（例如中文路徑、遷移到檔案在 Windows 未實作）。
# 需要：wine64、先執行過 tools/package.sh（或 build-engine.py）產生 dist/XPOptical/engine
set -euo pipefail
ROOT=$(cd "$(dirname "$0")/.." && pwd)
ENGINE="${1:-$ROOT/dist/XPOptical/engine}"
WINE=$(command -v wine64 || echo /usr/lib/wine/wine64)
WRAP=$(mktemp -d)
export WINEPREFIX="${WINEPREFIX:-$HOME/.cache/xpoptical/wineprefix}" WINEDEBUG=-all
trap 'rm -rf "$WRAP"' EXIT

for tool in qemu-system-i386 qemu-img; do
  cat > "$WRAP/$tool" <<WRAPPER
#!/bin/bash
exec "$WINE" "$ENGINE/bin/$tool.exe" "\$@"
WRAPPER
  chmod +x "$WRAP/$tool"
done

"$WRAP/qemu-system-i386" --version | head -1
PATH="$WRAP:$PATH" dotnet test "$ROOT/tests/XPOptical.Core.Tests" --filter "FullyQualifiedName~IntegrationTests"
