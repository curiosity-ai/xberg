#!/usr/bin/env bash
# A/B the per-line dictionary-invalid filter by config flip alone -- one binary, two arms.
#
# The threshold `max_ocr_output_dict_invalid_line_ratio` defaults to 0.75 (enabled). Setting it
# to 1.01 puts it above the [0,1] range a ratio can reach, which disables it exactly the way the
# page-level veto is disabled by default. So the two arms differ only in configuration, and no
# rebuild is needed -- which also removes the "two binaries at the same crate version share cache
# entries" hazard entirely, because there is only one binary.
#
# Cache handling is the load-bearing part. The per-line ratios are computed INSIDE the OCR run
# (ocr/processor/execution.rs:843) and stored in result metadata; a cached result produced before
# the feature existed carries no such metadata, so the filter silently no-ops. Both the ocr and
# extraction caches are therefore removed before every leg. `--ocr-no-cache` is deliberately NOT
# used: per crates/xberg-cli/src/commands/overrides.rs:211-231 it only takes effect when
# ocr.tesseract_config is already set, so with no config file it silently does nothing.
#
# Usage: ab_line_filter.sh <pdf> <ground-truth.txt> <out-dir> [backend] [layout]
set -euo pipefail

PDF="${1:?usage: ab_line_filter.sh <pdf> <gt.txt> <out-dir> [backend] [layout]}"
GT="${2:?missing ground truth}"
OUT="${3:?missing out dir}"
BACKEND="${4:-tesseract}"
LAYOUT="${5:-true}"

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
BIN="$ROOT/target/release/xberg"
CACHE="${XBERG_CACHE_DIR:-$HOME/Library/Caches/xberg}"

[ -x "$BIN" ] || { echo "no binary at $BIN" >&2; exit 2; }
mkdir -p "$OUT"

echo "binary sha256: $(shasum -a 256 "$BIN" | cut -d' ' -f1)"
echo "document:      $(basename "$PDF")   backend=$BACKEND layout=$LAYOUT"

run_leg() {
  local name="$1" config_json="$2" dest="$3"
  # Remove rather than bypass: see the cache note in the header. A plain
  # `rm -rf` on these two directories is NOT safe here: the extraction cache holds
  # >13k entries and rm walks it for long enough to race whatever else touches the
  # cache, failing with ENOTEMPTY and -- under `set -e` -- killing the whole run
  # before either leg executed, while the wrapper still reported success. Renaming
  # first is atomic, so the leg always starts against an empty directory even if the
  # actual deletion is slow or partially fails. ~keep
  local stamp
  stamp="$(date +%s)-$$"
  local dir
  for dir in ocr extraction; do
    if [ -d "$CACHE/$dir" ]; then
      mv "$CACHE/$dir" "$CACHE/.$dir.discard.$stamp" 2>/dev/null || true
      rm -rf "$CACHE/.$dir.discard.$stamp" 2>/dev/null || true
    fi
  done
  local args=(
    extract "$PDF"
    --no-config-discovery
    --ocr true
    --ocr-backend "$BACKEND"
    --force-ocr true
    --content-format markdown
    --format text
  )
  [ "$LAYOUT" = "true" ] && args+=(--layout) || args+=(--layout false)
  [ -n "$config_json" ] && args+=(--config-json "$config_json")

  local status=0
  "$BIN" "${args[@]}" > "$dest" 2> "$dest.stderr" || status=$?
  echo "  leg $name: exit=$status bytes=$(wc -c < "$dest" | tr -d ' ')"
  if [ "$status" -ne 0 ]; then
    echo "  --- stderr (first 5 lines) ---"; head -5 "$dest.stderr" | sed 's/^/    /'
  fi
  return 0
}

# Arm A: shipped default, filter ENABLED at 0.75.
run_leg "A(filter ON, 0.75)" "" "$OUT/filter_on.md"

# Arm B: filter DISABLED via out-of-range threshold.
run_leg "B(filter OFF, 1.01)" \
  '{"ocr":{"quality_thresholds":{"max_ocr_output_dict_invalid_line_ratio":1.01}}}' \
  "$OUT/filter_off.md"

# A leg that produced nothing must fail the command. The first run of this harness
# aborted before either leg and still reported exit 0 -- exactly the "ran vacuously"
# failure this tool exists to detect, so it now refuses to report on absent output. ~keep
for leg in filter_on filter_off; do
  if [ ! -s "$OUT/$leg.md" ]; then
    echo "FATAL: $OUT/$leg.md is missing or empty -- no measurement was taken." >&2
    exit 3
  fi
done

echo
echo "=== byte-identical? (the whole question) ==="
if cmp -s "$OUT/filter_on.md" "$OUT/filter_off.md"; then
  echo "  IDENTICAL -- the filter changed nothing in the rendered output."
else
  echo "  DIFFER -- $(diff <(cat "$OUT/filter_on.md") <(cat "$OUT/filter_off.md") | grep -c '^[<>]') changed lines"
  diff "$OUT/filter_off.md" "$OUT/filter_on.md" | head -40
fi

echo
echo "=== scores ==="
for leg in filter_off filter_on; do
  python3 "$ROOT/tools/ocr-measure/score_gt_lines.py" "$OUT/$leg.md" "$GT" || true
done

echo
echo "=== pre-registered falsifier literals ==="
for leg in filter_off filter_on; do
  echo "-- $leg"
  for lit in "OWATS DNDEVET OPMENT" "LAAALDLI" "EXHIBIT B-1" "SITE PLAN" "LEGEND"; do
    if grep -qF "$lit" "$OUT/$leg.md" 2>/dev/null; then echo "   PRESENT: $lit"; else echo "   absent : $lit"; fi
  done
done
