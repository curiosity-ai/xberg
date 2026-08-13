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

## Two bugs this caught

Worth recording, because neither would have been visible from the final output alone and
both are now regression-tested in `tests/Xberg.Tests/OnnxRuntimeTests.cs`.

**Softmax overflow.** `TensorPrimitives.SoftMax` exponentiates raw values without
subtracting the row maximum. RT-DETR's cross-attention produces logit rows around −164;
every `exp` underflowed to zero, the normalising sum was zero, and whole rows came back
NaN. The graph still ran to completion and produced plausible-looking numbers.

**Slice bound overflow.** Exporters spell "to the end of this axis" as `INT64_MAX`.
Narrowing the bounds tensor to `int` before clamping threw on the first decoder layer.
