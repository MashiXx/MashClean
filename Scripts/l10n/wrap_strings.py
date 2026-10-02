#!/usr/bin/env python3
"""Bọc chuỗi giao diện tiếng Việt trong `String(localized: ...)` để bản địa hoá được.

Dùng: python3 Scripts/l10n/wrap_strings.py <thư mục>...   (chạy lại nhiều lần không bọc trùng)

Bỏ qua: comment, chuỗi nhiều dòng, raw string, raw value của `case x = "..."`, pattern `case "..."`,
từ điển song ngữ `"vi": "..."`, và dòng ghi log.
"""
import re
import sys
from pathlib import Path

VIET = re.compile(r"[À-ỹ]")
SKIP_LINE = re.compile(r"\bLog\.|\bLogger\.|\bprint\(|\.(info|debug|notice|warning|error|fault)\(\"")
RAW_VALUE = re.compile(r"\bcase\s+\w+\s*=\s*$")
CASE_PATTERN = re.compile(r"(^|[\s,])case\s+$")
LANG_KEY = re.compile(r"\"(vi|en)\"\s*:\s*$")
WRAPPED = re.compile(r"String\(localized:\s*$")
STATIC_RESOURCE = re.compile(r"LocalizedStringResource\s*=\s*$")


def scan(src):
    """Trả về danh sách (start, end, text_parts_have_viet, multiline, raw) của mọi string literal."""
    spans = []
    i, n = 0, len(src)
    # Ngăn xếp: mỗi phần tử là ("code", paren_depth) hoặc ("str", start, multiline, raw_hashes, viet_flag_list)
    stack = [["code", 0]]
    while i < n:
        top = stack[-1]
        c = src[i]
        if top[0] == "code":
            if src.startswith("//", i):
                j = src.find("\n", i)
                i = n if j < 0 else j
                continue
            if src.startswith("/*", i):
                depth, i = 1, i + 2
                while i < n and depth:
                    if src.startswith("/*", i):
                        depth += 1; i += 2
                    elif src.startswith("*/", i):
                        depth -= 1; i += 2
                    else:
                        i += 1
                continue
            m = re.match(r'(#*)("""|")', src[i:])
            if m and (m.group(1) == "" or True):
                hashes = len(m.group(1))
                multiline = m.group(2) == '"""'
                if hashes == 0 or src[i] == "#":
                    start = i
                    i += hashes + len(m.group(2))
                    stack.append(["str", start, multiline, hashes, [False]])
                    continue
            if c == "(":
                top[1] += 1
            elif c == ")":
                if top[1] == 0 and len(stack) > 1:
                    stack.pop()  # kết thúc interpolation, quay về chuỗi
                    i += 1
                    continue
                top[1] -= 1
            i += 1
        else:
            _, start, multiline, hashes, viet = top
            close = ('"""' if multiline else '"') + "#" * hashes
            esc = "\\" + "#" * hashes
            if src.startswith(esc + "(", i):
                i += len(esc) + 1
                stack.append(["code", 0])
                continue
            if src.startswith(esc, i):
                i += len(esc) + 1
                continue
            if src.startswith(close, i):
                i += len(close)
                stack.pop()
                spans.append((start, i, viet[0], multiline, hashes > 0))
                continue
            if VIET.match(c):
                viet[0] = True
            i += 1
    return spans


def transform(src):
    spans = scan(src)
    inserts = []  # (vị trí trong chuỗi gốc, văn bản chèn)
    for start, end, has_viet, multiline, raw in spans:
        if not has_viet or multiline or raw:
            continue
        line_start = src.rfind("\n", 0, start) + 1
        line_end = src.find("\n", end)
        line = src[line_start:line_end if line_end >= 0 else len(src)]
        before = src[line_start:start]
        if line.lstrip().startswith("//") or SKIP_LINE.search(line):
            continue
        if RAW_VALUE.search(before) or CASE_PATTERN.search(before) or LANG_KEY.search(before):
            continue
        if WRAPPED.search(before) or STATIC_RESOURCE.search(before):
            continue
        inserts.append((start, 0, "String(localized: "))
        inserts.append((end, 1, ")"))
    # Chèn từ cuối lên đầu theo vị trí gốc, nên chuỗi lồng trong \(...) không làm lệch chuỗi bọc ngoài.
    # Cùng vị trí: ")" của chuỗi trong đứng trước "String(localized: " của chuỗi kế tiếp.
    out = src
    for pos, order, text in sorted(inserts, key=lambda x: (x[0], x[1]), reverse=True):
        out = out[:pos] + text + out[pos:]
    return out, len(inserts) // 2


def main():
    total = 0
    for root in sys.argv[1:]:
        for path in sorted(Path(root).rglob("*.swift")):
            src = path.read_text()
            new, changed = transform(src)
            if changed:
                path.write_text(new)
                total += changed
                print(f"{changed:4d}  {path}")
    print(f"Tổng: {total} chuỗi")


if __name__ == "__main__":
    main()
