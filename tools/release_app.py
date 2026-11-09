"""makes a release of the app: BifrostStudio.exe and its app.json, and with --publish a GitHub release of them.

    python tools/release_app.py                      # build only, into build/release/app-<version>/
    python tools/release_app.py --publish            # and create the release app-<version> on the app's repository
    python tools/release_app.py --icon build/studio-app/app.ico --publish

the version is YYYYMMDD-NN<commit>: the UTC day, the app's release number that day (from the repository's releases)
and the current commit. the repositories are built into the app; publishing needs the GitHub CLI signed in (in
GitHub Actions, GH_TOKEN). the release workflow (.github/workflows/release.yml) runs this on every push to main.
"""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import re
import shutil
import subprocess
import sys
import time

ROOT = Path(__file__).resolve().parents[1]
REPOS = {"app": "Saorax/bifrost-app", "bifrost": "Saorax/bifrost", "official": "Saorax/bh-client"}
MIRROR = "https://bifrost.saorax.com/download"


def git(*args: str) -> str:
    return subprocess.run(["git", "-C", str(ROOT), *args], capture_output=True, text=True, check=True).stdout.strip()


def next_version(repo: str) -> str:
    day = time.strftime("%Y%m%d", time.gmtime())
    out = subprocess.run(["gh", "release", "list", "--repo", repo, "--limit", "200", "--json", "tagName"],
                         capture_output=True, text=True)
    if out.returncode != 0:
        raise SystemExit(f"release_app: gh couldn't list {repo}'s releases: {out.stderr.strip()[:300]}")
    numbers = [int(m.group(1)) for r in json.loads(out.stdout or "[]") if (m := re.match(rf"app-{day}-(\d\d)", r["tagName"]))]
    return f"{day}-{max(numbers, default=0) + 1:02d}{git('rev-parse', '--short=8', 'HEAD')}"


def changes(repo: str) -> str:
    latest = subprocess.run(["gh", "release", "list", "--repo", repo, "--limit", "1", "--json", "tagName"],
                            capture_output=True, text=True)
    tags = [r["tagName"] for r in json.loads(latest.stdout or "[]")] if latest.returncode == 0 else []
    if tags:
        view = subprocess.run(["gh", "release", "view", tags[0], "--repo", repo, "--json", "targetCommitish"],
                              capture_output=True, text=True)
        since = json.loads(view.stdout or "{}").get("targetCommitish") if view.returncode == 0 else None
        if since:
            log = subprocess.run(["git", "-C", str(ROOT), "log", "--no-merges", "--format=- %s", f"{since}..HEAD"],
                                 capture_output=True, text=True)
            if log.returncode == 0 and log.stdout.strip():
                return f"Changes since {tags[0]}:\n\n{log.stdout.strip()}"
    return f"- {git('log', '-1', '--format=%s')}"


def sha256(path: Path) -> str:
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def main(argv: list[str] | None = None) -> int:
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    for product, repo in REPOS.items():
        p.add_argument(f"--{product}-repo", default=repo, help=f"OWNER/NAME of {product} releases (default {repo})")
    p.add_argument("--icon", type=Path, default=None, help="the .ico to build in (default: read from the Steam install)")
    p.add_argument("--mirror", default=MIRROR, help=f"the copy of the releases used when GitHub can't be reached (default {MIRROR}, '' for none)")
    p.add_argument("--version", default=None, help="the release name (default: YYYYMMDD-NN<commit>)")
    p.add_argument("--publish", action="store_true", help="create the GitHub release")
    a = p.parse_args(argv)
    repos = {product: getattr(a, f"{product}_repo") for product in REPOS}
    version = a.version or next_version(repos["app"])
    build = [sys.executable, str(ROOT / "tools" / "build_studio.py"), "--rid", "win-x64", f"--version={version}",
             *[f"--{product}-repo={repo}" for product, repo in repos.items()],
             *([f"--mirror={a.mirror}"] if a.mirror else []),
             *([f"--icon={a.icon}"] if a.icon else [])]
    if subprocess.run(build).returncode != 0:
        return 1
    folder = ROOT / "build" / "release" / f"app-{version}"
    shutil.rmtree(folder, ignore_errors=True)
    folder.mkdir(parents=True)
    exe = folder / "BifrostStudio.exe"
    shutil.copy2(ROOT / "build" / "studio-app" / "win-x64" / "BifrostStudio.exe", exe)
    manifest = folder / "app.json"
    manifest.write_text(json.dumps({"product": "app", "release": version, "asset": exe.name, "size": exe.stat().st_size,
                                    "sha256": sha256(exe), "commit": git("rev-parse", "HEAD"),
                                    "built": time.strftime("%Y-%m-%d %H:%M:%S", time.gmtime())}, indent=1) + "\n",
                        encoding="utf-8")
    print(f"app {version} -> {folder}")
    if not a.publish:
        return 0
    notes = changes(repos["app"])
    out = subprocess.run(["gh", "release", "create", f"app-{version}", str(exe), str(manifest), "--repo", repos["app"],
                          "--title", f"Bifrost app {version}", "--notes", notes, "--target", git("rev-parse", "HEAD")])
    return out.returncode


if __name__ == "__main__":
    sys.exit(main())
