#!/usr/bin/env bash
# One-shot Rust-vs-C# extraction benchmark. Everything runs inside this script so the whole
# measurement happens in a single process tree rather than being split across invocations.
#
#   bench.sh [--root DIR] [--iters N] [--warmup N] [--ext EXT] [--outdir DIR]
set -uo pipefail

ROOT=/home/user/xberg/test_documents
ITERS=5
WARMUP=2
EXT=""
OUTDIR=/tmp/xberg-bench
REPO=/home/user/xberg

while [ $# -gt 0 ]; do
  case "$1" in
    --root) ROOT="$2"; shift 2;;
    --iters) ITERS="$2"; shift 2;;
    --warmup) WARMUP="$2"; shift 2;;
    --ext) EXT="$2"; shift 2;;
    --outdir) OUTDIR="$2"; shift 2;;
    *) echo "unknown arg: $1" >&2; exit 2;;
  esac
done

mkdir -p "$OUTDIR"
EXTARG=""
[ -n "$EXT" ] && EXTARG="--ext $EXT"

echo "=== machine ==="
{
  echo "date        : $(date -u +%FT%TZ)"
  echo "kernel      : $(uname -sr)"
  echo "cpu         : $(grep -m1 'model name' /proc/cpuinfo | cut -d: -f2 | sed 's/^ //')"
  echo "cores       : $(nproc)"
  echo "mem         : $(free -g | awk '/^Mem:/{print $2" GiB"}')"
  echo "dotnet      : $(dotnet --version 2>/dev/null)"
  echo "rustc       : $(rustc --version 2>/dev/null)"
} | tee "$OUTDIR/machine.txt"

echo
echo "=== building rust benchmark (release, lto=thin) ==="
( cd "$REPO/dotnet/tools/xberg-bench" && cargo build --release 2>&1 | tail -3 ) || { echo "rust build FAILED"; exit 1; }
RS_BIN="$REPO/dotnet/tools/xberg-bench/target/release/xberg-bench"
[ -x "$RS_BIN" ] || { echo "missing $RS_BIN"; exit 1; }

echo
echo "=== building c# benchmark (Release) ==="
( cd "$REPO/dotnet" && dotnet build tools/Xberg.Bench -c Release 2>&1 | grep -E "error|Error\(s\)" | head -3 ) || { echo "c# build FAILED"; exit 1; }
CS_DLL="$REPO/dotnet/tools/Xberg.Bench/bin/Release/net10.0/xberg-bench.dll"
[ -f "$CS_DLL" ] || { echo "missing $CS_DLL"; exit 1; }

echo
echo "=== running rust ($ITERS iters, $WARMUP warmup passes) ==="
"$RS_BIN" "$ROOT" --iters "$ITERS" --warmup "$WARMUP" $EXTARG --out "$OUTDIR/rust.tsv"

echo
echo "=== running c# ($ITERS iters, $WARMUP warmup passes) ==="
DOTNET_TieredPGO=1 dotnet "$CS_DLL" "$ROOT" --iters "$ITERS" --warmup "$WARMUP" $EXTARG --out "$OUTDIR/cs.tsv"

echo
echo "=== analysis ==="
python3 - "$OUTDIR" <<'PYEOF'
import sys, csv, statistics as st
from collections import defaultdict

outdir = sys.argv[1]

def load(p):
    d = {}
    with open(p) as f:
        for row in csv.DictReader(f, delimiter='\t'):
            d[row['rel']] = (row['ext'], int(row['bytes']), int(row['ok']),
                             float(row['min_ms']), float(row['median_ms']))
    return d

rs, cs = load(f'{outdir}/rust.tsv'), load(f'{outdir}/cs.tsv')
common = sorted(set(rs) & set(cs))

# Compare only files both sides actually extracted. A file one side refuses and the other
# parses is a correctness difference, not a speed one, and averaging it into a ratio would
# quietly reward whichever side did less work.
both_ok = [r for r in common if rs[r][2] == 1 and cs[r][2] == 1]
only_rs = [r for r in common if rs[r][2] == 1 and cs[r][2] == 0]
only_cs = [r for r in common if rs[r][2] == 0 and cs[r][2] == 1]

def pct(xs, q):
    xs = sorted(xs)
    if not xs: return 0.0
    k = (len(xs) - 1) * q
    lo, hi = int(k), min(int(k) + 1, len(xs) - 1)
    return xs[lo] + (xs[hi] - xs[lo]) * (k - lo)

rmin = [rs[r][3] for r in both_ok]
cmin = [cs[r][3] for r in both_ok]
ratios = [cs[r][3] / rs[r][3] for r in both_ok if rs[r][3] > 0]

lines = []
lines.append(f"files compared (both extracted) : {len(both_ok)}")
lines.append(f"rust-only success              : {len(only_rs)}")
lines.append(f"c#-only success                : {len(only_cs)}")
lines.append("")
lines.append("| metric | Rust | C# | C# / Rust |")
lines.append("|---|---:|---:|---:|")
def row(name, f):
    a, b = f(rmin), f(cmin)
    lines.append(f"| {name} | {a:,.3f} ms | {b:,.3f} ms | {b/a:.2f}x |" if a else f"| {name} | - | - | - |")
row("total",  lambda x: sum(x))
row("mean",   lambda x: st.mean(x))
row("median", lambda x: st.median(x))
row("p90",    lambda x: pct(x, 0.90))
row("p99",    lambda x: pct(x, 0.99))
row("max",    lambda x: max(x))
lines.append("")
lines.append(f"per-file ratio (C#/Rust): median {st.median(ratios):.2f}x, "
             f"geomean {st.geometric_mean(ratios):.2f}x, "
             f"p10 {pct(ratios,0.10):.2f}x, p90 {pct(ratios,0.90):.2f}x")
faster = sum(1 for x in ratios if x < 1.0)
lines.append(f"C# faster on {faster}/{len(ratios)} files ({100*faster/len(ratios):.1f}%)")
lines.append("")

by_ext = defaultdict(list)
for r in both_ok:
    by_ext[rs[r][0]].append((rs[r][3], cs[r][3]))
lines.append("| ext | n | Rust total | C# total | Rust median | C# median | C# / Rust (median) |")
lines.append("|---|---:|---:|---:|---:|---:|---:|")
for ext, vals in sorted(by_ext.items(), key=lambda kv: -sum(v[0] for v in kv[1]))[:24]:
    rv = [v[0] for v in vals]; cv = [v[1] for v in vals]
    lines.append(f"| {ext or '(none)'} | {len(vals)} | {sum(rv):,.1f} ms | {sum(cv):,.1f} ms | "
                 f"{st.median(rv):.3f} ms | {st.median(cv):.3f} ms | {st.median(cv)/st.median(rv):.2f}x |")

summary = "\n".join(lines)
print(summary)
open(f'{outdir}/summary.md','w').write(summary + "\n")

with open(f'{outdir}/per-file.tsv','w') as f:
    f.write("rel\text\tbytes\trust_min_ms\tcs_min_ms\tratio\trust_median_ms\tcs_median_ms\n")
    for r in sorted(both_ok, key=lambda r: -cs[r][3]):
        e,b,_,rm,rmd = rs[r]; _,_,_,cm,cmd = cs[r]
        f.write(f"{r}\t{e}\t{b}\t{rm:.4f}\t{cm:.4f}\t{(cm/rm if rm else 0):.3f}\t{rmd:.4f}\t{cmd:.4f}\n")

print(f"\nper-file results: {outdir}/per-file.tsv ({len(both_ok)} rows)")
print(f"summary        : {outdir}/summary.md")
PYEOF
