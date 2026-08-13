# Xberg C# Port — Work Breakdown

Legend: `[ ]` todo · `[~]` in progress · `[x]` done · `[-]` out of scope (dropped)

Each format is "done" when the `Xberg.TestRunner` output matches the locally generated
`{filename}-results-rust.json` golden files for its fixtures (documented deviations allowed).
See "Re-syncing after an upstream merge" in `Claude.md` for how to regenerate them.

> **Status (after the August upstream merge, 1821 Rust commits):** measured against goldens
> regenerated from the merged `crates/xberg` over the full 2942-fixture corpus:
> **2122 fixtures (72.1%) match on every hard dimension**; content-parity **77.0% identical,
> 87.6% ≥95%-similar**; 125 fixtures (<80%) are genuine content misses; **22 catastrophes
> (0.7%)**. 340 unit tests.
>
> The merge moved the goalposts: these numbers are against *current* upstream behaviour, so
> they are not comparable with the pre-merge figures they replace. Re-sync fixes so far:
> markdown smart punctuation and block-level raw HTML, structural rendering for
> YAML/TOML/JSONL plus `flattened_fields`, a JSON node for every element kind, docx entity
> refs / inline annotations / page-break accounting, `.msg` PR_HTML binary bodies, the full
> email header set with CRLF normalization, csv table elements, DocSecurity flags,
> character counts as Unicode scalars rather than UTF-8 bytes, and — for PDF — scanned-page
> detection plus serde-faithful float formatting.

## Known gaps after the merge

Ordered by corpus impact. Each is a real upstream behaviour the port does not yet reproduce —
not a cosmetic difference.

- [x] **PDF metadata (388 fixtures).** Was 0/388, now 324/388. Two independent causes, both
      fixed: the missing scanned-detection fields (`pdf/scan_detect.rs` is now ported as
      `Internal/Pdf/PdfScanDetect.cs`, fed by a dedicated content-stream pass), and serde's
      float spelling — an integral `f32` is `0.0`, not `0`, so `scanned_confidence` failed
      on every document that is *not* scanned. The remaining 64 are other field differences,
      not yet triaged.
- [ ] **PDF content (389 fixtures, 77 fully matching; plain 114/388, markdown 49/388).**
      The largest remaining area, and upstream landed 127 PDF commits in this window. This is
      extraction *quality*, not a missing feature, and it does not decompose into a few
      systematic fixes — measured, not assumed:
      - Plain-text divergence spreads across **223 distinct first-divergence clusters** for
        274 failing fixtures, i.e. mostly reading-order and whitespace, one document at a time.
      - Markdown/html were pinned far below plain by **heading level** assignment rather than
        heading detection. Porting the sparsity gate, the H1-rescue gates and the level
        inference took markdown 32 → 49 and html 27 → 41 of 388, but moved **no fixture into
        `ok`**: every one of them still fails plain or json, so the heading work only pays off
        once reading order does. Rust's remaining rules are in `pdf/structure/classify.rs` +
        `adapters.rs` — still unported there: `sparse_multi_page_heading_map` (a repeated font
        tier across pages), `has_repeated_sparse_peer_heading_tier`, the changelog
        hierarchy passes, and `promote_title_heading`'s guard. Port a rule at a time and
        measure; guessing costs more than it gains.
      - Tables: of 234 mismatches only **30** are "we found no table at all" (the missing
        ruling-line/bordered-grid tier). The other 204 are cell-segmentation differences.
      A caution learned the hard way: `pdf/structure/text_repair.rs` looks like a free win but
      belongs *only* to documents the structure pipeline assembled. Applying it to the flat
      native-text split as well cost 33 of the 114 matching plain fixtures.
- [ ] **DocTags ingestion (12 of the 22 catastrophes).** Upstream added DocTags as an input
      format, so `*.doctags.txt` fixtures parse into a structured tree; we read them as plain
      text and emit the raw `<doctag><loc_60>…` markup. New format, not a bug fix.
- [ ] **TOML key ordering (toml 1/5).** Rust's `toml::Value::Table` is a `BTreeMap`, so keys
      are emitted in sorted order; the C# parser preserves file order. Datetimes also need the
      `$__toml_private_datetime` wrapper that toml→serde_json produces, while the flattened
      view keeps them plain.
