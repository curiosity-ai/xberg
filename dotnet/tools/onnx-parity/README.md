# ONNX parity pipeline

Validates the pure-C# ONNX runtime under `src/Xberg/Internal/Onnx` against ONNX Runtime,
one graph value at a time.

## Why per-layer

The C# runtime parses the same `.onnx` file and executes the same graph with its own SIMD
kernels. A whole-model comparison can only say the final answer differs; it cannot say
which of 2676 nodes caused it, and every node after the first bad one is downstream noise.
So the Python side promotes **every intermediate value to a graph output** and dumps it,
and the C# side replays the graph capturing the same values. The first divergence in
topological order names the failing operator.

That still leaves one ambiguity: a node can diverge because its own kernel is wrong, or
because a correct kernel amplified rounding that arrived from upstream. `--isolate` settles
it by feeding each node the reference's *own* recorded inputs, so its output is judged
alone. A failure there is unambiguously a kernel bug.

## Setup

```bash
python3 -m venv mlenv
./mlenv/bin/pip install onnx onnxruntime numpy pillow
```

Models are pinned by revision and SHA-256 in `crates/xberg/src/layout/model_manager.rs`.
Fetch one directly:

```bash
curl -L -o rtdetr.onnx \
  "https://huggingface.co/xberg-io/layout-models/resolve/c6bf493e2f7b0b9a29a5870da9880c14e20ff0a3/rtdetr/model.onnx"
sha256sum rtdetr.onnx   # 3bf2fb0ee6df87435b7ae47f0f3930ec3dc97ec56fd824acc6d57bc7a6b89ef2
```

## Running

Build a realistic input. Random noise is a poor test: every detection scores about the
same, so rank order flips on float-level differences and tells you nothing. A page with
real structure produces well-separated scores.

```bash
./mlenv/bin/python make_page_input.py page --synthetic
```

Dump the reference:

```bash
./mlenv/bin/python dump_reference.py rtdetr.onnx ref/rtdetr --dim N=1 \
    --input images=page/images.npy \
    --input orig_target_sizes=page/orig_target_sizes.npy
```

Roughly 2.6 GB for RT-DETR at the default 4M-element cap — generated on demand, never
committed. Raise `--max-elements` to cover the large early feature maps too.

Compare:

```bash
cd ../../ && dotnet run --project tools/Xberg.OnnxParity -c Release -- \
    --model .../rtdetr.onnx --reference .../ref/rtdetr --limit 0 --detections 0.3
```

| flag | effect |
| --- | --- |
| `--isolate OP` | run every node of that operator against reference inputs |
| `--detections T` | decode both sides through RT-DETR postprocessing at threshold `T` |
| `--limit N` | detailed reports for the first `N` mismatches, one line each after |
| `--atol` / `--rtol` | tolerances; a value passes if within *either* |
| `--list-ops` | operator histogram for the model, no reference needed |

## Reading the results

Whole-graph agreement to the last bit is not achievable and not the goal. Both runtimes
compute in float32 but accumulate in different orders — a blocked SIMD `MultiplyAdd` does
not sum in the same sequence as ORT's kernels — so results differ in the last ulp or two
from the first convolution onward.

RT-DETR then amplifies that dramatically in one specific place. Its box head runs through
an inverse sigmoid: values clipped near the bounds are divided by a quantity close to zero,
turning a 1-ulp difference into an absolute difference of hundreds, before a `log` squashes
it back down. Intermediate values around 90,000 differing by 2% are that effect, not a bug.

What actually matters, in order:

1. **`--isolate` is clean for every operator.** This is the real correctness check.
2. **`--detections` agrees.** Same regions, classes, confidences and geometry.
3. Raw tensor divergence — informative for locating a bug, meaningless as a pass/fail bar.

Last measured on the synthetic page, RT-DETR (2676 nodes, opset 16):

- every operator instance matches under `--isolate` — 1600+ node outputs
- `scores` match to 1.1e-4; `labels` and `boxes` diverge only on sub-threshold queries
- all 10 detections above 0.3 agree in class, confidence and geometry

The PP-LCNet table classifier (282 nodes) matches at every single node in whole-graph mode,
largest absolute difference 9.8e-6 — it has no inverse sigmoid to amplify anything.

## Performance

### Measuring at all

Timing on this VM is unreliable in two specific ways, and both are handled before any number
is reported.

**The host changes underneath you.** Re-measuring the machine's peak fused-multiply-add
throughput between consecutive runs of the same binary has produced 100 and 218 GFLOP/s.
A 2x swing in the ceiling dwarfs most of what optimisation buys, so every timing run starts
by printing the CPU, core count and memory, then re-measuring peak FMA throughput (256- and
512-bit) and streaming memory bandwidth. If those numbers move between runs, the model
numbers from those runs are not comparable. For the same reason `compare_speed.py` runs ONNX
Runtime and this runtime back to back rather than comparing against a figure recorded
earlier.

