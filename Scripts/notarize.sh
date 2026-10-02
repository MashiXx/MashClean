#!/bin/bash
# Notarize + staple (mục 19 bước 4). Cần ký bằng Developer ID trước.
# Dùng: Scripts/notarize.sh path/to/MashClean.dmg  (profile notarytool tạo bằng `xcrun notarytool store-credentials mashclean`)
set -euo pipefail
TARGET="$1"
PROFILE="${NOTARY_PROFILE:-mashclean}"
xcrun notarytool submit "$TARGET" --keychain-profile "$PROFILE" --wait
xcrun stapler staple "$TARGET"
xcrun stapler validate "$TARGET"
