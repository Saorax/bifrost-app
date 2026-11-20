"""build the replay pack: what Bifrost Studio needs to make old-version replay copies on a player's PC. It holds no
game files: per version the Steam manifest to download, the download size, the member-name table and the replay
versions; the compiled replay harness; and replay_copy.py. The game files come from Steam with the player's account.

    python tools/replay_pack.py [--copies DIR] [--history DIR] [--harness-source DIR] [--out build/replay-pack]

--copies is the folder of replay-harness copies (each with bifrost-harness-info.json, bifrost-harness-names.json)
and walk-results.tsv; --history the release-history archive (manifests/ for sizes); --harness-source the folder
with make_copy.py, used to compile the harness. Upload build/replay-pack/replay-pack.zip as the asset of the
release tagged replay-pack in the app repository.
"""
from __future__ import annotations

import argparse
import datetime
import hashlib
import json
import os
import re
import shutil
import sys
import tempfile
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
APP, DEPOT = 291550, 291551
EXTRA = {"10.11": "252787301610705290"}
AUDIO_KEPT = re.compile(r"(^|[\\/])Init\.bnk$", re.I)


def download_bytes(manifest: Path) -> int | None:
    if not manifest.exists():
        return None
    total = 0
    started = False
    for line in manifest.read_text(encoding="utf-8", errors="replace").splitlines():
        if line.strip().startswith("Size Chunks"):
            started = True
            continue
        if not started:
            continue
        m = re.match(r"\s*(\d+)\s+\d+\s+[0-9a-f]{40}\s+(\d+)\s+(.+)$", line)
        if not m:
            continue
        size, flags, name = int(m.group(1)), int(m.group(2)), m.group(3).strip()
        if flags & 64:
            continue
        if re.match(r"audio[\\/]", name, re.I) and not AUDIO_KEPT.search(name):
            continue
        total += size
    return total


def main() -> int:
    local = Path(os.environ.get("LOCALAPPDATA", "")) / "Bifrost" / "Build" / "Work" / "replay-harness"
    ap = argparse.ArgumentParser()
    ap.add_argument("--copies", default=str(local))
    ap.add_argument("--history", default="C:/Coding/BRAWLHALLA PROJECTS/data/release-history")
    ap.add_argument("--harness-source", default="C:/Coding/bifrost/data/replay-harness")
    ap.add_argument("--out", default=str(ROOT / "build" / "replay-pack"))
    args = ap.parse_args()
    copies, history, out = Path(args.copies), Path(args.history), Path(args.out)
    manifests = {}
    walk = copies / "walk-results.tsv"
    for row in walk.read_text(encoding="utf-8").splitlines():
        cells = row.split("\t")
        if len(cells) > 2 and cells[2].isdigit():
            manifests[cells[1]] = cells[2]
    manifests.update(EXTRA)
    stage = out / "replay-pack"
    if stage.exists():
        shutil.rmtree(stage)
    (stage / "names").mkdir(parents=True)
    (stage / "harness").mkdir()
    sys.path.insert(0, args.harness_source)
    import make_copy
    with tempfile.TemporaryDirectory() as t:
        blocks = make_copy.compile_harness(Path(t))
    for i, block in enumerate(blocks):
        (stage / "harness" / f"harness-{i:02d}.abc").write_bytes(block)
    shutil.copyfile(ROOT / "tools" / "replay_copy.py", stage / "replay_copy.py")
    versions, skipped = [], []
    for d in sorted(copies.iterdir()):
        info_file, names_file = d / "bifrost-harness-info.json", d / "bifrost-harness-names.json"
        if not d.is_dir() or not info_file.exists() or not names_file.exists():
            continue
        tag = d.name
        if tag not in manifests:
            skipped.append(f"{tag} (no manifest)")
            continue
        info = json.loads(info_file.read_text(encoding="utf-8"))
        size = download_bytes(history / "manifests" / f"manifest_{DEPOT}_{manifests[tag]}.txt")
        shutil.copyfile(names_file, stage / "names" / f"{tag}.json")
        versions.append({"tag": tag, "label": info.get("game") or tag, "replayVersions": info["replayVersions"],
                         "app": APP, "depot": DEPOT, "manifest": manifests[tag], "bytes": size or 0})
    versions.sort(key=lambda v: -max(v["replayVersions"]))
    index = {"format": 1, "made": datetime.datetime.now().isoformat(timespec="seconds"), "versions": versions}
    (stage / "index.json").write_text(json.dumps(index, indent=1) + "\n", encoding="utf-8")
    pack = out / "replay-pack.zip"
    with zipfile.ZipFile(pack, "w", zipfile.ZIP_DEFLATED) as z:
        for f in sorted(stage.rglob("*")):
            if f.is_file():
                z.write(f, f.relative_to(stage).as_posix())
    sha = hashlib.sha256(pack.read_bytes()).hexdigest()
    total = sum(v["bytes"] for v in versions)
    print(f"{len(versions)} versions, all of them {total / 2**30:.1f} GB from Steam; {pack} ({pack.stat().st_size:,} bytes, sha256 {sha})")
    if skipped:
        print("left out: " + ", ".join(skipped))
    return 0


if __name__ == "__main__":
    sys.exit(main())
