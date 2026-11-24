"""Writes the release version (from the VERSION environment variable, e.g. v1.2.0) into kadr/__init__.py,
so the built app knows which version it is and the updater can compare it with the newest release."""
import os
from pathlib import Path

v = os.environ.get("VERSION", "v0.0.0-dev").lstrip("v")
(Path(__file__).resolve().parents[1] / "kadr" / "__init__.py").write_text(f'__version__ = "{v}"\n', encoding="utf-8")
print("version", v)
