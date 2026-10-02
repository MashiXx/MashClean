#!/usr/bin/env python3
"""Sinh Localization/{en,vi}.lproj/Localizable.strings từ Localization/en.json (khoá là chuỗi tiếng Việt gốc)."""
import json
import re
from pathlib import Path

root = Path(__file__).resolve().parents[2] / "Localization"
SPEC = re.compile(r"%(?:\d+\$)?(lld|ld|llu|lu|d|@|lf|f|%)")


def esc(s):
    return s.replace("\\", "\\\\").replace('"', '\\"').replace("\n", "\\n").replace("\t", "\\t")


def write(lang, pairs):
    d = root / f"{lang}.lproj"
    d.mkdir(parents=True, exist_ok=True)
    lines = ["/* Sinh tự động bởi Scripts/l10n/gen_strings.py từ Localization/en.json. Không sửa tay. */", ""]
    lines += [f'"{esc(k)}" = "{esc(v)}";' for k, v in pairs]
    (d / "Localizable.strings").write_text("\n".join(lines) + "\n", encoding="utf-8")


table = json.load(open(root / "en.json"))
VI_OVERRIDES = {"action.uninstall": "Gỡ cài đặt"}
bad = [k for k, v in table.items() if v and sorted(SPEC.findall(k)) != sorted(SPEC.findall(v))]
if bad:
    raise SystemExit("Format specifier không khớp:\n" + "\n".join(repr(k) for k in bad))
untranslated = [k for k, v in table.items() if not v]
write("en", [(k, v or k) for k, v in sorted(table.items())])
# Khoá dạng "action.*" (khi một chuỗi tiếng Việt cần hai bản dịch khác nhau) mang giá trị tiếng Việt trong VI_OVERRIDES.
write("vi", [(k, VI_OVERRIDES.get(k, k)) for k in sorted(table)])
print(f"Đã sinh {len(table)} chuỗi (en, vi); {len(untranslated)} chuỗi chưa dịch giữ nguyên tiếng Việt")
