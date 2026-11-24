"""Self-update from the GitHub Releases of this project (macOS .dmg, Linux AppImage / .deb / .rpm).
A newer release is downloaded and checked against the SHA-256 GitHub publishes for it, then:
- macOS: the new Kadr.app from the disk image replaces the running one;
- AppImage: the file replaces itself;
- .deb / .rpm: installed with the system's password prompt (pkexec), so only when the user clicks.
Then Kadr starts again."""
import hashlib
import os
import platform
import shlex
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

import requests

from . import __version__, platform_utils

# the repository that built this copy (stamped by tools/stamp_version.py), so forks update from themselves
try:
    from . import __repo__ as REPO
except ImportError:
    REPO = "marseillered-design/kadr"
API = f"https://api.github.com/repos/{REPO}/releases/latest"
DOWNLOAD_PREFIX = f"https://github.com/{REPO}/releases/download/"


def version() -> str:
    return __version__


def is_dev() -> bool:
    """Running from source, or a build not made for a release: never update itself."""
    return not getattr(sys, "frozen", False) or __version__.startswith("0.0.0")


def compare(a: str, b: str) -> int:
    def parse(v):
        parts = [int(x) if x.isdigit() else 0 for x in (v or "").lstrip("v").split("-")[0].split(".")]
        return (parts + [0, 0, 0])[:3]
    pa, pb = parse(a), parse(b)
    return (pa > pb) - (pa < pb)


def install_kind() -> str | None:
    """How this copy was installed: 'mac', 'appimage', 'deb', 'rpm' or None (unknown: don't touch it)."""
    if platform_utils.MAC:
        return "mac" if app_bundle() is not None else None
    if not platform_utils.LINUX:
        return None
    if os.environ.get("APPIMAGE"):
        return "appimage"
    exe = Path(sys.executable).resolve()
    if str(exe).startswith("/opt/kadr/"):
        if shutil.which("dpkg") and Path("/var/lib/dpkg/info/kadr.list").exists():
            return "deb"
        if shutil.which("rpm") and subprocess.run(["rpm", "-q", "kadr"], capture_output=True).returncode == 0:
            return "rpm"
    return None


def needs_password(kind) -> bool:
    return kind in ("deb", "rpm")


def app_bundle() -> Path | None:
    p = Path(sys.executable).resolve()
    for parent in p.parents:
        if parent.suffix == ".app":
            return parent
    return None


def asset_name(kind) -> str | None:
    if kind == "mac":
        return "Kadr-macOS-AppleSilicon.dmg" if platform.machine() == "arm64" else "Kadr-macOS-Intel.dmg"
    return {"appimage": "Kadr-Linux-x86_64.AppImage", "deb": "Kadr-Linux-amd64.deb", "rpm": "Kadr-Linux-x86_64.rpm"}.get(kind)


def latest(kind) -> dict:
    r = requests.get(API, headers={"User-Agent": "Kadr-Updater", "Accept": "application/vnd.github+json"}, timeout=30)
    r.raise_for_status()
    d = r.json()
    info = {"version": str(d.get("tag_name", "")).lstrip("v"), "page": d.get("html_url") or f"https://github.com/{REPO}/releases"}
    want = asset_name(kind)
    for a in d.get("assets") or []:
        if a.get("name") == want:
            info["url"] = a.get("browser_download_url")
            info["size"] = a.get("size")
            digest = a.get("digest") or ""
            info["sha256"] = digest[7:].lower() if digest.startswith("sha256:") else None
            info["name"] = want
    return info


def download(info) -> str:
    url = info.get("url") or ""
    if not url.startswith(DOWNLOAD_PREFIX):
        raise RuntimeError("no download for this system in the release")
    folder = Path(tempfile.gettempdir()) / "KadrUpdate"
    folder.mkdir(exist_ok=True)
    path = folder / f"{info['version']}-{info['name']}"
    h = hashlib.sha256()
    size = 0
    with requests.get(url, headers={"User-Agent": "Kadr-Updater"}, stream=True, timeout=60) as r:
        r.raise_for_status()
        with open(path, "wb") as f:
            for chunk in r.iter_content(1 << 20):
                f.write(chunk)
                h.update(chunk)
                size += len(chunk)
    if info.get("size") and size != info["size"]:
        raise RuntimeError("the download is incomplete")
    if info.get("sha256") and h.hexdigest() != info["sha256"]:
        path.unlink(missing_ok=True)
        raise RuntimeError("the download does not match the release (SHA-256)")
    return str(path)


def _relaunch_later(command: str):
    """Start Kadr again two seconds after this copy has quit (only one copy may run)."""
    subprocess.Popen(["/bin/sh", "-c", f"sleep 2; {command}"], start_new_session=True,
                     stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)


def install(kind, path: str):
    """Puts the new version in place and schedules the restart. The caller quits right after."""
    if kind == "mac":
        bundle = app_bundle()
        mnt = tempfile.mkdtemp(prefix="kadr-dmg-")
        subprocess.run(["hdiutil", "attach", "-nobrowse", "-noautoopen", "-mountpoint", mnt, path], check=True, capture_output=True)
        try:
            new = bundle.with_name(bundle.name + ".new")
            old = bundle.with_name(bundle.name + ".old")
            shutil.rmtree(new, ignore_errors=True)
            shutil.rmtree(old, ignore_errors=True)
            subprocess.run(["ditto", str(Path(mnt) / "Kadr.app"), str(new)], check=True, capture_output=True)
            bundle.rename(old)
            new.rename(bundle)
            shutil.rmtree(old, ignore_errors=True)
        finally:
            subprocess.run(["hdiutil", "detach", mnt, "-quiet"], capture_output=True)
        _relaunch_later(f"open -n {shlex.quote(str(bundle))} --args --tray --updated")
    elif kind == "appimage":
        target = Path(os.environ["APPIMAGE"])
        tmp = target.with_name(target.name + ".new")
        shutil.copyfile(path, tmp)
        tmp.chmod(0o755)
        os.replace(tmp, target)
        _relaunch_later(f"exec {shlex.quote(str(target))} --tray --updated")
    elif kind in ("deb", "rpm"):
        if kind == "deb":
            cmd = ["pkexec", "apt-get", "install", "-y", "--allow-downgrades", path]
        else:
            cmd = ["pkexec", "dnf", "install", "-y", path] if shutil.which("dnf") else ["pkexec", "rpm", "-U", "--force", path]
        r = subprocess.run(cmd, capture_output=True, text=True)
        if r.returncode != 0:
            raise RuntimeError((r.stderr or r.stdout or "").strip()[-300:] or "install cancelled")
        _relaunch_later("exec /opt/kadr/kadr --tray --updated")
    else:
        raise RuntimeError("this copy of Kadr can’t update itself")


def cleanup():
    shutil.rmtree(Path(tempfile.gettempdir()) / "KadrUpdate", ignore_errors=True)
