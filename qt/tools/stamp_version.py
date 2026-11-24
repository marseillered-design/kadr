"""Writes the release version (VERSION, e.g. v1.2.0) and the repository that built it (GITHUB_REPOSITORY) into
kadr/__init__.py: the updater compares the version with the newest release of that repository, so a fork's
builds update from the fork and never from the original."""
import os
from pathlib import Path

v = os.environ.get("VERSION", "v0.0.0-dev").lstrip("v")
repo = os.environ.get("GITHUB_REPOSITORY", "marseillered-design/kadr")
(Path(__file__).resolve().parents[1] / "kadr" / "__init__.py").write_text(
    f'__version__ = "{v}"\n__repo__ = "{repo}"\n', encoding="utf-8")
print("version", v, "from", repo)
