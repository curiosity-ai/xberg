#!/usr/bin/env python3
"""Time ONNX Runtime and the C# runtime back to back on the same inputs.

Comparing against a number recorded in an earlier session is unsound here: the
calibration probe shows this VM's peak FMA throughput swinging by more than 2x
between runs depending on what else shares the host. A ratio is only meaningful
if both sides are measured minutes apart on the same machine state, which is
what this script arranges — and it re-runs the C# calibration afterwards so a
mid-run host change is visible rather than silently folded into the result.

Usage::

    compare_speed.py MODEL.onnx INPUT_DIR [--runs 5] [--repo /path/to/dotnet]
"""

from __future__ import annotations

import argparse
import statistics
import subprocess
import sys
import time
from pathlib import Path

import numpy as np
import onnxruntime as ort

WARMUP_RUNS = 3


def time_ort(model: Path, feeds: dict[str, np.ndarray], runs: int, optimized: bool) -> list[float]:
    options = ort.SessionOptions()
    if not optimized:
        options.graph_optimization_level = ort.GraphOptimizationLevel.ORT_DISABLE_ALL
    session = ort.InferenceSession(str(model), options, providers=["CPUExecutionProvider"])

    for _ in range(WARMUP_RUNS):
        session.run(None, feeds)

    timings = []
    for _ in range(runs):
        start = time.perf_counter()
        session.run(None, feeds)
        timings.append((time.perf_counter() - start) * 1000)
    return sorted(timings)


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("model", type=Path)
    ap.add_argument("input_dir", type=Path, help="directory holding images.npy and orig_target_sizes.npy")
    ap.add_argument("--reference", type=Path, help="reference dump directory for the C# harness")
    ap.add_argument("--runs", type=int, default=5)
    ap.add_argument("--repo", type=Path, default=Path(__file__).resolve().parents[2])
    args = ap.parse_args()

    feeds = {
        "images": np.load(args.input_dir / "images.npy"),
        "orig_target_sizes": np.load(args.input_dir / "orig_target_sizes.npy"),
    }

    print(f"onnxruntime {ort.__version__}, {ort.get_available_providers()}")
    optimized = time_ort(args.model, feeds, args.runs, optimized=True)
    unfused = time_ort(args.model, feeds, args.runs, optimized=False)
    median_ort = statistics.median(optimized)

    print()
    print(f"ONNX Runtime (graph optimisations on) : median {median_ort:7.0f} ms  best {optimized[0]:7.0f} ms")
    print(f"ONNX Runtime (optimisations disabled) : median {statistics.median(unfused):7.0f} ms  "
          f"best {unfused[0]:7.0f} ms")
    print()
    print("--- C# runtime, measured immediately after ---")
    sys.stdout.flush()

    command = [
        "dotnet", "run", "--project", "tools/Xberg.OnnxParity", "-c", "Release", "--no-build", "--",
        "--model", str(args.model), "--benchmark", str(args.runs),
    ]
    if args.reference:
        command += ["--reference", str(args.reference)]
    result = subprocess.run(command, cwd=args.repo, capture_output=True, text=True)
    print(result.stdout)
    if result.returncode != 0:
        print(result.stderr, file=sys.stderr)
        return result.returncode

    for line in result.stdout.splitlines():
        if "inference over" in line:
            median_cs = float(line.split("median")[1].split("ms")[0].strip())
            print(f"ratio: C# is {median_cs / median_ort:.2f}x ONNX Runtime's median "
                  f"({median_cs:.0f} ms vs {median_ort:.0f} ms)")
            break
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