**Cold code measures the compiler.** .NET starts methods in a quick-JIT tier and promotes
them only after roughly thirty calls, and this graph also has to fault in 169 MB of weights
and spin up the thread pool. Every benchmark discards three full inference passes first.

### Where it stands

One 640x640 page, 4 cores, both sides measured in the same session:

| | median |
| --- | --- |
| ONNX Runtime | ~0.69 s |
| this runtime | ~2.1 s |
| *ratio* | *~3x* |

Down from 10.5 s when this work started. The model is 118 GFLOP of convolution, so ORT runs
at roughly 170 GFLOP/s and this runtime at roughly 110, against a measured 512-bit ceiling
of 470-570 GFLOP/s.

### What actually moved it

Ordered by effect, and none of it was guessed — each came from the per-node profile:

- **Folding the decomposed batch normalisation** (36% → nothing). The export spells every
  batch norm as a per-channel `Mul` then a per-channel `Add`, each a full streaming pass over
  a multi-megabyte activation to apply a constant affine map. `GraphOptimizer` folds the
  chain into the convolution's weights and bias, and the following activation — `Relu`,
  `Sigmoid`, or the `Sigmoid`/`Mul` pair that spells SiLU — into the same output pass.
  193 nodes disappear.
- **Explicit `FusedMultiplyAdd`** (~1.5x on the multiply). The JIT does not contract
  `a * b + c` into an FMA, because that would change the rounding. A kernel written the
  natural way silently runs at a fraction of peak; the calibration reports both rates side by
  side so the gap stays visible.
- **Explicit `Vector512`** (~1.3x overall). `Vector512.IsHardwareAccelerated` reports
  `false` here and `Vector<float>.Count` stays at 8, but explicit `Vector512<float>` code
  still compiles to real AVX-512: 577 GFLOP/s against 161 in a pure FMA loop. Believing the
  flag would leave most of the machine unused.
- **Reference-based loads in the inner loop.** Eight `Span.Slice` calls per iteration were
  eight bounds checks; `Vector.LoadUnsafe` over a `ref float` removed them.
- **Cache panelling.** With the row loop outermost every row block re-streamed the whole
  right-hand operand — 26 MB pulled 64 times for one layer. Panels of depth and columns keep
  a slab resident while all rows consume it.
- **Pooled buffers.** Activations are recycled through a free list keyed by exact length,
  with reference counts on the storage rather than the tensor so that `Reshape` views and
  `Identity` aliases keep their memory alive. A standalone `Relu` over a 26 MB tensor fell
  from 163 ms to 44 ms.
- **Blocked transpose** (270 ms → 51 ms). A permutation of the last two axes was walking the
  source a cache line per element.
- **Vectorised `erf`** (50 ms → negligible). One GELU node was evaluating a 24-term Chebyshev
  fit in double precision, per element.

### What was tried and rejected

Recorded because the measurements are the useful part:

- **Constant folding of the shape arithmetic.** 1683 of 2676 nodes are scalar bookkeeping —
  and all of them together account for 2.3 ms of 8081 ms. It would shrink the node count and
  change nothing.
- **An eight-row register block.** AVX-512's 32 registers nominally hold sixteen
  accumulators plus operands, but the multiply got *slower*, 116 → 74 GFLOP/s.
- **Bigger convolution tiles.** Larger tiles re-read the weight matrix fewer times, but
  streaming a multi-megabyte unrolled buffer per tile cost more than the reuse saved.
- **`DOTNET_PreferredVectorBitWidth=512`.** Does not widen `Vector<T>` on this runtime;
  only explicit `Vector512` does.

### The remaining gap

Convolution is ~58% of runtime and runs at roughly the same rate as the bare multiply, so
im2col overhead is no longer material — the microkernel is the limit. Closing the rest means
what MLAS does: packing both operands into panels laid out exactly as the kernel walks them,
and per-CPU kernel selection. That is a substantial project against decades of tuning, not a
missing flag.

## Two bugs this caught

Worth recording, because neither would have been visible from the final output alone and
both are now regression-tested in `tests/Xberg.Tests/OnnxRuntimeTests.cs`.

**Softmax overflow.** `TensorPrimitives.SoftMax` exponentiates raw values without
subtracting the row maximum. RT-DETR's cross-attention produces logit rows around −164;
every `exp` underflowed to zero, the normalising sum was zero, and whole rows came back
NaN. The graph still ran to completion and produced plausible-looking numbers.

**Slice bound overflow.** Exporters spell "to the end of this axis" as `INT64_MAX`.
Narrowing the bounds tensor to `int` before clamping threw on the first decoder layer.
