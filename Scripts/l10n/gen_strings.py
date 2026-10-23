#!/usr/bin/env python3
"""Sinh Localization/{en,vi}.lproj/*.strings từ các bảng nguồn trong Localization/.

- en.json       → Localizable.strings (khoá là chuỗi tiếng Việt gốc)
- infoplist.json → InfoPlist.strings   (khoá là khoá Info.plist; mỗi khoá có sẵn bản en và vi)

Cụm nói App Shortcuts nằm ở bảng riêng `AppShortcuts`, viết tay trong Extensions/AppIntents/{en,vi}.lproj/.
"""
import json
import re
from pathlib import Path

root = Path(__file__).resolve().parents[2] / "Localization"
SPEC = re.compile(r"%(?:\d+\$)?(lld|ld|llu|lu|d|@|lf|f|%)")
HEADER = "/* Sinh tự động bởi Scripts/l10n/gen_strings.py từ Localization/{src}. Không sửa tay. */"


def esc(s):
    return s.replace("\\", "\\\\").replace('"', '\\"').replace("\n", "\\n").replace("\t", "\\t")


def write(lang, name, src, pairs):
    d = root / f"{lang}.lproj"
    d.mkdir(parents=True, exist_ok=True)
    lines = [HEADER.format(src=src), ""]
    lines += [f'"{esc(k)}" = "{esc(v)}";' for k, v in pairs]
    (d / name).write_text("\n".join(lines) + "\n", encoding="utf-8")


def check_specs(table, src):
    bad = [k for k, v in table.items() if v and sorted(SPEC.findall(k)) != sorted(SPEC.findall(v))]
    if bad:
        raise SystemExit(f"Format specifier không khớp trong {src}:\n" + "\n".join(repr(k) for k in bad))


def load(name):
    path = root / name
    return json.load(open(path, encoding="utf-8")) if path.exists() else {}


# Bảng khoá-tiếng-Việt: bản vi dùng chính khoá, bản en dùng giá trị đã dịch.
# Khoá dạng "action.*" (khi một chuỗi tiếng Việt cần hai bản dịch khác nhau) mang giá trị tiếng Việt trong VI_OVERRIDES.
VI_OVERRIDES = {"action.uninstall": "Gỡ cài đặt"}


def gen_vi_keyed(src, out):
    table = load(src)
    if not table:
        return 0
    check_specs(table, src)
    write("en", out, src, [(k, v or k) for k, v in sorted(table.items())])
    write("vi", out, src, [(k, VI_OVERRIDES.get(k, k)) for k in sorted(table)])
    return table


main = gen_vi_keyed("en.json", "Localizable.strings")

# InfoPlist: khoá là khoá Info.plist nên mỗi ngôn ngữ lấy giá trị riêng.
info = load("infoplist.json")
if info:
    missing = [k for k, v in info.items() if not v.get("en") or not v.get("vi")]
    if missing:
        raise SystemExit("infoplist.json thiếu bản dịch: " + ", ".join(missing))
    for lang in ("en", "vi"):
        write(lang, "InfoPlist.strings", "infoplist.json", [(k, info[k][lang]) for k in sorted(info)])

untranslated = len([k for k, v in main.items() if not v])
print(f"Đã sinh {len(main)} chuỗi giao diện và {len(info)} khoá Info.plist (en, vi);"
      f" {untranslated} chuỗi chưa dịch giữ nguyên tiếng Việt")
