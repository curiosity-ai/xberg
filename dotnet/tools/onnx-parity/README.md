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
| ONNX Runtime | ~0.51 s |
| this runtime | ~1.15 s |
| *ratio* | *~2.3x* |

Down from 10.5 s, i.e. 16x, when this work started. The model is 118 GFLOP of
convolution: ONNX Runtime runs it at roughly 230 GFLOP/s and this runtime at roughly 175,
against a measured 512-bit ceiling of 500-620 GFLOP/s.

### What actually moved it

Ordered by effect, and none of it was guessed — each came from the per-node profile, and the
matrix multiply's shape came from reading MLAS, ONNX Runtime's own kernel library.

- **Folding the decomposed batch normalisation** (36% → nothing). The export spells every
  batch norm as a per-channel `Mul` then a per-channel `Add`, each a full streaming pass over
  a multi-megabyte activation to apply a constant affine map. `GraphOptimizer` folds the
  chain into the convolution's weights and bias, and the following activation — `Relu`,
  `Sigmoid`, or the `Sigmoid`/`Mul` pair that spells SiLU — into the same output pass.
- **Packing the multiply's right-hand operand**, MLAS-style: a panel is copied into a buffer
  where each group of sixteen columns is physically contiguous, so the kernel walks it
  forwards instead of jumping a row stride per step. 100-116 → 180-196 GFLOP/s.
- **Aligning that panel to a cache line.** MLAS declares its panel 64-byte aligned; .NET
  gives no such guarantee, and a `float[]` was observed landing at offsets 0, 8, 16, 24, 40
  and 48 across successive allocations. Since the panel's rows are exactly one line apart,
  that offset decides whether *every* 512-bit load is a split load or none are.
  180-196 → 230-246 GFLOP/s.
- **Explicit `Vector512`.** `Vector512.IsHardwareAccelerated` reports `false` here and
  `Vector<float>.Count` stays at 8, but explicit `Vector512<float>` still compiles to real
  AVX-512: 577 GFLOP/s against 161 in a pure FMA loop.
- **Explicit `FusedMultiplyAdd`.** The JIT does not contract `a * b + c` into an FMA, because
  that would change the rounding. A kernel written the natural way silently runs at a
  fraction of peak; the calibration reports both rates side by side so the gap stays visible.
- **MLAS's register block**: twelve rows by two vectors on AVX-512, six elsewhere — twenty-four
  accumulators plus two operands and one reused broadcast.
- **Implicit-GEMM convolution.** The unrolled receptive fields are never materialised: the
  multiply asks for its operand a packed panel at a time and convolution gathers those
  columns straight out of the image, so the expansion — nine times the input for a 3x3 layer
  — is written once into a cache-resident panel instead of spilled to a buffer and read back.
- **Fusing layer normalisation**, another nine nodes per encoder and decoder layer, each a
  full pass over an 8.6 MB activation.
- **Pooled buffers**, recycled through a free list keyed by exact length, with reference
  counts on the storage rather than the tensor so `Reshape` views and `Identity` aliases keep
  their memory alive. A standalone `Relu` over a 26 MB tensor fell from 163 ms to 44 ms.
- **Reference-based loads** in the inner loop, removing eight bounds checks per iteration;
  a **blocked transpose** (270 → 51 ms); a **vectorised `erf`** (50 ms → negligible);
  **`Pow` by a constant exponent** lowered to multiplies (20 ms on one node); and
  **batched MatMul parallelised across batches** rather than inside each (396 → 160 ms).

### What was tried and rejected

Recorded because the measurements are the useful part:

- **Constant folding of the shape arithmetic.** 1683 of 2676 nodes are scalar bookkeeping —
  and all of them together account for 2.3 ms of 8081 ms.
- **An eight-row register block, before packing.** Measured markedly slower, 116 → 74
  GFLOP/s. Reading MLAS explained why: with an unpacked operand the reads, not the register
  pressure, were the constraint. Twelve rows only became worthwhile once the panel was packed.
- **Bigger convolution tiles**, back when convolution still tiled: the extra weight reuse did
  not pay for streaming a multi-megabyte buffer per tile.
- **Splitting the row range across threads** when column panels are scarce. Measured neutral
  on four cores; not kept.
- **`DOTNET_PreferredVectorBitWidth=512`.** Does not widen `Vector<T>` on this runtime; only
  explicit `Vector512` does.

### The remaining gap

Convolution is ~60% of runtime, at roughly 175 GFLOP/s against the multiply's own 230-246.
That difference is the gather that feeds it, which has no arithmetic to amortise its memory
traffic. Beyond that, the multiply sits at about 45% of the machine's measured ceiling where
MLAS reaches roughly 55%; the rest is in things C# cannot express — software prefetch hints,
and instruction scheduling done by hand in assembly. MLAS also carries per-CPU kernel
variants selected at run time, where this has one AVX-512 path and one portable path.

## Two bugs this caught

Worth recording, because neither would have been visible from the final output alone and
both are now regression-tested in `tests/Xberg.Tests/OnnxRuntimeTests.cs`.

**Softmax overflow.** `TensorPrimitives.SoftMax` exponentiates raw values without
subtracting the row maximum. RT-DETR's cross-attention produces logit rows around −164;
every `exp` underflowed to zero, the normalising sum was zero, and whole rows came back
NaN. The graph still ran to completion and produced plausible-looking numbers.

**Slice bound overflow.** Exporters spell "to the end of this axis" as `INT64_MAX`.
Narrowing the bounds tensor to `int` before clamping threw on the first decoder layer.
