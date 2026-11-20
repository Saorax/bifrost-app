"""make one old Brawlhalla version's replay copy on this PC, from that version's game files (downloaded from Steam
with the player's own account), so Bifrost Studio can watch its replays and read their match stats.

    python replay_copy.py --game DIR --out DIR --version 10.11 --names names.json --harness DIR --replay-version 270
                          [--label 10.11] [--tools DIR]

--tools is the folder holding abc_roundtrip.py (the player kit's air/tools, which brings RABCDAsm in air/.tools).
The game's own files are linked or copied unchanged; only BrawlhallaAir.swf is patched (a present() hook, stand-ins
for Steam / Epic / telemetry, no network, no full screen, the user folders kept inside the copy) and given the
harness, and the copy gets its own application id and no anti-cheat or launcher executables.
"""
from __future__ import annotations

import argparse
import datetime
import json
import os
import re
import shutil
import struct
import subprocess
import sys
import tempfile
import zlib
from pathlib import Path

HARNESS = 'QName(PackageNamespace("bh.harness"), "Harness")'


EC = re.compile(r'QName\(PackageNamespace\("flash\.external"(?:, "#\d+")?\), "ExtensionContext"\)')


STATIC_EC = re.compile(r'^(\s*)(getlex|findpropstrict|findproperty)(\s+)' + EC.pattern + r'\s*$')


PRESENT = re.compile(r'^(\s*)callpropvoid\s+QName\(PackageNamespace\(""(?:, "#\d+")?\), "present"\), 0\s*$')


DISPLAY_STATE = re.compile(r'^(\s*)(initproperty|setproperty)\s+QName\(PackageNamespace\(""(?:, "#\d+")?\), "displayState"\)\s*$')


BLOCKED = ("SteamAir", "DnaManager", "EpicAir", "AntiCheat", "NetworkNextAir", "NetLib")


FILE_LEX = re.compile(r'^(\s*)getlex(\s+)QName\(PackageNamespace\("flash\.filesystem"\), "File"\)\s*$')


USER_DIR = re.compile(r'^\s*getproperty\s+QName\(PackageNamespace\(""(?:, "#\d+")?\), "(userDirectory|documentsDirectory|desktopDirectory)"\)\s*$')


NET = {("flash.net", "Socket"): "GuardedSocket", ("flash.net", "SecureSocket"): "GuardedSecureSocket",
       ("flash.net", "URLLoader"): "GuardedURLLoader", ("flash.net", "URLStream"): "GuardedURLStream",
       ("flash.net", "DatagramSocket"): "GuardedDatagramSocket", ("flash.display", "Loader"): "GuardedLoader",
       ("flash.net", "NetConnection"): "GuardedNetConnection", ("flash.net", "NetStream"): "GuardedNetStream",
       ("flash.net", "navigateToURL"): "navigateToURL", ("flash.net", "sendToURL"): "sendToURL"}


NET_NAMES = "Socket|SecureSocket|URLLoader|URLStream|DatagramSocket|Loader|NetConnection|NetStream|navigateToURL|sendToURL"


NET_HINT = re.compile(r'"(?:' + NET_NAMES + r')"\)')


NET_USE = re.compile(r'^(\s*)(findpropstrict|findproperty|getlex|constructprop|callproperty|callpropvoid)(\s+)'
                     r'QName\(PackageNamespace\("(flash\.net|flash\.display)"(?:, "#\d+")?\), "(' + NET_NAMES + r')"\)(.*)$')


KEY = re.compile(r'"((?:var|method|Class)_\d+)"')


def swf_body(data: bytes) -> tuple[bytes, bytes]:
    sig = data[:3]
    if sig == b"FWS":
        raw = data
    elif sig == b"CWS":
        raw = data[:8] + zlib.decompress(data[8:])
    else:
        sys.exit("unsupported SWF compression " + repr(sig))
    nbits = raw[8] >> 3
    rect = (5 + nbits * 4 + 7) // 8
    head_end = 8 + rect + 4
    return raw[:head_end], raw[head_end:]


def tags(body: bytes):
    i = 0
    while i < len(body):
        code_len = struct.unpack_from("<H", body, i)[0]
        code, length = code_len >> 6, code_len & 0x3F
        i += 2
        if length == 0x3F:
            length = struct.unpack_from("<I", body, i)[0]
            i += 4
        yield code, body[i:i + length]
        i += length
        if code == 0:
            break


