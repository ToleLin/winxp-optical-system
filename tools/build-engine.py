#!/usr/bin/env python3
"""
組裝 Windows 用的執行引擎（engine 資料夾）：

  engine/bin/          qemu-system-x86_64.exe、qemu-img.exe、usbredirect.exe 與它們需要的 DLL
  engine/share/qemu/   x86 開機韌體與鍵盤配置檔
  engine/licenses/     各套件的授權文件
  engine/THIRD-PARTY-NOTICES.txt

來源是 MSYS2 UCRT64 的官方套件（含 usb-redir 與 libusb）。只複製實際用到的 DLL：
從三個執行檔開始，解析 PE 匯入表，逐一找出提供該 DLL 的套件，直到全部解析完畢。
找不到套件、也不是 Windows 內建的 DLL 會讓建置失敗，避免使用者電腦上缺檔。

用法：python3 tools/build-engine.py --out dist/XPOptical/engine [--cache ~/.cache/xpoptical]
需要：zstd 指令，或 Python 3.14 以上（標準函式庫內建 zstd）。
"""
import argparse
import fnmatch
import hashlib
import io
import json
import shutil
import struct
import subprocess
import sys
import tarfile
import urllib.request
from pathlib import Path

REPO = "https://repo.msys2.org/mingw/ucrt64"
SOURCES = "https://repo.msys2.org/mingw/sources"
PREFIX = "ucrt64/"
ENTRY_POINTS = ["bin/qemu-system-x86_64.exe", "bin/qemu-img.exe", "bin/usbredirect.exe"]
FIRMWARE_PATTERNS = [
    "bios.bin", "bios-256k.bin", "vgabios-cirrus.bin", "vgabios-stdvga.bin", "kvmvapic.bin",
    "linuxboot.bin", "linuxboot_dma.bin", "multiboot.bin", "multiboot_dma.bin", "pvh.bin",
    "efi-*.rom", "pxe-*.rom", "keymaps/*",
]

# Windows 10/11 內建的 DLL（UCRT 的 api-ms-win-* 另外判斷）
SYSTEM_DLLS = {
    "advapi32.dll", "avrt.dll", "bcrypt.dll", "cfgmgr32.dll", "comctl32.dll", "comdlg32.dll",
    "crypt32.dll", "d2d1.dll", "d3d11.dll", "d3d9.dll", "d3dcompiler_47.dll", "dbghelp.dll", "dnsapi.dll",
    "dsound.dll", "dwmapi.dll", "dwrite.dll", "dxgi.dll", "gdi32.dll", "gdiplus.dll", "hid.dll",
    "imm32.dll", "iphlpapi.dll", "kernel32.dll", "ksuser.dll", "mfplat.dll", "mpr.dll", "msimg32.dll",
    "msvcrt.dll", "ncrypt.dll", "netapi32.dll", "normaliz.dll", "ntdll.dll", "ole32.dll", "oleaut32.dll",
    "opengl32.dll", "powrprof.dll", "propsys.dll", "psapi.dll", "rpcrt4.dll", "secur32.dll",
    "setupapi.dll", "shell32.dll", "shlwapi.dll", "user32.dll", "userenv.dll", "usp10.dll",
    "uxtheme.dll", "version.dll", "winhttp.dll", "wininet.dll", "winmm.dll", "winspool.drv",
    "wldap32.dll", "ws2_32.dll", "wsock32.dll", "wtsapi32.dll", "windowscodecs.dll", "winusb.dll",
    "dinput8.dll", "xinput1_4.dll", "glu32.dll", "mswsock.dll", "credui.dll", "cryptbase.dll",
    "mf.dll", "mfreadwrite.dll", "mfuuid.dll", "dxva2.dll", "wintrust.dll", "sspicli.dll",
    "authz.dll", "virtdisk.dll", "winhvplatform.dll", "winhvemulation.dll", "dxcore.dll",
}


def log(msg):
    print(msg, flush=True)


def fetch(url, dest: Path, sha256=None):
    if dest.exists() and (sha256 is None or file_sha256(dest) == sha256):
        return dest
    dest.parent.mkdir(parents=True, exist_ok=True)
    log(f"  下載 {url}")
    tmp = dest.with_suffix(dest.suffix + ".part")
    with urllib.request.urlopen(url) as r, open(tmp, "wb") as f:
        shutil.copyfileobj(r, f)
    if sha256 is not None and file_sha256(tmp) != sha256:
        tmp.unlink()
        raise SystemExit(f"SHA256 不符：{url}")
    tmp.replace(dest)
    return dest


