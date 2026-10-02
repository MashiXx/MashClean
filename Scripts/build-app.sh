#!/bin/bash
# Build MashClean.app (mục 19): rule → sinh Xcode project → xcodebuild.
# Dùng: Scripts/build-app.sh [Debug|Release]
set -euo pipefail
cd "$(dirname "$0")/.."
CONFIG="${1:-Debug}"
DERIVED="${DERIVED_DATA:-$PWD/.build/DerivedData}"

if [ ! -f App/Resources/Rules/rules.bundle ]; then
    echo "==> Đóng gói rule"
    Scripts/build-rules.sh
fi

echo "==> Sinh Xcode project (XcodeGen)"
command -v xcodegen >/dev/null || { echo "Cần cài XcodeGen: brew install xcodegen"; exit 1; }
xcodegen generate --quiet

echo "==> Build $CONFIG"
ARGS=(-project MashClean.xcodeproj -scheme MashClean -configuration "$CONFIG" -derivedDataPath "$DERIVED" -destination 'generic/platform=macOS')
if command -v xcbeautify >/dev/null; then
    xcodebuild "${ARGS[@]}" build | xcbeautify
else
    xcodebuild "${ARGS[@]}" build -quiet
fi

APP="$DERIVED/Build/Products/$CONFIG/MashClean.app"
echo "==> Xong: $APP"