def tag(code: int, payload: bytes) -> bytes:
    return struct.pack("<HI", (code << 6) | 0x3F, len(payload)) + payload


def add_abc(swf: Path, abc_tag_bodies: list[bytes]) -> None:
    data = swf.read_bytes()
    head, body = swf_body(data)
    out = bytearray()
    added = False
    for code, payload in tags(body):
        if code == 82 and not added:
            for one in abc_tag_bodies:
                out += tag(82, one)
            added = True
        out += tag(code, payload)
    if not added:
        sys.exit("the game SWF has no ABC block")
    raw = bytearray(head) + out
    struct.pack_into("<I", raw, 4, len(raw))
    swf.write_bytes(b"CWS" + bytes(raw[3:8]) + zlib.compress(bytes(raw[8:]), 9))


def asm(name: str) -> str:
    return name.replace("\\", "\\\\").replace('"', '\\"')


def fall_hook(tree: Path, handler: str) -> int:
    head = re.compile(r'^\s*trait method QName\([^)]*\), "' + re.escape(asm(handler)) + r'"\)')
    found = 0
    for f in tree.rglob("*.class.asasm"):
        text = f.read_text(encoding="utf-8")
        if f'"{asm(handler)}")' not in text:
            continue
        lines = text.split("\n")
        changed = False
        for i, line in enumerate(lines):
            if not head.match(line):
                continue
            end = next(j for j in range(i, len(lines)) if lines[j].strip() == "end ; method")
            params = [l for l in lines[i:end] if l.strip().startswith("param ")]
            if len(params) != 3 or "uint" not in params[0] or "Boolean" not in params[2]:
                continue
            code = next(j for j in range(i, end) if lines[j].strip() == "code")
            scope = next(j for j in range(code, end) if lines[j].strip() == "pushscope")
            stack = next(j for j in range(i, code) if lines[j].strip().startswith("maxstack"))
            m = re.match(r"(\s*)maxstack (\d+)", lines[stack])
            lines[stack] = f"{m.group(1)}maxstack {int(m.group(2)) + 3}"
            ind = lines[scope][:len(lines[scope]) - len(lines[scope].lstrip())]
            lines[scope + 1:scope + 1] = [ind + "getlex              " + HARNESS, ind + "getlocal0", ind + "getlocal1",
                                          ind + 'callpropvoid        QName(PackageNamespace(""), "fell"), 2']
            found += 1
            changed = True
            break
        if changed:
            f.write_text("\n".join(lines), encoding="utf-8")
    return found


def dodge_hook(tree: Path, dodges: str) -> int:
    field = re.compile(r'^\s*(getproperty|initproperty|setproperty)\s+QName\(PackageNamespace\(""(?:, "#\d+")?\), "' + re.escape(asm(dodges)) + r'"\)\s*$')
    found = 0
    for f in tree.rglob("*.class.asasm"):
        text = f.read_text(encoding="utf-8")
        if f'"{asm(dodges)}")' not in text:
            continue
        lines = text.split("\n")
        code = [i for i, l in enumerate(lines) if l.strip()]
        hits = []
        for a, b, c in zip(code, code[1:], code[2:]):
            ma, mc = field.match(lines[a]), field.match(lines[c])
            if ma and mc and ma.group(1) == "getproperty" and lines[b].strip() in ("increment", "increment_i") and mc.group(1) != "getproperty":
                hits.append(c)
        for at in reversed(hits):
            ind = lines[at][:len(lines[at]) - len(lines[at].lstrip())]
            lines[at + 1:at + 1] = [ind + "getlex              " + HARNESS, ind + "getlocal0",
                                    ind + 'callpropvoid        QName(PackageNamespace(""), "dodged"), 1']
            start = max(i for i, l in enumerate(lines[:at]) if l.strip().startswith("maxstack"))
            m = re.match(r"(\s*)maxstack (\d+)", lines[start])
            lines[start] = f"{m.group(1)}maxstack {int(m.group(2)) + 2}"
        if hits:
            f.write_text("\n".join(lines), encoding="utf-8")
            found += len(hits)
    return found


INTERNAL = re.compile(r'PackageInternalNs\(""((?:, "#\d+")?)\)')


PLAY_OFFLINE = re.compile(r'QName\(PrivateNamespace\("[^"]*"(?:, "#\d+")?\), "OnPlayOfflineButton"\)')


