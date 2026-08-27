#!/usr/bin/env bash
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"
TEMP_ROOT="$(mktemp -d)"
CACHE_DIR="$TEMP_ROOT/vendor-cache"
BUILD_LOG="$TEMP_ROOT/build.log"
READABLE_PERMISSIONS="700"
BLOCKED_PERMISSIONS="000"
FAILURE_LOG_LINES="200"

cleanup() {
  chmod "$READABLE_PERMISSIONS" "$CACHE_DIR" 2>/dev/null || true
  rm -rf "$TEMP_ROOT"
}
trap cleanup EXIT

mkdir -p "$CACHE_DIR"
chmod "$BLOCKED_PERMISSIONS" "$CACHE_DIR"

cd "$REPO_ROOT"
if ! CARGO_TARGET_DIR="$TEMP_ROOT/target" \
  TESSERACT_RS_CACHE_DIR="$CACHE_DIR" \
  cargo test --locked -p xberg --no-default-features \
  --features "ocr,tesseract-dynamic" --test ocr_auto_rotate --no-run -vv >"$BUILD_LOG" 2>&1; then
  tail -n "$FAILURE_LOG_LINES" "$BUILD_LOG"
  exit 1
fi

if ! grep -Fq "Using dynamic linking with system-installed Tesseract libraries" "$BUILD_LOG"; then
  echo "::error::The dynamic Tesseract build path was not selected"
  exit 1
fi

if grep -Eq 'custom_out_dir:|Downloading .* from https?://' "$BUILD_LOG"; then
  echo "::error::The dynamic Tesseract build unexpectedly entered the vendored build path"
  exit 1
fi

chmod "$READABLE_PERMISSIONS" "$CACHE_DIR"
if find "$CACHE_DIR" -mindepth 1 -print -quit | grep -q .; then
  echo "::error::The dynamic Tesseract build wrote to the vendor cache"
  exit 1
fi

echo "Verified host Tesseract dynamic linking without vendor-cache access"
