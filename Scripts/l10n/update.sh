#!/bin/bash
# Cập nhật bản địa hoá: build sạch để trình biên dịch xuất chuỗi → trích khoá vào Localization/en.json → sinh .strings.
# Sau khi chạy, dịch các chuỗi còn rỗng trong Localization/en.json rồi chạy lại gen_strings.py.
set -euo pipefail
cd "$(dirname "$0")/../.."
DD="$(mktemp -d)"
trap 'rm -rf "$DD"' EXIT
xcodegen generate --quiet
xcodebuild -project MashClean.xcodeproj -scheme MashClean -configuration Debug -derivedDataPath "$DD" \
    -destination 'generic/platform=macOS' build SWIFT_EMIT_LOC_STRINGS=YES -quiet
python3 Scripts/l10n/extract_keys.py "$DD" Localization/en.json
python3 Scripts/l10n/gen_strings.py