def publish_play_offline(tree: Path) -> int:
    changed = 0
    for f in tree.rglob("*.asasm"):
        text = f.read_text(encoding="utf-8")
        if '"OnPlayOfflineButton")' not in text:
            continue
        new, n = PLAY_OFFLINE.subn('QName(PackageNamespace(""), "OnPlayOfflineButton")', text)
        if n:
            f.write_text(new, encoding="utf-8")
            changed += n
    return changed


def publish_internal(tree: Path) -> int:
    files = list(tree.rglob("*.asasm"))
    slots = sum(f.read_text(encoding="utf-8").count('trait slot QName(PackageInternalNs("")') for f in files if f.name.endswith(".class.asasm"))
    if slots < 100:
        return 0
    changed = 0
    for f in files:
        text = f.read_text(encoding="utf-8")
        new, n = INTERNAL.subn(r'PackageNamespace(""\1)', text)
        if n:
            f.write_text(new, encoding="utf-8")
            changed += n
    return changed


def patch_tree(tree: Path) -> dict:
    counts = {"present": 0, "static": 0, "types": 0, "displayState": 0, "folders": 0, "network": 0}
    for f in tree.rglob("*.asasm"):
        text = f.read_text(encoding="utf-8")
        if "ExtensionContext" not in text and '"present")' not in text and '"displayState")' not in text \
                and 'Directory")' not in text and not NET_HINT.search(text):
            continue
        lines = text.split("\n")
        out = []
        bump = False
        for line in lines:
            m = PRESENT.match(line)
            if m:
                ind = m.group(1)
                out += [ind + "dup", ind + "getlex              " + HARNESS, ind + "swap",
                        ind + 'callpropvoid        QName(PackageNamespace(""), "presenting"), 1', line]
                counts["present"] += 1
                bump = True
                continue
            m = STATIC_EC.match(line)
            if m:
                out.append(m.group(1) + m.group(2) + m.group(3) + HARNESS)
                counts["static"] += 1
                continue
            m = NET_USE.match(line)
            if m:
                guarded = NET[(m.group(4), m.group(5))]
                out.append(m.group(1) + m.group(2) + m.group(3) + 'QName(PackageNamespace("bh.harness"), "' + guarded + '")' + m.group(6))
                counts["network"] += 1
                continue
            m = DISPLAY_STATE.match(line)
            if m:
                out += [m.group(1) + "pop", m.group(1) + "pop"]
                counts["displayState"] += 1
                continue
            if USER_DIR.match(line) and out and FILE_LEX.match(out[-1]):
                m = FILE_LEX.match(out[-1])
                out[-1] = m.group(1) + "getlex" + m.group(2) + HARNESS
                counts["folders"] += 1
            new, n = EC.subn('QName(PackageNamespace(""), "Object")', line)
            counts["types"] += n
            out.append(new)
        text = "\n".join(out)
        if bump:
            text = bump_maxstack(text)
        f.write_text(text, encoding="utf-8")
    return counts


def bump_maxstack(text: str) -> str:
    at = text.find('"presenting"), 1')
    start = text.rfind("maxstack", 0, at)
    m = re.compile(r"maxstack (\d+)").match(text, start)
    return text[:start] + f"maxstack {int(m.group(1)) + 2}" + text[m.end():]


MISSING = "~missing:"


CLASS_ANCHORS = {"ScreenManager": ["var_960", "var_14057", "var_14958", "owner"], "GameApp": ["forceOffline", "mainInstance"],
                 "ScreenBase": ["focusedScreen", "isActive"], "Class_536": ["var_10474"], "Class_83": ["var_5985", "var_437"],
                 "Class_329": ["method_4102"]}


def resolve_classes(tree: Path, table: dict) -> list[str]:
    left = []
    for key in table.pop("__unresolved__", []):
        anchors = [table[a] for a in CLASS_ANCHORS.get(key, []) if a in table and not table[a].startswith(MISSING)]
        score: dict[str, int] = {}
        for f in tree.rglob("*.class.asasm"):
            text = f.read_text(encoding="utf-8")
            n = sum(1 for a in anchors if f'"{asm(a)}")' in text and
                    re.search(r'trait (?:slot|const|method|getter|setter) QName\([^)]*\), "' + re.escape(asm(a)) + '"', text))
            if n:
                score[f.name[:-len(".class.asasm")]] = n
        best = sorted(score.items(), key=lambda x: -x[1])
        if best and best[0][1] >= 2 and (len(best) == 1 or best[1][1] < best[0][1]):
            table[key] = best[0][0]
        else:
            left.append(f"{key} (anchors: {best[:3]})")
    return left


