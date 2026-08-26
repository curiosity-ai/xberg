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
| `--benchmark N` | time `N` runs, then per-operator and per-node cost, throughput and pool hit rate |
| `--gemm` | time the kernel on the shapes these models lower to; add it to `--benchmark` to get both in one process, where the two are comparable |

Reading the JIT's own output is worth doing directly rather than reasoning about it — the
largest single win in the multiply was visible only there:

```bash
DOTNET_TieredCompilation=0 DOTNET_JitDisasm="TwelveRows512" \
    dotnet run --project tools/Xberg.OnnxParity -c Release -- --gemm
```

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

Timing on this VM is unreliable in three specific ways, and all three are handled before any
number is reported.

**The host changes underneath you.** Re-measuring the machine's peak fused-multiply-add
throughput between consecutive runs of the same binary has produced 100 and 218 GFLOP/s, and
one kernel shape has read 75.6, 32.6 and 40.4 ms across three runs of identical code. A swing
that large dwarfs most of what optimisation buys. So every timing run starts by printing the
CPU, core count and memory, then re-measuring peak FMA throughput (256- and 512-bit) and
streaming memory bandwidth — and **every rate is also reported as a fraction of that ceiling**.
Two consecutive runs agree on the fraction to within a point where their absolute figures
differ by 30%. Absolute GFLOP/s across runs is not evidence; percent of peak is.

For the same reason `--gemm` can run in the same process as `--benchmark`, so an in-model rate
and a standalone rate for the same shape are comparable at all, and `compare_speed.py` runs
ONNX Runtime and this runtime back to back rather than against a figure recorded earlier.

`--benchmark` needs no reference dump. A timing run measures time, and the time a convolution
takes does not depend on the numbers going through it, so the harness synthesises inputs from
the shapes the graph declares — a symbolic batch dimension becomes 1. Requiring the reference
meant regenerating hundreds of megabytes of promoted intermediates before anyone could measure
a kernel change, which is enough friction to stop the measurement happening at all:

```bash
dotnet run --project tools/Xberg.OnnxParity -c Release -- --model rtdetr.onnx --benchmark 3
```

**Cold code measures the compiler.** .NET starts methods in a quick-JIT tier and promotes
them only after roughly thirty calls, and this graph also has to fault in 169 MB of weights
and spin up the thread pool. Every benchmark discards three full inference passes first.

**Small shapes measure the thread pool.** Kernel-shape timings repeat until each shape has had
about 3 GFLOP of work. Before that, the smallest shape in the set read anywhere from 16 to 104
GFLOP/s purely on wake-up and timer granularity.

And the harness itself can be the thing being measured. The packing benchmark allocated its
scratch buffer inside the parallel body, which made packing look like half the cost of some
shapes; corrected, it is 3-20%, and a restructuring aimed at that phantom would have been
wasted. Anything that looks like a large effect is worth reproducing before acting on it.

### Where it stands

One 640x640 page, 4 cores, both sides measured in the same session:

| | median |
| --- | --- |
| ONNX Runtime | ~0.51 s |
| this runtime | ~0.95 s |
| *ratio* | *~1.8x* |

Down from 10.5 s, i.e. 16x, when this work started. The whole graph is 132 GFLOP of
multiply-accumulate; the arithmetic-bound nodes now run it at 185-190 GFLOP/s against a
measured 512-bit ceiling that varies between 400 and 620.

### What actually moved it

