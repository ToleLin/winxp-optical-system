#!/usr/bin/env python3
"""
下載執行引擎（engine 資料夾）各元件的原始碼，打包成一個 tar，隨 GitHub Release 一起發佈：

  engine-manifest.json      元件清單（套件、版本、對應的原始碼檔名）
  sources/<套件>-<版本>.src.tar.zst   MSYS2 的原始碼封存檔（含原始碼與建置腳本）

QEMU、usbredir、libusb 等以 GPL/LGPL 授權，公開散布執行檔時，取得者必須能從同一處取得對應的原始碼。

用法：python3 tools/fetch-engine-sources.py <engine-manifest.json> <輸出.tar>
"""
import http.client
import json
import shutil
import sys
import tarfile
import tempfile
import time
import urllib.request
from pathlib import Path


def download(url: str, dest: Path, tries: int = 4):
    for attempt in range(1, tries + 1):
        try:
            with urllib.request.urlopen(url, timeout=120) as r, open(dest, "wb") as f:
                shutil.copyfileobj(r, f, 1 << 20)
            return
        except (OSError, http.client.HTTPException) as e:
            if attempt == tries:
                raise SystemExit(f"無法下載 {url}：{e}")
            print(f"  重試（{attempt}）{url}：{e}", file=sys.stderr)
            time.sleep(2 ** attempt)


def main():
    if len(sys.argv) != 3:
        raise SystemExit(__doc__)
    manifest_path, out = Path(sys.argv[1]), Path(sys.argv[2])
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    archives = sorted({p["sourceArchive"] for p in manifest["packages"].values()})

    total = 0
    with tempfile.TemporaryDirectory() as tmp, tarfile.open(out, "w") as tar:
        tar.add(manifest_path, "engine-manifest.json")
        for i, name in enumerate(archives, 1):
            f = Path(tmp) / name
            download(f"{manifest['sourceRepo']}/{name}", f)
            size = f.stat().st_size
            total += size
            print(f"[{i}/{len(archives)}] {name}（{size / 2**20:.1f} MB）")
            tar.add(f, f"sources/{name}")
            f.unlink()
    print(f"完成：{out}，{len(archives)} 個原始碼封存檔，共 {total / 2**20:.0f} MB")


if __name__ == "__main__":
    main()