ANTI_CHEAT = ("EasyAntiCheat", "EasyAntiCheat_EOS", "start_protected_game")


def game_exe(game: Path) -> str:
    settings = game / "EasyAntiCheat" / "Settings.json"
    if settings.exists():
        text = settings.read_text(encoding="utf-8", errors="replace")
        m = re.search(r'"executable"\s*:\s*"([^"]+)"', text)
        if not m:
            sys.exit(f"{settings}: no executable named")
        return m.group(1)
    return "Brawlhalla.exe"


def make_offline(game: Path, out: Path) -> None:
    host = game_exe(game)
    if not (game / host).exists():
        sys.exit(f"the game executable {host} is not in {game}")
    for name in ANTI_CHEAT:
        target = out / name
        if target.is_dir():
            shutil.rmtree(target)
    for f in list(out.iterdir()):
        if f.is_file() and (f.suffix.lower() == ".exe" or f.name.lower().startswith("eossdk") or "anticheat" in f.name.lower()):
            f.unlink()
    shutil.copyfile(game / host, out / "Brawlhalla.exe")
    data = (out / "Brawlhalla.exe").read_bytes()
    marks = [w.encode(enc) for w in ("EasyAntiCheat", "start_protected_game", "eac-prod") for enc in ("ascii", "utf-16-le")]
    if b"CaptiveAppEntry" not in data or "Adobe AIR".encode("utf-16-le") not in data:
        sys.exit(f"{host} is not an AIR captive-runtime host (no CaptiveAppEntry / Adobe AIR in it): refusing to make this copy")
    if any(m in data for m in marks):
        sys.exit(f"{host} looks like an anti-cheat launcher, not the AIR game: refusing to make this copy")
    left = [p.relative_to(out).as_posix() for p in out.rglob("*") if p.is_file() and
            (p.suffix.lower() == ".exe" and p.name != "Brawlhalla.exe" and not p.relative_to(out).as_posix().startswith("Adobe AIR/")
             or "easyanticheat" in p.as_posix().lower() or "eossdk" in p.name.lower())]
    if left:
        sys.exit("refusing: anti-cheat or extra executables left in the copy: " + ", ".join(left[:10]))


def link_tree(source: Path, target: Path, own: set[str]) -> int:
    n = 0
    for f in source.rglob("*"):
        rel = f.relative_to(source)
        parts = rel.parts
        if len(parts) >= 4 and parts[:3] == ("META-INF", "AIR", "extensions") and parts[3] in BLOCKED:
            continue
        dest = target / rel
        if f.is_dir():
            dest.mkdir(parents=True, exist_ok=True)
            continue
        if rel.as_posix() in own:
            continue
        if dest.exists():
            if dest.stat().st_size == f.stat().st_size and os.path.samefile(dest, f):
                continue
            dest.unlink()
        dest.parent.mkdir(parents=True, exist_ok=True)
        try:
            os.link(f, dest)
        except OSError:
            shutil.copy2(f, dest)
        n += 1
    return n


def app_xml(text: str, version: str) -> str:
    text, n = re.subn(r"<id>[^<]*</id>", f"<id>BifrostReplay{re.sub(r'[^A-Za-z0-9-]', '', version)}</id>", text, count=1)
    if n != 1:
        sys.exit("no <id> in application.xml")
    for blocked in BLOCKED:
        text = re.sub(r"\s*<extensionID>" + blocked + r"</extensionID>", "", text)
    window = re.search(r"<initialWindow>(.*?)</initialWindow>", text, re.S)
    if not window:
        sys.exit("no <initialWindow> in application.xml")
    inner = re.sub(r"\s*<(x|y)>[^<]*</\1>", "", window.group(1))
    inner = re.sub(r"<visible>[^<]*</visible>", "<visible>true</visible>", inner)
    inner += "\n    <x>-32000</x>\n    <y>-32000</y>\n  "
    return text[:window.start(1)] + inner + text[window.end(1):]

