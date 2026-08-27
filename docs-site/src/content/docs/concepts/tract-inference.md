---
title: "Pure-Rust Inference (tract)"
---

Xberg runs its ML models — layout detection, table classification, document-orientation, OCR —
through [ONNX Runtime](https://onnxruntime.ai/) by default. ONNX Runtime is a native library and
cannot link on `wasm32` or the Android x86_64 emulator. On those targets Xberg runs the same models
through [`tract`](https://github.com/sonos/tract), Sonos' pure-Rust ONNX engine, behind a shared
inference seam. The `tract` engine loads the identical `.onnx` artifacts (no weight conversion), is
CPU-only, and needs no C toolchain.

ONNX Runtime stays the default on every native build. The `tract` engine is selected only where ORT
cannot link, and it trades CPU latency for portability — see [Latency](#latency).

## Model coverage

`tract` 0.23.4 does not execute every model Xberg ships. The seam routes each model to whichever engine
is active; models tract cannot run stay ONNX Runtime-only and are compiled out of the pure-Rust
feature sets (`layout-tract`, `auto-rotate-tract`).

| Model | Role | `tract` |
|---|---|---|
| RT-DETR | Layout detection | Runs |
| PP-LCNet | Table classifier, document-orientation, text-line orientation | Runs |
| DBNet / CRNN / AngleNet | PaddleOCR detection / recognition / angle | Runs — see [PaddleOCR](#paddleocr) |
| TATR | Table-structure recognition | ONNX Runtime only |
| PP-DocLayout-V3 | Layout detection | ONNX Runtime only |
| SLANeXt | Table-structure recognition | ONNX Runtime only |

The three ONNX Runtime-only models are blocked by concrete gaps in tract 0.23.4:

- **TATR** is a quantized export. Pinning the input clears the convolution's symbolic in-channel, but
  a fused scale constant carries a symbolic batch size the type analyser cannot unify with a concrete
  `1`.
- **PP-DocLayout-V3** clears its input facts, but tract's `LayerNormalization` translator then
  mis-infers the shape of the DETR decoder's norm layer — an op-translation bug, not a shape-pinning
  gap.
- **SLANeXt** uses the ONNX `Loop` operator, which tract does not implement.

Revisit each only if a non-quantized export or an upstream tract fix lands.

## Latency

Measured on Apple Silicon (aarch64), release build, best-of-8 warm inferences. Each engine runs
**as Xberg ships it**: ONNX Runtime with its default intra-op thread pool (up to `min(8, cores)`
threads), tract single-threaded (the seam configures no tract thread pool). The ratio below is
therefore an *as-shipped, wall-clock* comparison — the real cost you pay on a no-ORT build versus
native ORT — and an **upper bound** on the pure per-core kernel gap, since part of ORT's lead is
thread parallelism rather than kernel efficiency.

| Model | tract load | ORT load | tract run | ORT run | tract / ORT run |
|---|---|---|---|---|---|
| RT-DETR layout detector | 465 ms | 221 ms | 2637 ms | 137 ms | 19.3× |
| PP-LCNet table classifier | 22 ms | 9 ms | 31.9 ms | 2.2 ms | 14.4× |
| PP-LCNet document-orientation | 22 ms | 8 ms | 31.9 ms | 2.8 ms | 11.5× |

As each engine ships (ORT multi-threaded, tract single-threaded), tract's pure-Rust CPU path runs
roughly 11–19× slower than ONNX Runtime in wall-clock. This is the accepted
trade-off: these models run about once per page, and on the targets tract exists for — WASM and the
Android x86_64 emulator, where ONNX Runtime cannot link at all — the alternative is no inference, not
ORT. Native builds keep ONNX Runtime, so the regression never reaches native users. RT-DETR's
~2.6 s per inference is the ceiling to watch for WASM UX; the CNN classifiers at ~32 ms are
comfortable.

Reproduce the table with:

```sh
cargo test --release -p xberg --no-default-features --features "layout-detection,auto-rotate,tract" \
  --lib inference::tract_backend::tests::tract_vs_ort_latency_report -- --ignored --nocapture
```

## Platform availability

| Target | Engine | Models |
|---|---|---|
| Native (desktop, server, Windows, Android arm64) | ONNX Runtime | Full set |
| Android x86_64 emulator (`android-target`), iOS | tract | RT-DETR layout, table classifier, document-orientation |
| WASM (`wasm-target`) | tract | RT-DETR layout, document-orientation (streamed weights via `detectLayout` / `detectOrientation`) |

## PaddleOCR

Shape handling on tract depends on how the plan is built. A plan left symbolic tolerates a new input
shape on every call; a plan pinned via `with_input_fact` bakes that exact shape in as a constant and
errors on any other. DBNet's FPN skip connections only optimize when pinned, so DBNet plans are
necessarily shape-pinned — and DBNet resizes each page to content-dependent dimensions, so one plan
cannot serve every page.

Because a pinned plan corresponds to exactly one shape, padding every page into one fixed square
canvas would bound the plan count to one by construction. That is not what Xberg does, and the
reason is a measured one: both detection backbones are PP-LCNets carrying `GlobalAveragePool`
squeeze-and-excitation blocks (10 in PP-OCRv5 `det/mobile`, 8 in PP-OCRv6 `det/tiny`) which reduce
over the **whole** spatial extent. Enlarging the input therefore rescales every channel gate and
shifts the probability map across the entire page, not just near the padding seam. On a 791×1024
scan resized to 480×640, padding it into a 640×640 canvas moved the map by up to 0.77 (mean 2.6e-3),
flipping 827 of 307 200 pixels across DBNet's 0.3 binarization threshold and merging two text lines
into one region — 59 detected regions became 58, and 29 words were lost end to end.

DBNet plans are therefore pinned to each page's **own** resized extent and cached by shape (four
resident plans, least-recently-used eviction). A document's pages nearly all resize to the same
extent, so the cache is built once and reused; a new extent costs one plan build. With the extents
equal, the two engines agree to 5.0e-5 on the probability map and produce identical detection boxes,
which is what `xberg`'s `paddle_ocr::tract_parity` suite asserts. CRNN, which batches by
content-dependent width, is left symbolic and tolerates varying widths in one plan; AngleNet and the
layout CNNs use a fixed resolution and need no special handling either way.
