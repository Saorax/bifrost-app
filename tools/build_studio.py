"""publish Bifrost (studio/, the player's app) for one or more operating systems.

    python tools/build_studio.py                 # this computer's OS
    python tools/build_studio.py --rid win-x64 --rid linux-x64 --rid osx-arm64
    python tools/build_studio.py --game "D:\\Steam\\steamapps\\common\\Brawlhalla"
    python tools/build_studio.py --icon build/studio-app/app.ico

each goes to build/studio-app/<rid>/ as a self-contained single file (no .NET install needed to run it):
BifrostStudio.exe on Windows, BifrostStudio on Linux and macOS. Needs the .NET 10 SDK.
"""

from __future__ import annotations

import argparse
import colorsys
from pathlib import Path
import platform
import re
import struct
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[1]
RIDS = ("win-x64", "win-arm64", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64")
ICON = ROOT / "build" / "studio-app" / "app.ico"
HUE = 215 / 360


def host_rid() -> str:
    arm = platform.machine().lower() in ("arm64", "aarch64")
    system = {"win32": "win", "darwin": "osx"}.get(sys.platform, "linux")
    return f"{system}-{'arm64' if arm else 'x64'}"


def steam_folders() -> list[Path]:
    found: list[Path] = []
    if sys.platform == "win32":
        import winreg
        for hive, key, value in ((winreg.HKEY_CURRENT_USER, r"Software\Valve\Steam", "SteamPath"),
                                 (winreg.HKEY_LOCAL_MACHINE, r"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath")):
            try:
                with winreg.OpenKey(hive, key) as k:
                    found.append(Path(winreg.QueryValueEx(k, value)[0]))
            except OSError:
                pass
    found += [Path.home() / ".steam" / "steam", Path.home() / "Library" / "Application Support" / "Steam"]
    return found


def find_game() -> Path | None:
    libraries: list[Path] = []
    for steam in steam_folders():
        libraries.append(steam)
        try:
            vdf = (steam / "steamapps" / "libraryfolders.vdf").read_text(encoding="utf-8", errors="replace")
        except OSError:
            continue
        libraries += [Path(p.replace("\\\\", "\\")) for p in re.findall(r'"path"\s+"([^"]+)"', vdf)]
    for library in libraries:
        game = library / "steamapps" / "common" / "Brawlhalla"
        if (game / "Brawlhalla.exe").exists():
            return game
    return None


def resources(exe: bytes) -> dict[tuple[int, int], bytes]:
    pe = struct.unpack_from("<I", exe, 0x3C)[0]
    sections = struct.unpack_from("<H", exe, pe + 6)[0]
    optional = struct.unpack_from("<H", exe, pe + 20)[0]
    directories = pe + 24 + (96 if struct.unpack_from("<H", exe, pe + 24)[0] == 0x10B else 112)
    rva = struct.unpack_from("<I", exe, directories + 16)[0]
    table = [struct.unpack_from("<8sIIII", exe, pe + 24 + optional + 40 * i) for i in range(sections)]

    def offset(address: int) -> int:
        for _, size, start, raw_size, raw in table:
            if start <= address < start + max(size, raw_size):
                return address - start + raw
        raise ValueError("resource outside every section")

    base = offset(rva)

    def entries(at: int) -> list[tuple[int, int]]:
        count = sum(struct.unpack_from("<HH", exe, at + 12))
        return [struct.unpack_from("<II", exe, at + 16 + 8 * i) for i in range(count)]

    def leaf(at: int) -> bytes:
        while True:
            pointer = entries(at)[0][1]
            if not pointer & 0x80000000:
                address, size = struct.unpack_from("<II", exe, base + pointer)
                return exe[offset(address):offset(address) + size]
            at = base + (pointer & 0x7FFFFFFF)

    found: dict[tuple[int, int], bytes] = {}
    for kind, pointer in entries(base):
        if kind in (3, 14):
            for name, inner in entries(base + (pointer & 0x7FFFFFFF)):
                found[(kind, name)] = leaf(base + (inner & 0x7FFFFFFF))
    return found


def recoloured(dib: bytes) -> bytes:
    header, width, height = struct.unpack_from("<Iii", dib, 0)
    if struct.unpack_from("<H", dib, 14)[0] != 32:
        return dib
    out = bytearray(dib)
    for i in range(header, header + 4 * width * (height // 2), 4):
        b, g, r = out[i], out[i + 1], out[i + 2]
        if r == g == b:
            continue
        top, low = max(r, g, b) / 255, min(r, g, b) / 255
        red, green, blue = colorsys.hsv_to_rgb(HUE, (top - low) / top, top)
        out[i], out[i + 1], out[i + 2] = round(blue * 255), round(green * 255), round(red * 255)
    return bytes(out)


def write_icon(game: Path, out: Path) -> bool:
    exe = game / "Brawlhalla.exe.bifrost-original"
    if not exe.exists():
        exe = game / "Brawlhalla.exe"
    try:
        found = resources(exe.read_bytes())
    except (OSError, ValueError, struct.error):
        return False
    groups = sorted(k for k in found if k[0] == 14)
    if not groups:
        return False
    group = found[groups[0]]
    images = []
    for i in range(struct.unpack_from("<H", group, 4)[0]):
        entry = group[6 + 14 * i:6 + 14 * i + 12]
        ident = struct.unpack_from("<H", group, 6 + 14 * i + 12)[0]
        if (3, ident) in found:
            images.append((entry, recoloured(found[(3, ident)])))
    if not images:
        return False
    data = struct.pack("<HHH", 0, 1, len(images))
    at = 6 + 16 * len(images)
    for entry, image in images:
        data += entry[:8] + struct.pack("<II", len(image), at)
        at += len(image)
    out.parent.mkdir(parents=True, exist_ok=True)
    out.write_bytes(data + b"".join(image for _, image in images))
    return True


def publish_command(rid: str, repo: str | None = None, repos: dict[str, str] | None = None,
                    version: str | None = None, icon: Path | None = None, mirror: str | None = None) -> list[str]:
    extra = [f"-p:ReleaseRepo={repo}"] if repo else []
    extra += [f"-p:ReleaseMirror={mirror}"] if mirror else []
    extra += [f"-p:{product.capitalize()}Repo={name}" for product, name in (repos or {}).items() if name]
    extra += [f"-p:AppVersion={version}"] if version else []
    extra += [f"-p:ApplicationIcon={icon}"] if icon and rid.startswith("win") else []
    return ["dotnet", "publish", str(ROOT / "studio" / "BifrostStudio.csproj"), "-c", "Release", "-r", rid,
            "--self-contained", "true", "-p:PublishSingleFile=true", "-p:IncludeNativeLibrariesForSelfExtract=true", "-p:DebugType=none",
            *extra, "-o", str(ROOT / "build" / "studio-app" / rid)]


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    parser.add_argument("--rid", action="append", choices=RIDS, help="runtime identifier (repeatable; default: this OS)")
    parser.add_argument("--repo", default=None, help="OWNER/NAME: the GitHub repository of every release, built into the app")
    parser.add_argument("--app-repo", default=None, help="OWNER/NAME of the app's releases (default --repo)")
    parser.add_argument("--bifrost-repo", default=None, help="OWNER/NAME of Bifrost's releases (default --repo)")
    parser.add_argument("--official-repo", default=None, help="OWNER/NAME of Official's releases (default --repo)")
    parser.add_argument("--version", default=None, help="the app's release name (YYYYMMDD-NN<commit>), built in")
    parser.add_argument("--mirror", default=None, help="URL of a copy of the releases (<mirror>/<repo name>/latest.txt names the newest), built in")
    parser.add_argument("--game", type=Path, default=None, help="the Brawlhalla install the icon is read from (default: Steam's)")
    parser.add_argument("--icon", type=Path, default=None, help="an .ico made earlier, used instead of reading the install")
    args = parser.parse_args(argv)
    rids = args.rid or [host_rid()]
    icon = None
    if args.icon:
        if not args.icon.is_file():
            print(f"no icon at {args.icon}")
            return 1
        icon = args.icon.resolve()
    elif any(rid.startswith("win") for rid in rids):
        game = args.game or find_game()
        if game and write_icon(game, ICON):
            icon = ICON
            print(f"icon from {game}")
        else:
            print("no Brawlhalla install with an icon found (--game DIR): the default icon is used")
    for rid in rids:
        repos = {"app": args.app_repo, "bifrost": args.bifrost_repo, "official": args.official_repo}
        named = [args.repo] if args.repo else [name for name in repos.values() if name]
        print(f"publishing {rid}" + (f" (releases from {', '.join(named)})" if named else " (no release repository: development)"),
              flush=True)
        if subprocess.run(publish_command(rid, args.repo, repos, args.version, icon, args.mirror)).returncode != 0:
            print(f"failed: {rid}")
            return 1
        print(ROOT / "build" / "studio-app" / rid)
    return 0


if __name__ == "__main__":
    sys.exit(main())
