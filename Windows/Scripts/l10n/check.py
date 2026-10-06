#!/usr/bin/env python3
"""Kiểm tra bản dịch tiếng Anh của bản Windows.

Quét mã C#/XAML trong Windows/src tìm chuỗi nguồn tiếng Việt (Loc.T("…"), Loc.F("…", …), {ui:L '…'},
new LocalizedText("vi", "en") không cần), so với mọi file Localization/en*.json của app, rồi:
  - in các khoá còn thiếu bản dịch;
  - kiểm tra định dạng (%@, %lld, %d…) của khoá và bản dịch khớp nhau;
  - --write <file>: ghi các khoá thiếu (giá trị rỗng) vào file JSON để dịch.

Dùng: python Windows/Scripts/l10n/check.py [--write Windows/src/CleanBoost.App/Localization/en.todo.json]
"""
import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SRC = ROOT / "src"
LOC_DIR = SRC / "CleanBoost.App" / "Localization"

# Chuỗi C# thường "…" (không xử lý chuỗi verbatim/raw — mã không dùng chúng cho Loc).
CS_STRING = r'"((?:[^"\\]|\\.)*)"'
PATTERNS = [
    re.compile(r'\bLoc\.(?:T|F)\(\s*' + CS_STRING),
    re.compile(r'\bLocalizedText\.Source\(\s*' + CS_STRING),
]
XAML_PATTERN = re.compile(r"\{ui:L\s+'([^']*)'\}")
SPEC = re.compile(r"%(?:\d+\$)?(?:\.\d+)?(?:ll|l)?[@difsu]")


def unescape(s: str) -> str:
    return (s.replace(r'\"', '"').replace(r"\n", "\n").replace(r"\t", "\t").replace(r"\\", "\\"))


def contains_vietnamese(s: str) -> bool:
    return bool(re.search(r"[À-ỹĐđ]", s)) or " " in s


def collect_keys() -> dict:
    keys = {}
    for path in SRC.rglob("*.cs"):
        if "obj" in path.parts or "bin" in path.parts:
            continue
        text = path.read_text(encoding="utf-8")
        for pattern in PATTERNS:
            for m in pattern.finditer(text):
                key = unescape(m.group(1))
                keys.setdefault(key, str(path.relative_to(ROOT)))
    for path in SRC.rglob("*.xaml"):
        text = path.read_text(encoding="utf-8")
        for m in XAML_PATTERN.finditer(text):
            keys.setdefault(m.group(1), str(path.relative_to(ROOT)))
    return keys


def load_translations() -> dict:
    merged = {}
    files = sorted(LOC_DIR.glob("en*.json"), key=lambda p: (p.name != "en.json", p.name))
    for f in files:
        try:
            data = json.loads(f.read_text(encoding="utf-8"))
        except json.JSONDecodeError as e:
            print(f"lỗi: {f.name}: JSON sai: {e}")
            sys.exit(2)
        for k, v in data.items():
            if v:
                merged[k] = v
    return merged


def main() -> int:
    keys = collect_keys()
    translations = load_translations()
    missing = {k: src for k, src in keys.items() if k not in translations and contains_vietnamese(k)}
    bad_format = []
    for k in keys:
        v = translations.get(k)
        if v is None:
            continue
        if sorted(SPEC.findall(k)) != sorted(SPEC.findall(v)):
            bad_format.append((k, v))
    for k, src in sorted(missing.items(), key=lambda kv: kv[1]):
        print(f"thiếu: {src}: {k!r}")
    for k, v in bad_format:
        print(f"định dạng lệch: {k!r} → {v!r}")
    print(f"{len(keys)} khoá trong mã, {len(missing)} thiếu bản dịch, {len(bad_format)} lệch định dạng")
    if "--write" in sys.argv:
        out = Path(sys.argv[sys.argv.index("--write") + 1])
        existing = json.loads(out.read_text(encoding="utf-8")) if out.exists() else {}
        for k in missing:
            existing.setdefault(k, "")
        out.write_text(json.dumps(existing, ensure_ascii=False, indent=2, sort_keys=True) + "\n", encoding="utf-8")
        print(f"đã ghi {out}")
    return 1 if missing or bad_format else 0


if __name__ == "__main__":
    sys.exit(main())