def file_sha256(path: Path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def zstd_tar(path: Path):
    if shutil.which("zstd"):
        data = subprocess.run(["zstd", "-dc", str(path)], check=True, capture_output=True).stdout
    else:
        # Windows 通常沒有 zstd 指令；Python 3.14 起標準函式庫內建 zstd
        from compression import zstd
        data = zstd.decompress(path.read_bytes())
    return tarfile.open(fileobj=io.BytesIO(data), mode="r:")


def load_files_db(path: Path):
    """回傳 (packages, file_index)。packages[name] = {filename, version, sha256, files}；
    file_index[小寫路徑] = (套件名稱, 原始大小寫的相對路徑)。"""
    packages, entries = {}, {}
    with zstd_tar(path) as tar:
        for m in tar:
            if not m.isfile():
                continue
            pkgdir, kind = m.name.rsplit("/", 1)
            entries.setdefault(pkgdir, {})[kind] = tar.extractfile(m).read().decode("utf-8")
    for pkgdir, parts in entries.items():
        desc = parse_desc(parts.get("desc", ""))
        name = desc["NAME"][0]
        files = [l for l in parse_desc(parts.get("files", "")).get("FILES", [])]
        packages[name] = {
            "filename": desc["FILENAME"][0],
            "version": desc["VERSION"][0],
            "sha256": desc["SHA256SUM"][0],
            "license": desc.get("LICENSE", []),
            "url": desc.get("URL", [""])[0],
            # 原始碼封存檔以 BASE（建置腳本名稱）命名，例如 mingw-w64-qemu-11.1.2-1.src.tar.zst
            "base": desc.get("BASE", [name])[0],
            "files": files,
        }
    index = {}
    for name, pkg in packages.items():
        for f in pkg["files"]:
            if f.startswith(PREFIX):
                index.setdefault(f.lower(), (name, f[len(PREFIX):]))
    return packages, index


def parse_desc(text):
    result, key = {}, None
    for line in text.splitlines():
        if line.startswith("%") and line.endswith("%"):
            key = line.strip("%")
            result[key] = []
        elif line and key:
            result[key].append(line)
    return result


def pe_imports(path: Path):
    """解析 PE 檔的一般匯入與延遲載入匯入，回傳 DLL 名稱集合。"""
    data = path.read_bytes()
    pe = struct.unpack_from("<I", data, 0x3C)[0]
    if data[pe:pe + 4] != b"PE\0\0":
        raise ValueError(f"不是 PE 檔：{path}")
    nsect, = struct.unpack_from("<H", data, pe + 6)
    optsize, = struct.unpack_from("<H", data, pe + 20)
    opt = pe + 24
    magic, = struct.unpack_from("<H", data, opt)
    dirs = opt + (96 if magic == 0x10B else 112)
    sections = [struct.unpack_from("<8sIIII", data, opt + optsize + i * 40) for i in range(nsect)]

    def offset(rva):
        for _, vsize, vaddr, rawsize, rawptr in sections:
            if vaddr <= rva < vaddr + max(vsize, rawsize):
                return rva - vaddr + rawptr
        raise ValueError(f"RVA {rva:#x} 不在任何區段內：{path}")

    def cstr(off):
        return data[off:data.index(b"\0", off)].decode("ascii")

    names = set()
    imp_rva, _ = struct.unpack_from("<II", data, dirs + 8 * 1)
    if imp_rva:
        o = offset(imp_rva)
        while True:
            name_rva = struct.unpack_from("<IIIII", data, o)[3]
            if name_rva == 0:
                break
            names.add(cstr(offset(name_rva)))
            o += 20
    delay_rva, _ = struct.unpack_from("<II", data, dirs + 8 * 13)
    if delay_rva:
        o = offset(delay_rva)
        while True:
            _, name_rva = struct.unpack_from("<II", data, o)
            if name_rva == 0:
                break
            names.add(cstr(offset(name_rva)))
            o += 32
    return names


def is_system_dll(name):
    n = name.lower()
    return n in SYSTEM_DLLS or n.startswith(("api-ms-win-", "ext-ms-"))


class PackageCache:
    def __init__(self, cache: Path, packages):
        self.cache = cache
        self.packages = packages
        self.extracted = {}

    def root(self, name):
        """下載並解開套件（只取 bin、share/qemu、share/licenses），回傳解開的根目錄。"""
        if name in self.extracted:
            return self.extracted[name]
        pkg = self.packages[name]
        archive = fetch(f"{REPO}/{pkg['filename']}", self.cache / "pkg" / pkg["filename"], pkg["sha256"])
        root = self.cache / "x" / pkg["filename"]
        marker = root / ".done"
        if not marker.exists():
            if root.exists():
                shutil.rmtree(root)
            root.mkdir(parents=True)
            with zstd_tar(archive) as tar:
                for m in tar:
                    if m.name.startswith((PREFIX + "bin/", PREFIX + "share/qemu/", PREFIX + "share/licenses/")) \
                            and (m.isfile() or m.isdir()):
                        tar.extract(m, root, filter="data")
            marker.write_text("")
        self.extracted[name] = root
        return root

    def path_of(self, name, rel):
        return self.root(name) / PREFIX / rel


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", required=True, type=Path)
    ap.add_argument("--cache", type=Path, default=Path.home() / ".cache" / "xpoptical")
    args = ap.parse_args()

    log("讀取 MSYS2 套件清單…")
    files_db = fetch(f"{REPO}/ucrt64.files", args.cache / "ucrt64.files")
    packages, index = load_files_db(files_db)
    cache = PackageCache(args.cache, packages)

    def provider(rel):
        hit = index.get((PREFIX + rel).lower())
        if hit is None:
            raise SystemExit(f"MSYS2 中找不到 {rel}")
        return hit

    # 1. 從三個執行檔開始解析 DLL 相依
    log("解析 DLL 相依…")
    needed = {}  # 小寫檔名 -> (套件, 相對路徑)
    queue = []
    for rel in ENTRY_POINTS:
        queue.append(provider(rel))
    system, unknown = set(), set()
    while queue:
        name, rel = queue.pop()
        key = Path(rel).name.lower()
        if key in needed:
            continue
        needed[key] = (name, rel)
        for dll in sorted(pe_imports(cache.path_of(name, rel))):
            # Windows 內建的 DLL 優先（例如 MSYS2 的 mesa 也提供 opengl32.dll，但不該打包）
            if is_system_dll(dll):
                system.add(dll.lower())
            elif (hit := index.get((PREFIX + "bin/" + dll).lower())) is not None:
                queue.append(hit)
            else:
                unknown.add(dll)
    if unknown:
        raise SystemExit("以下 DLL 既不在 MSYS2 也不是已知的 Windows 內建 DLL：" + ", ".join(sorted(unknown)))

    # 2. 輸出
    out = args.out
    if out.exists():
        shutil.rmtree(out)
    (out / "bin").mkdir(parents=True)
    used_packages = set()
    for key, (name, rel) in sorted(needed.items()):
        src = cache.path_of(name, rel)
        # 檔名大小寫以實際檔案為準
        shutil.copy2(src, out / "bin" / src.name)
        used_packages.add(name)

    fw_pkg, _ = provider("share/qemu/bios-256k.bin")
    used_packages.add(fw_pkg)
    fw_root = cache.path_of(fw_pkg, "share/qemu")
    fw_out = out / "share" / "qemu"
    for f in sorted(fw_root.rglob("*")):
        rel = f.relative_to(fw_root).as_posix()
        if f.is_file() and any(fnmatch.fnmatch(rel, p) for p in FIRMWARE_PATTERNS):
            (fw_out / rel).parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(f, fw_out / rel)
    for required in ["bios-256k.bin", "vgabios-cirrus.bin", "keymaps/en-us"]:
        if not (fw_out / required).exists():
            raise SystemExit(f"韌體缺少 {required}")

    notices = [
        "XP 光學系統 — 執行引擎第三方元件",
        "",
        "engine 資料夾內的程式來自 MSYS2 專案（https://www.msys2.org/）的 UCRT64 套件，未經修改。",
        "各元件依其授權條款散布，授權文字位於 engine/licenses/。",
        f"原始碼可由 {SOURCES}/ 取得（各套件的檔名見 engine-manifest.json 的 sourceArchive），",
        "本程式每個版本的 GitHub Releases 頁面（https://github.com/ToleLin/winxp-optical-system/releases）",
        "也附有同版本所有元件的原始碼封存檔（XPOptical-<版本>-engine-sources.tar）。",
        "建置腳本位於 https://github.com/msys2/MINGW-packages 。",
        "",
        f"{'套件':<48} {'版本':<20} 授權",
    ]
    manifest = {}
    for name in sorted(used_packages):
        pkg = packages[name]
        notices.append(f"{name:<48} {pkg['version']:<20} {' '.join(pkg['license'])}")
        manifest[name] = {"version": pkg["version"], "sha256": pkg["sha256"],
                          "sourceArchive": f"{pkg['base']}-{pkg['version']}.src.tar.zst"}
        lic_root = cache.path_of(name, "share/licenses")
        if lic_root.exists():
            for f in lic_root.rglob("*"):
                if f.is_file():
                    dest = out / "licenses" / name / f.relative_to(lic_root)
                    dest.parent.mkdir(parents=True, exist_ok=True)
                    shutil.copy2(f, dest)
    (out / "THIRD-PARTY-NOTICES.txt").write_text("\n".join(notices) + "\n", encoding="utf-8")
    (out / "engine-manifest.json").write_text(json.dumps({
        "source": REPO,
        "sourceRepo": SOURCES,
        "packages": manifest,
        "systemDlls": sorted(system),
    }, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")

    # 3. 最終檢查：bin 內每個 PE 的匯入都能在 bin 內或 Windows 內建 DLL 找到
    present = {p.name.lower() for p in (out / "bin").iterdir()}
    for p in (out / "bin").iterdir():
        for dll in pe_imports(p):
            if dll.lower() not in present and not is_system_dll(dll):
                raise SystemExit(f"{p.name} 需要 {dll}，但沒有打包")

    size = sum(f.stat().st_size for f in out.rglob("*") if f.is_file())
    log(f"完成：{len(present)} 個檔案在 bin，{len(used_packages)} 個套件，共 {size / 1e6:.1f} MB")
    log(f"使用到的 Windows 內建 DLL：{', '.join(sorted(system))}")


if __name__ == "__main__":
    sys.exit(main())