Ordered by effect, and none of it was guessed — each came from the per-node profile, from
reading MLAS (ONNX Runtime's own kernel library), or from reading the JIT's assembly output.

- **Folding the decomposed batch normalisation** (36% → nothing). The export spells every
  batch norm as a per-channel `Mul` then a per-channel `Add`, each a full streaming pass over
  a multi-megabyte activation to apply a constant affine map. `GraphOptimizer` folds the
  chain into the convolution's weights and bias, and the following activation — `Relu`,
  `Sigmoid`, or the `Sigmoid`/`Mul` pair that spells SiLU — into the same output pass.
- **Keeping the kernel's accumulators in registers.** RyuJIT was writing every accumulator
  through to the stack after every multiply-add — twenty-three 64-byte stores per iteration of
  the innermost loop, maintaining a copy nothing ever read. A `Vector512` passed by value to a
  real call is passed by hidden reference, so the local has to be materialised on the stack and
  its address escapes; one partial-block branch in the store helper was enough, however cold.
  The register kernels now see only whole blocks and the store is a single inlined
  load-add-store. 3x3 256→256 went 235 → 287 GFLOP/s, 1x1 1024→2048 112 → 293.
- **Packing the multiply's right-hand operand**, MLAS-style: a panel is copied into a buffer
  where each group of sixteen columns is physically contiguous, so the kernel walks it
  forwards instead of jumping a row stride per step. 100-116 → 180-196 GFLOP/s.
- **Aligning that panel to a cache line.** MLAS declares its panel 64-byte aligned; .NET
  gives no such guarantee, and a `float[]` was observed landing at offsets 0, 8, 16, 24, 40
  and 48 across successive allocations. Since the panel's rows are exactly one line apart,
  that offset decides whether *every* 512-bit load is a split load or none are.
  180-196 → 230-246 GFLOP/s.
- **Running leftover rows through the register block.** A row range that does not divide by
  the block size used to send its remainder through a one-row kernel, which re-reads the whole
  packed panel to produce one row. On a 64-channel layer the four leftover rows cost as much as
  the other sixty: the shape measured 142 GFLOP/s where the same shape with 60 rows measured
  228. The six-row kernel now takes a live-row count and serves partial groups.
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
- **Copying slices in runs.** `Slice` decided how much it could move contiguously by requiring
  every axis to be taken *whole*, which is the wrong test for the innermost axis — a
  contiguous sub-range of it is still contiguous. Slicing the last axis therefore fell back to
  one block copy per element, and the decoder does that six times a layer: 11.5 ms per node to
  move 6.5 MB. Slice went from 65 ms across 58 nodes to out of the profile's top ten.
- **Broadcasting where one operand holds still.** The broadcast plan looked for trailing axes
  over which *both* operands advance with the output. A per-row scale, `[N,D]` against `[N,1]`,
  has no such run at all, so the block collapsed to one element and the tensor was walked one
  element per delegate call — 11.5 ms to move 8.6 MB on one decoder node. A block now also
  forms where one side advances and the other holds still, as a vector-scalar call.
- **Packing depth-major.** Both packers walked block-major, reading each operand row once per
  column block — eight scattered sixteen-float reads across a row stride that for a 160x160
  feature map is 100 KB. Depth-major reads each row once and scatters only the writes.
- **Moving packed blocks as vectors.** Sixteen floats is exactly one cache line, and at that
  size `Span.CopyTo` is mostly call and length-dispatch overhead.
- **Giving max pooling its own walk.** The shared pooling walk finished one output pixel at a
  time, re-reading source rows per pixel and branching on max-versus-average in the innermost
  loop. Taking the kernel on the outside makes each pass a running maximum of a contiguous
  source row against a run of the output row. 11.3 → 7.8 ms on the backbone's one such node.
- **Vectorising the strided gather.** A convolution that downsamples failed the gather's
  `strideW == 1` test, so every block of every stride-2 layer — one at each ResNet stage
  transition — went element by element. Stride two is now a pair of loads and a two-source
  permute.
- **Splitting rows when panels are scarce.** The decoder's projections are 256 columns over
  8400 rows: two column panels, so two of four cores idle. Whole-model MatMul 169 → 118-135 ms.
- **Fusing layer normalisation**, another nine nodes per encoder and decoder layer, each a
  full pass over an 8.6 MB activation.
- **Pooled buffers**, recycled through a free list keyed by exact length, with reference
  counts on the storage rather than the tensor so `Reshape` views and `Identity` aliases keep
  their memory alive. A standalone `Relu` over a 26 MB tensor fell from 163 ms to 44 ms.
- **Reference-based loads** in the inner loop, removing eight bounds checks per iteration;
  **hoisted row cursors**, so a broadcast is one addressing mode rather than a `lea`, an `add`
  and a sign extension per row per iteration; a **blocked transpose** (270 → 51 ms); a
  **vectorised `erf`** (50 ms → negligible); **`Pow` by a constant exponent** lowered to
  multiplies (20 ms on one node); and **batched MatMul parallelised across batches** rather
  than inside each (396 → 160 ms).

### What was tried and rejected

Recorded because the measurements are the useful part:

- **Constant folding of the shape arithmetic.** 1683 of 2676 nodes are scalar bookkeeping —
  and all of them together account for 2.3 ms of 8081 ms.
- **An eight-row register block, before packing.** Measured markedly slower, 116 → 74
  GFLOP/s. Reading MLAS explained why: with an unpacked operand the reads, not the register
  pressure, were the constraint. Twelve rows only became worthwhile once the panel was packed.
- **Bigger convolution tiles**, back when convolution still tiled: the extra weight reuse did
  not pay for streaming a multi-megabyte buffer per tile.
- **`DOTNET_PreferredVectorBitWidth=512`.** Does not widen `Vector<T>` on this runtime; only
  explicit `Vector512` does.
- **In-place unary operators.** The session knows which values die at each node, so a `Relu`
  whose input is dead, pool-owned, held by one name and covering its whole array could write
  over it — one fewer allocation, and a destination already in cache. Implemented and measured
  the wrong way: the pool did visibly less work (629 buffers reused and 105 allocated per run
  became 586 and 92) and `Relu` still went from 35-36 ms to 40-41. Reverted rather than kept
  on the strength of the reasoning, since it also puts aliasing risk in the one place the
  parity harness cannot check — capture mode has to retain every intermediate, so in-place is
  disabled exactly when the per-node comparison runs.
- **Splitting rows *whenever* panels are below twice the core count.** Every extra row chunk
  repacks the same operand panel; a 1024x2048 product over a 20x20 map fell from 166 to 97
  GFLOP/s. Kept only where the panel count is below the core count outright.

  This one is also a lesson in reading your own experiments. It was first recorded as rejected
  outright — but the shape it had been tested on had four panels on four cores, so the guard
  computed one chunk and the mechanism never engaged. What was recorded as its cost was the
  host moving between runs. The percent-of-peak column exists because of this.

### The remaining gap

At ~1.8x, the arithmetic-bound nodes run at 185-190 GFLOP/s and the best single shapes reach
270-300, so the spread across shapes is now the largest remaining term rather than the peak.
Convolution is still ~60% of runtime.

### Two wrong turns, and what they cost to find

This section named a next step twice and was wrong twice. Both are recorded because the way
each was disproved is the useful part.

**"A direct convolution for small-channel layers."** A per-node profile says the small-channel
3x3 layers run at 195-277 GFLOP/s, mid-pack, while the slowest shapes in the graph are 1x1
convolutions with a large channel count at 113-128. A kernel avoiding the nine-fold `im2col`
expansion would have been attacking shapes that are not the problem.

**"So it must be the shallow reduction."** The reasoning was that a 1x1 reduces over 256 or 512
elements where a 3x3 over the same channels reduces over nine times as many, so each output
tile's prologue and accumulator store are amortised nine times worse. It is a good story and it
is not what is happening: `--gemm` runs those exact products at 365-379 GFLOP/s, near the best
shapes in the set. The multiply was never slow.

What was actually missing was a measurement. `--gemm` measures the product a convolution lowers
to; nothing measured the convolution, which is 61% of the graph. `--conv` does, and on every hot
shape — 1x1 included — a whole convolution runs at 71-113% of the calibrated ceiling. Only the
shortcut convolutions are genuinely slower in the graph than in isolation, and they are worth
about 2% of inference between them.

The first version of `--conv` read **21 GFLOP/s on a shape whose multiply runs at 365**, because
it allocated a fresh multi-megabyte output on every call instead of going through the pool the
session installs: it was measuring the garbage collector. Its second version still disagreed with
itself by 3x between adjacent columns, on three warm-up passes and five samples. The numbers only
became stable at thirty of each. Both are the same lesson the top of this section already
records, learned again.

### Where the time actually goes, and what was taken

`Conv` is 61% and `MatMul` 14%; 1,649 of the 2,315 nodes take under 10 us each, 0.2% between
them. The remaining quarter is element-wise and data movement, and that is where the one win
in this pass came from.

**Writing an element-wise result over a dying operand.** Four hundred nodes read a buffer and
write another the same size; when the operand they read is dead the moment they are done with
it, the second buffer is waste. `OnnxSession.FindReusableOperand` decides, and the conditions
are the whole of the risk — reusing one buffer too eagerly corrupts a value some later node
reads, and that surfaces as a plausible detection rather than a crash.

| | before | after |
| --- | --- | --- |
| `Relu` | 35.7 ms | 27.5 ms |
| `Add` | 50.3 ms | 42.3 ms |
| `Mul` | 10.6 ms | 8.4 ms |
| buffer rentals per inference | 734 | 473 |
| pool memory retained | 214 MiB | 168 MiB |

Whole-model, timed by `--check-reuse`, which runs both configurations alternately in one process
because across processes this host moves more than the difference: **1.00x, 1.02x, 1.04x** over
three runs. Two percent, and honestly at the edge of what can be resolved here — the per-operator
figures are the solid part, and they are consistent with it, since the operators involved are 12%
of runtime. The memory figures are counts rather than timings and do not vary.

`--check-reuse` also compares every graph output bitwise between the two configurations, which is
the only check that means anything for a change of this kind. RT-DETR's three outputs are
identical to the bit.

- Beyond that: software prefetch hints and hand-scheduled instruction order, which C# cannot
  express, and MLAS's per-CPU kernel variants selected at run time, where this has one AVX-512
  path and one portable path.

## Two bugs this caught

Worth recording, because neither would have been visible from the final output alone and
both are now regression-tested in `tests/Xberg.Tests/OnnxRuntimeTests.cs`.

**Softmax overflow.** `TensorPrimitives.SoftMax` exponentiates raw values without
subtracting the row maximum. RT-DETR's cross-attention produces logit rows around −164;
every `exp` underflowed to zero, the normalising sum was zero, and whole rows came back
NaN. The graph still ran to completion and produced plausible-looking numbers.

**Slice bound overflow.** Exporters spell "to the end of this axis" as `INT64_MAX`.
Narrowing the bounds tensor to `int` before clamping threw on the first decoder layer.
