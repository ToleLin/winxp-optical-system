#!/usr/bin/env bash
# 把 CI 建出的安裝檔發佈到 GitHub Releases（由 .github/workflows/build.yml 呼叫）：
#   XPOptical-Setup-<版本>.exe            安裝檔
#   XPOptical-<版本>-win-x64.zip           免安裝版
#   XPOptical-<版本>-engine-sources.tar    執行引擎各元件的原始碼（GPL/LGPL 要求）
#   SHA256SUMS.txt                         以上檔案的檢查碼
# 用法：publish-release.sh <標籤> <檔案資料夾>
#   標籤必須是 v<Directory.Build.props 的版本>，例如 v0.2.1；
#   加上後綴（例如 v0.3.0-beta.1）會發佈為預先發行版，不會成為 releases/latest。
#   標籤還不存在時，建立在目前的提交（HEAD）上；已存在時必須指向 HEAD。
# 需要：gh（GH_TOKEN 要有 contents: write 權限）、python3
set -euo pipefail
TAG=$1
DIR=$2
ROOT=$(cd "$(dirname "$0")/.." && pwd)
VERSION=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$ROOT/Directory.Build.props")

# 標籤會寫進說明裡的下載網址，只允許英數字、點與連字號
[[ "$TAG" =~ ^v[0-9A-Za-z.-]+$ ]] || { echo "標籤格式不正確：$TAG（例如 v$VERSION）" >&2; exit 1; }
case "$TAG" in
  "v$VERSION") PRERELEASE=() ;;
  "v$VERSION"-*) PRERELEASE=(--prerelease) ;;
  *) echo "標籤 $TAG 與 Directory.Build.props 的版本 $VERSION 不符（應為 v$VERSION）" >&2; exit 1 ;;
esac

SETUP="XPOptical-Setup-$VERSION.exe"
ZIP="XPOptical-$VERSION-win-x64.zip"
SOURCES="XPOptical-$VERSION-engine-sources.tar"
for f in "$SETUP" "$ZIP"; do
  [ -f "$DIR/$f" ] || { echo "找不到 $DIR/$f" >&2; exit 1; }
done

# 附註標籤取 ^{} 才是它指向的提交
TARGET=$(git -C "$ROOT" rev-parse HEAD)
TAGGED=$(git -C "$ROOT" ls-remote origin "refs/tags/$TAG" "refs/tags/$TAG^{}" | sort -k2 | tail -1 | cut -f1)
if [ -n "$TAGGED" ] && [ "$TAGGED" != "$TARGET" ]; then
  echo "標籤 $TAG 已指向 $TAGGED，不是這次建置的 $TARGET" >&2; exit 1
fi

python3 "$ROOT/tools/fetch-engine-sources.py" "$DIR/XPOptical/engine/engine-manifest.json" "$DIR/$SOURCES"
(cd "$DIR" && sha256sum "$SETUP" "$ZIP" "$SOURCES" > SHA256SUMS.txt)
FILES=("$DIR/$SETUP" "$DIR/$ZIP" "$DIR/$SOURCES" "$DIR/SHA256SUMS.txt")

# 說明：packaging/release-notes.md，後面接上 GitHub 自動整理的變更清單（合併的 PR）
NOTES=$(mktemp)
sed -e "s/{VERSION}/$VERSION/g" -e "s/{TAG}/$TAG/g" "$ROOT/packaging/release-notes.md" > "$NOTES"
printf '\n' >> "$NOTES"
gh api "repos/{owner}/{repo}/releases/generate-notes" -f tag_name="$TAG" -f target_commitish="$TARGET" \
  --jq .body >> "$NOTES"

if gh release view "$TAG" >/dev/null 2>&1; then
  # 重新執行：更新檔案與說明
  gh release upload "$TAG" "${FILES[@]}" --clobber
  gh release edit "$TAG" --title "XP 光學系統 $VERSION" --notes-file "$NOTES"
else
  gh release create "$TAG" "${FILES[@]}" --target "$TARGET" --title "XP 光學系統 $VERSION" \
    --notes-file "$NOTES" "${PRERELEASE[@]}"
fi
echo "已發佈：$TAG"