def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--game", required=True)
    ap.add_argument("--out", required=True)
    ap.add_argument("--version", required=True)
    ap.add_argument("--label")
    ap.add_argument("--names", required=True)
    ap.add_argument("--harness", required=True)
    ap.add_argument("--replay-version", type=int, action="append", required=True)
    ap.add_argument("--tools", default=str(Path(sys.executable).resolve().parents[1] / "air" / "tools"))
    args = ap.parse_args()
    game, out, roundtrip = Path(args.game), Path(args.out), Path(args.tools) / "abc_roundtrip.py"
    if not (game / "BrawlhallaAir.swf").exists():
        sys.exit(f"no BrawlhallaAir.swf in {game}")
    if not roundtrip.exists():
        sys.exit(f"no abc_roundtrip.py in {args.tools}")
    names = json.loads(Path(args.names).read_text(encoding="utf-8"))
    abc = [p.read_bytes() for p in sorted(Path(args.harness).glob("*.abc"))]
    if not abc:
        sys.exit(f"no harness blocks in {args.harness}")
    head, body = swf_body((game / "BrawlhallaAir.swf").read_bytes())
    has_offline_switch = b"-forceoffline" in body
    with tempfile.TemporaryDirectory() as t:
        work = Path(t)
        tree = work / "asasm"
        r = subprocess.run([sys.executable, "-I", str(roundtrip), "disasm", "--swf", str(game / "BrawlhallaAir.swf"), "--src", str(tree)],
                           capture_output=True, text=True)
        if r.returncode != 0:
            sys.exit("reading the game's code failed:\n" + r.stdout[-3000:] + r.stderr[-3000:])
        left = resolve_classes(tree, names)
        if left:
            sys.exit(f"names not found in this version: " + ", ".join(left))
        counts = patch_tree(tree)
        counts["internal"] = publish_internal(tree)
        counts["playOffline"] = publish_play_offline(tree)
        counts["dodge"] = 0 if names["var_39"].startswith(MISSING) else dodge_hook(tree, names["var_39"])
        counts["fall"] = 0 if names["method_1042"].startswith(MISSING) else fall_hook(tree, names["method_1042"])
        if counts["present"] != 1 or counts["static"] == 0 or counts["dodge"] != (0 if names["var_39"].startswith(MISSING) else 1):
            sys.exit(f"patch points not as expected: {counts}")
        swf = work / "BrawlhallaAir.swf"
        r = subprocess.run([sys.executable, "-I", str(roundtrip), "build", "--src", str(tree), "--swf", str(game / "BrawlhallaAir.swf"),
                            "--out", str(swf)], capture_output=True, text=True)
        if r.returncode != 0:
            sys.exit("assembling the patched game failed:\n" + r.stdout[-3000:] + r.stderr[-3000:])
        add_abc(swf, abc)
        out.mkdir(parents=True, exist_ok=True)
        own = {"BrawlhallaAir.swf", "META-INF/AIR/application.xml", "steam_appid.txt",
               "bifrost-harness.json", "bifrost-harness-names.json", "bifrost-harness-info.json"}
        linked = link_tree(game, out, own)
        for name in ("BrawlhallaAir.swf", "META-INF/AIR/application.xml"):
            if (out / name).exists():
                (out / name).unlink()
        shutil.copyfile(swf, out / "BrawlhallaAir.swf")
    (out / "META-INF" / "AIR").mkdir(parents=True, exist_ok=True)
    xml = (game / "META-INF" / "AIR" / "application.xml").read_text(encoding="utf-8")
    (out / "META-INF" / "AIR" / "application.xml").write_text(app_xml(xml, args.version), encoding="utf-8")
    (out / "steam_appid.txt").unlink(missing_ok=True)
    make_offline(game, out)
    names.pop("__unresolved__", None)
    (out / "bifrost-harness-names.json").write_text(json.dumps(names, indent=1, sort_keys=True) + "\n", encoding="utf-8")
    info = {"game": args.label or args.version, "replayVersions": sorted(set(args.replay_version)), "made": datetime.datetime.now().isoformat(timespec="seconds"),
            "sourceSwf": str((game / "BrawlhallaAir.swf").resolve()), "offlineSwitch": has_offline_switch, "patched": counts}
    (out / "bifrost-harness-info.json").write_text(json.dumps(info, indent=1) + "\n", encoding="utf-8")
    print(f"patched: {counts}; {len(names)} names; {linked} files linked; replay versions {info['replayVersions']}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
