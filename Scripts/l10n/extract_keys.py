#!/usr/bin/env python3
"""Trích mọi chuỗi bản địa hoá từ file .stringsdata do trình biên dịch sinh (SWIFT_EMIT_LOC_STRINGS=YES).

Dùng: Scripts/l10n/update.sh (build sạch rồi gọi script này).
      python3 Scripts/l10n/extract_keys.py <DerivedData> Localization/en.json
Chuỗi mới được thêm vào en.json với giá trị rỗng để dịch; chuỗi không còn dùng bị gỡ.
"""
import glob
import json
import sys

derived, out = sys.argv[1], sys.argv[2]
keys = set()
for f in glob.glob(derived + "/**/*.stringsdata", recursive=True):
    data = json.load(open(f))
    for entries in data.get("tables", {}).values():
        keys.update(e["key"] for e in entries)
keys.discard("")

try:
    existing = json.load(open(out))
except FileNotFoundError:
    existing = {}
merged = {k: existing.get(k, "") for k in sorted(keys)}
missing = [k for k, v in merged.items() if not v]
removed = [k for k in existing if k not in keys]
json.dump(merged, open(out, "w"), ensure_ascii=False, indent=2, sort_keys=True)
print(f"{len(merged)} chuỗi, {len(missing)} chưa dịch, gỡ {len(removed)} chuỗi không còn dùng")
for k in missing[:50]:
    print("  chưa dịch:", repr(k))
