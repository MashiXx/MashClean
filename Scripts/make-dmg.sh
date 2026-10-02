#!/bin/bash
# Đóng gói DMG có lối tắt Applications để kéo thả (mục 19 bước 5).
# Dùng: Scripts/make-dmg.sh path/to/Clean Boost.app [out.dmg]
set -euo pipefail
APP="$1"
OUT="${2:-CleanBoost.dmg}"
STAGE="$(mktemp -d)"
trap 'rm -rf "$STAGE"' EXIT
cp -R "$APP" "$STAGE/"
ln -s /Applications "$STAGE/Applications"
rm -f "$OUT"
hdiutil create -volname "Clean Boost" -srcfolder "$STAGE" -ov -format UDZO "$OUT"
if [ -n "${SIGN_IDENTITY:-}" ]; then codesign --sign "$SIGN_IDENTITY" --timestamp "$OUT"; fi
echo "==> $OUT"
