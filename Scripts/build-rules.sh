#!/bin/bash
# Đóng gói bộ rule (mục 8.5, 23.8): lint → test → build → sign → verify.
# Kết quả: App/Resources/Rules/rules.bundle
#
# Biến môi trường:
#   RULES_SIGNING_KEY_FILE  file base64 khoá riêng Ed25519 (mặc định Secrets/rules_signing_key.b64; CI dùng secret)
#   RULES_VERSION           ghi đè version.txt (yyyymmddNN)
#   RULES_MIN_APP           phiên bản app tối thiểu (mặc định 1.0.0)
#   SCRATCH_PATH            thư mục build của SwiftPM (mặc định Packages/.build)
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
RULES_DIR="$ROOT/Rules"
FIXTURES_DIR="$ROOT/Tests/Fixtures/rules"
OUT_DIR="$ROOT/App/Resources/Rules"
KEY_FILE="${RULES_SIGNING_KEY_FILE:-$ROOT/Secrets/rules_signing_key.b64}"
MIN_APP="${RULES_MIN_APP:-1.0.0}"
SCRATCH="${SCRATCH_PATH:-$ROOT/Packages/.build}"

echo "==> Build rulepack"
swift build --package-path "$ROOT/Packages" --scratch-path "$SCRATCH" --product rulepack -c release >/dev/null
RULEPACK="$(swift build --package-path "$ROOT/Packages" --scratch-path "$SCRATCH" -c release --show-bin-path)/rulepack"

WORK="$(mktemp -d "${TMPDIR:-/tmp}/mashclean-rules.XXXXXX")"
trap 'rm -rf "$WORK"' EXIT

echo "==> Lint"
"$RULEPACK" lint "$RULES_DIR"

echo "==> Test trên fixture"
"$RULEPACK" test "$RULES_DIR" --fixtures "$FIXTURES_DIR"

echo "==> Build"
BUILD_ARGS=(build "$RULES_DIR" --out "$WORK/rules.unsigned" --min-app "$MIN_APP")
if [[ -n "${RULES_VERSION:-}" ]]; then BUILD_ARGS+=(--version "$RULES_VERSION"); fi
"$RULEPACK" "${BUILD_ARGS[@]}"

echo "==> Sign"
if [[ ! -f "$KEY_FILE" ]]; then
    echo "Không tìm thấy khoá ký: $KEY_FILE" >&2
    exit 1
fi
"$RULEPACK" sign "$WORK/rules.unsigned" --key "$KEY_FILE" --out "$WORK/rules.bundle"

echo "==> Verify bằng public key nhúng trong app"
"$RULEPACK" verify "$WORK/rules.bundle"

mkdir -p "$OUT_DIR"
cp "$WORK/rules.bundle" "$OUT_DIR/rules.bundle"
echo "==> Xong: $OUT_DIR/rules.bundle"
