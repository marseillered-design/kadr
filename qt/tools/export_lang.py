"""Copies the interface texts from the Windows version (src/Lang.cs) into qt/kadr/lang.json, so both versions
speak the same 7 languages. Run after adding or changing a row in Lang.cs:  python qt/tools/export_lang.py"""
import json
import re
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
src = (ROOT / "src" / "Lang.cs").read_text(encoding="utf-8")
lit = re.compile(r'"((?:[^"\\]|\\.)*)"')


def unescape(s):
    return re.sub(r"\\(.)", lambda m: {"n": "\n", "t": "\t"}.get(m.group(1), m.group(1)), s)


out = {}
for m in re.finditer(r"^\s*A\((.*)\);\s*$", src, re.M):
    parts = [unescape(x) for x in lit.findall(m.group(1))]
    if len(parts) != 8:
        raise SystemExit(f"row needs a key + 7 languages: {parts[:2]}")
    out[parts[0]] = parts[1:]
(ROOT / "qt" / "kadr" / "lang.json").write_text(json.dumps(out, ensure_ascii=False, indent=0), encoding="utf-8")
print(len(out), "texts written to qt/kadr/lang.json")
