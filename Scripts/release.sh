#!/bin/bash
# Chuỗi phát hành đầy đủ (mục 19 bước 6): build Release → kiểm tra chữ ký → DMG → notarize → appcast Sparkle.
set -euo pipefail
cd "$(dirname "$0")/.."
Scripts/build-rules.sh
Scripts/build-app.sh Release
APP=".build/DerivedData/Build/Products/Release/MashClean.app"
codesign --verify --deep --strict --verbose=2 "$APP"
spctl --assess --type execute --verbose "$APP" || echo "Gatekeeper chưa chấp nhận (cần Developer ID + notarize)"
VERSION=$(/usr/libexec/PlistBuddy -c "Print CFBundleShortVersionString" "$APP/Contents/Info.plist")
mkdir -p dist
Scripts/make-dmg.sh "$APP" "dist/MashClean-$VERSION.dmg"
Scripts/notarize.sh "dist/MashClean-$VERSION.dmg"
if command -v generate_appcast >/dev/null; then generate_appcast dist; else echo "Bỏ qua appcast: chưa có generate_appcast của Sparkle"; fi