- [ ] **pptx slide numbering (metadata 0/11).** Upstream GH#1413 reworked the extractor to
      parse each archive-derived slide independently and carry its true slide number; we still
      rebuild from the rendered string and only advance the number on a titled slide.
- [ ] **Reviewer comments (`CommentRef` / `CommentDefinition`).** Upstream gave docx comments
      their own element kinds (GH#300). The C# `ElementKindTag` has no such variants, so the
      JSON renderer's comment arms could not be ported with the rest.
- [ ] **Email attachment text (3 eml + 2 msg catastrophes).** Rust extracts each attachment
      through the pipeline and appends its text under a level-2 heading; we list attachment
      names and sizes only.
- [ ] **Markdown math.** `$…$` / `$$…$$` should surface as content with the delimiters
      stripped (pulldown-cmark `ENABLE_MATH`); we pass the delimiters through as literal text.
- [ ] **Citation formats (nbib, ris).** Severe under-extraction — both are catastrophes.
- [ ] **html 10/41, typ 0/8, odp, ipynb, mdx.** Not yet triaged; use `--cluster`.

---

## Phase 0 — Setup & reference data

- [x] Analyze the Rust repo; identify the content-extraction subset.
- [x] Write `dotnet/Claude.md` (architecture + mapping).
- [x] Create the `dotnet/` solution: `Xberg` (lib), `Xberg.Tests`, `Xberg.TestRunner` (CLI).
- [x] Write the Rust golden-reference generator (`tools/xberg-reference-gen`).
- [x] Run the generator over `../test_documents` to produce the `*-results-rust.json`
      goldens (generated locally, not committed — see `Claude.md`).
- [x] Wire `Xberg.TestRunner` to load fixtures + golden files and diff per format.

## Phase 1 — Core spine (foundational; everything depends on it)

- [x] `Types/`: `InternalDocument`, `InternalElement`, `ElementKind` (tagged union),
      `InternalElementId` (BLAKE3), `Relationship`.
- [x] `Types/`: `Metadata`, `Table`, `BoundingBox`, `ExtractedImage`, `ExtractedUri`,
      `PageContent`, `ExtractedDocument`, `ExtractionResult`, `ProcessingWarning`.
- [x] `Types/`: `DocumentStructure` tree + `NodeContent` + `ContentLayer` + `TextAnnotation`
      (needed for the structured object output).
- [x] `Internal/Blake3`: BLAKE3 hash (must match Rust byte output for element IDs).
- [x] `Core/Config`: trimmed `ExtractionConfig`, `OutputFormat`, `ResultFormat`, per-format
      option structs (native-only).
- [x] `Core/Mime`: extension + magic-byte MIME detection (`detect_mime_type*`), format table.
- [x] `Core/Registry`: extractor registry, dispatch by MIME; `IExtractor` interface.
- [x] `Rendering/Common`: nesting state, `IsBodyElement`, container helpers, `GetLanguage`.
- [x] `Rendering/Plain`.
- [x] `Rendering/Json` (heading-driven tree — port verbatim from `json.rs`).
- [x] `Rendering/Markdown` (GFM). Decide: port comrak AST bridge or direct writer.
- [x] `Rendering/Html` (+ styled variant).
- [x] `Rendering/Djot`.
- [x] `Core/Derive`: `InternalDocument → ExtractedDocument` (native path of `derive.rs`):
      page splitting, structure derivation, language detection (optional), URI collection.
- [x] `Xberg` public API: `Extract(input, config)` sync + async, `ExtractBatch`.

## Phase 2 — Office formats (priority)

- [x] `Internal/Ooxml`: shared OOXML helpers (ZIP open, relationships, shared strings,
      content-types, core/app properties → `Metadata`).
- [x] **docx** (`extractors/docx.rs`, `extraction/docx/`) — paragraphs, styles, tables,
      lists, hyperlinks, images, headers/footers, tracked changes.
- [x] **xlsx** (`extractors/excel.rs`, `extraction/excel.rs`) — sheets → tables, shared
      strings, number formats. Port needed calamine logic.
- [x] **pptx** (`extractors/pptx.rs`, `extraction/pptx/`) — slides, text frames, tables, notes.
- [x] **odt** (`extractors/odt.rs`) — ODF text.
- [x] **ods** — ODF spreadsheet (via excel/calamine path).
- [x] **doc** (`extractors/doc.rs`, `extraction/doc/`) — legacy Word (CFB + FIB/piece table).
- [x] **ppt** (`extractors/ppt.rs`, `extraction/ppt/`) — legacy PowerPoint (CFB).
- [x] **xls** — legacy Excel (CFB, BIFF) via calamine path.
- [x] **rtf** (`extractors/rtf/`) — RTF control-word parser.
- [x] **epub** (`extractors/epub/`) — ZIP + OPF spine + XHTML.
- [x] `Internal/Cfb`: OLE compound-file reader (shared by doc/ppt/xls/hwp/msg).

## Phase 3 — Structured, markup & data formats

- [x] **html** (`extractors/html.rs`) — HTML→Markdown/structured. Port `html-to-markdown-rs`
      or use AngleSharp + custom writer.
- [x] **xml** (`extractors/xml.rs`), **jats** (`jats/`), **docbook** (`docbook.rs`).
- [x] **markdown** (`extractors/markdown.rs`) + **mdx** (`mdx.rs`) + **djot** (`djot_format/`).
- [x] **csv** (`extractors/csv.rs`) — delimiter sniffing → table.
- [x] **structured** (`extractors/structured.rs`) — JSON / JSONL / YAML / TOML.
- [x] **text** (`extractors/text.rs`) — plain text.
- [x] **opml** (`opml/`), **bibtex** (`bibtex.rs`), **citation** (`citation.rs`).
- [x] **rst** (`rst.rs`), **latex** (`latex/`), **org** (`orgmode.rs`), **typst** (`typst.rs`),
      **jupyter** (`jupyter.rs`), **fictionbook** (`fictionbook.rs`), **dbf** (`dbf.rs`).

## Phase 4 — Email & archives

- [x] **eml** + **msg** (`extractors/email.rs`, `extraction/email.rs`) — MIME + CFB msg.
      Attachment-text inlining still missing; see "Known gaps".
- [ ] **pst** (`extractors/pst.rs`) — Outlook PST (port `outlook-pst`; large — evaluate).
- [x] **archives** (`extractors/archive.rs`) — zip / tar / 7z / gzip, recursive extraction
      of children through the pipeline.
- [x] `Internal/SevenZip` (managed LZMA/LZMA2 + 7z container), `Internal/Tar`; gzip/deflate via BCL.

## Phase 5 — PDF, images, Korean & Apple formats

- [x] **pdf** (`extractors/pdf/`, `pdf/`) — text extraction, tables, annotations, form fields,
      per-page content. **Largest single effort.** Pure-managed PDF reader required.
- [x] **image** (`extractors/image.rs`, `extraction/image*.rs`) — metadata + EXIF only
      (OCR path dropped). Use ImageSharp for decode + dimensions.
- [x] **hwp** (`extractors/hwp.rs`) — CFB-based Hangul.
- [x] **hwpx** (`extractors/hwpx.rs`) — ZIP-based Hangul (port `unhwp`).
- [ ] **iwork** (`extractors/iwork/`) — Pages/Numbers/Keynote (ZIP + snappy + protobuf/IWA).

## Phase 6 — Code files (LOWEST priority)

- [ ] **code** (`extractors/code.rs`) — tree-sitter equivalent for 306 languages.
      **Only after all of Phases 1–5 are ported and green.** Evaluate a managed tree-sitter
      binding vs. a lighter language-detection + fenced-block approach.

## Phase 7 — Layout detection (ML)

Brought into scope after the merge. The Rust build reaches ONNX Runtime through `ort`,
a native library; a portable managed NuGet package cannot, so the model runs on a
hand-written ONNX runtime instead of a binding. See `tools/onnx-parity/README.md`.

- [x] **ONNX model parser.** Hand-written protobuf wire reader plus
      ModelProto/GraphProto/NodeProto/TensorProto/AttributeProto decoding
      (`Internal/Onnx/ProtoReader.cs`, `OnnxModel.cs`). No dependency, no codegen; tensor
      payloads are slices over the model bytes rather than copies.
- [x] **Operator kernels** (`Internal/Onnx/Ops/`) covering both pinned graphs — the 40
      operators RT-DETR uses and the table classifier's set. Vectorised through
      `System.Numerics.Tensors`; convolution lowers to GEMM via im2col with dedicated
      pointwise and depthwise paths, and MatMul uses an axpy-ordered kernel so the inner
      loop is a contiguous fused multiply-add.
- [x] **Graph execution** (`OnnxSession.cs`) with liveness-based release of intermediates,
      which is what keeps RT-DETR's working set bounded.
- [x] **RT-DETR wrapper** (`Internal/Layout/RtDetrModel.cs`) and the layout types ported
      from `layout/types.rs`, including the exact preprocessing contract: bilinear resize to
      an exact 640x640 (aspect ratio *not* preserved), `/255`, no ImageNet normalisation.
- [x] **Layer-by-layer validation** against ONNX Runtime via `tools/onnx-parity` and
      `tools/Xberg.OnnxParity`. Every operator instance matches in isolation; all detections
      above threshold agree in class, confidence and geometry.
- [ ] **Model acquisition** — port `layout/model_manager.rs`: Hugging Face download, on-disk
      cache, SHA-256 verification, atomic publish with rollback.
- [x] **Graph optimisation and buffer reuse.** Decomposed batch-norm folded into convolution
      weights, activations fused into the convolution's output pass, and activation storage
      recycled through a pool with reference counts on the buffer (so `Reshape` views and
      `Identity` aliases keep their memory alive). Both verified to produce byte-identical
      detections.
- [ ] **Inference performance.** ~2.1 s per 640x640 page on 4 cores against ONNX Runtime's
      ~0.69 s measured in the same session — roughly 3x, down from 16x. What moved it is
      recorded in `tools/onnx-parity/README.md`, along with what was measured and rejected.
      Convolution is ~58% of runtime and now runs at about the same rate as the bare matrix
      multiply, so the microkernel is the limit: closing the rest means packing both operands
      into kernel-order panels and per-CPU kernel selection, which is what MLAS does.
- [ ] **Page rasterisation.** The blocker for end-to-end use: layout detection needs a
      rendered page bitmap, and the C# port has no PDF renderer. Until one exists the model
      can only be driven from images supplied by the caller.
- [ ] **Remaining models**: PP-DocLayout-V3, TATR, SLANeXt wired/wireless, PP-LCNet table
      classifier. The runtime already executes the classifier; the others need their own
      pre/postprocessing ported.
- [ ] **Layout-aware reading order** (`extractors/pdf/reading_order.rs`). Note this is
      entirely `#[cfg(feature = "layout-detection")]`, so it is absent from the goldens —
      measuring it needs `xberg-reference-gen` rebuilt with that feature enabled.

## Cross-cutting

- [ ] `Metadata` extraction parity (office core/app props, EXIF, PDF info dict).
- [ ] URI/link collection.
- [ ] Per-page content splitting.
- [ ] Document-structure tree derivation (for the structured object output).
- [ ] Security limits (max size, zip-bomb guards — `extractors/security.rs`).
- [ ] CI: build + run `Xberg.TestRunner` on the fixtures; publish NuGet on tag. Note the
      corpus is no longer self-contained: CI must run `test_documents/scripts/fetch_corpus.py`
      and regenerate goldens, since neither the binaries nor the goldens are in git.
- [ ] Performance: the 55 MB `parsebench/text_content.jsonl` fixture takes ~29s to render
      markdown now that structured formats build an element tree. Not wrong, but far off
      Rust; the harness timeout was raised to 120s to keep it measured rather than skipped.

## Excluded (dropped per requirements)

- [-] OCR (Tesseract/Paddle/candle), doc orientation.
- [-] Audio/video transcription.
- [-] Embeddings, reranking, NER/GLiNER, keyword extraction, chunking-for-RAG.
- [-] LLM / structured LLM extraction, captioning.
- [-] Server mode (REST API, MCP), URL ingestion/crawling.
