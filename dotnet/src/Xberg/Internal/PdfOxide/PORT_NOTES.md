# `Internal/PdfOxide` — the pdf_oxide text pipeline, ported

The port reaches PDF text through its own content-stream interpreter
(`Internal/Pdf/PdfContentExtractor.cs`). Upstream xberg reaches it through
**pdf_oxide**, and the two produce spans at different granularity — which is the
single largest source of PDF divergence, because every downstream rule (spacing,
reading order, paragraph breaks, table cell assignment) is calibrated to
pdf_oxide's granularity. Transplanting individual thresholds was measured three
times and regressed every time; the only thing that closes it is porting the
producer.

This directory is that port: pdf_oxide's font, content-stream and text-extraction
layers, faithful enough that the spans match.

## Source

`~/.cargo/registry/src/index.crates.io-*/pdf_oxide-0.3.77/src/`

Cite the Rust file (and function) at the top of each C# file so re-syncing is
mechanical.

## What is NOT ported

pdf_oxide's own file layer — xref, object streams, filters, decryption — is not
ported. `Internal/Pdf` already has a working equivalent, and the file layer is not
where the divergence lives. Ported code reaches objects through
`PdfDocument`/`PdfObject` and the `Ox` helpers in `OxideObjects.cs`.

Also out of scope, as ever: rendering, writing/editing, OCR, FFI, WASM.

## The spine — read these first

| File | What it gives you |
|---|---|
| `OxideGeometry.cs` | `OxPoint`, `OxRect`, `OxMatrix` — ports of `geometry/mod.rs` + `Matrix` |
| `OxideLayout.cs` | `OxTextSpan`, `OxTextChar`, `OxPageText`, `OxColor`, `OxFontWeight`, `OxMcidScope`, `OxArtifactType` |
| `OxideObjects.cs` | `Ox.*` accessors over `PdfDocument`/`PdfObject` |

Do not edit the spine. If a port genuinely needs a field or helper the spine lacks,
say so in your report rather than adding it — a second writer editing the spine
breaks everyone else's build.

## Conventions

- **Single precision.** pdf_oxide is `f32` end to end and its thresholds were tuned
  there. Use `float`, not `double`, for geometry, widths and thresholds. Widening
  silently moves where spans break.
- **Faithful first.** Port the logic as written, including the guards that look
  redundant — they are usually load-bearing on some fixture. Where the Rust has a
  comment explaining *why*, keep that reasoning in the C# comment.
- **Comment density matches the surrounding port.** Explain why a rule exists, not
  what a line does. No commented-out code, no "step 1 / step 2" narration.
- **Naming.** C# conventions (PascalCase members), Rust names otherwise recognisable:
  `merge_adjacent_spans` → `MergeAdjacentSpans`.
- **No `unsafe`, no P/Invoke.** The package is pure managed.
- **Own your files.** Each work item owns a disjoint set of new files. Never edit a
  file another item owns; `TextExtractor` is a `partial class` precisely so several
  files can contribute to it without touching each other.

## Wiring

Nothing here is called from `PdfExtractor` until the whole pipeline lands and is
measured against the corpus. Until then this namespace is dead code that must
compile and be covered by unit tests.
