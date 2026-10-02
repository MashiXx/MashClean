#!/bin/bash
# Chạy thử app vài giây để bắt lỗi chết ngay khi mở (vd dyld từ chối nạp framework vì chữ ký).
# Dùng: Scripts/smoke-launch.sh path/to/MashClean.app
set -euo pipefail
APP="$1"
BIN="$APP/Contents/MacOS/MashClean"
LOG="$(mktemp)"
trap 'rm -f "$LOG"' EXIT
MASHCLEAN_SMOKE_TEST=1 MASHCLEAN_DRY_RUN=1 "$BIN" >"$LOG" 2>&1 &
PID=$!
sleep 4
if kill -0 "$PID" 2>/dev/null; then
    kill "$PID"
    wait "$PID" 2>/dev/null || true
    echo "==> Khởi động thử: OK"
else
    echo "==> Khởi động thử: app chết ngay khi mở" >&2
    grep -E "Library not loaded|Reason:|Termination|error" "$LOG" | head -5 >&2 || cat "$LOG" | head -20 >&2
    exit 1
fi
