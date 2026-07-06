# Xberg C# Port — Phase 1 (Core Spine) Notes

This document records the modeling choices, deferrals, and intentional deviations from the
Rust source for the foundational "core spine" of the port. Later phases (extractors) build on
these types and contracts.

## How `ElementKind` is modeled

`ElementKind` is a **readonly struct + `ElementKindTag` enum + payload fields**
(`Types/ElementKind.cs`), not an abstract record hierarchy. Rationale:

- Rust's `ElementKind` is `#[derive(Copy)]` — a small value type. A readonly struct preserves
  value semantics and avoids a heap allocation per element.
- The renderers port Rust `match elem.kind { … }` directly to `switch (elem.Kind.Tag)` with
  payload access via `elem.Kind.Level` / `.Ordered` / `.TableIndex` / etc. — clean and allocation-free.
- Factory members (`ElementKind.Heading(level)`, `ElementKind.Title`, …) mirror the Rust variants.

**Serialization** (`ElementKindConverter`) reproduces serde's *default externally-tagged* enum
form, since the Rust `ElementKind` has no `#[serde(tag=...)]`:
- unit variants → bare string, e.g. `"Title"`, `"PageBreak"`;
- struct variants → `{"Heading":{"level":2}}`, `{"Table":{"table_index":0}}`, `{"OcrText":{"level":"Word"}}`.
Inner field names are snake_case; variant names are PascalCase (matching serde). Round-trips
verified by `InternalDocumentTests.InternalDocumentRoundTripsThroughJson`.

The other tagged unions use custom `JsonConverter`s to match serde exactly:
- `NodeContent` — internally tagged on `node_type` (nested under a `"content"` object on `DocumentNode`).
- `AnnotationKind` — internally tagged on `annotation_type`, flattened into the `TextAnnotation` object.
- `FormatMetadata` — internally tagged on `format_type`, payload fields flattened.
- `RelationshipTarget` — externally tagged (`{"Index":n}` / `{"Key":"…"}`).
- `OutputFormat` — bare string (lowercase known variants, or the custom renderer name).

## Deferrals in `derive.rs`

`Core/Derive.cs` implements the **native happy path** only. Skipped (all left `null`/default,
matching the Rust `..Default::default()` for those fields):

- OCR element building (`build_ocr_elements`), `ocr_elements`, `OcrText`-specific handling.
- Chunking, embeddings, keyword extraction, LLM usage, tree-sitter `code_intelligence`.
- Element-based output (`elements`): in Rust this is produced by a *separate* pipeline pass
  (`transform_extraction_result_to_elements`) gated on `ResultFormat::ElementBased`, **not** by
  `derive_extraction_result`. It is therefore not built here; `Elements` stays `null`. Wiring the
  transform is a later task.
- `djot_content` structured field (distinct from the djot *string* render) — always `null`.
- **Per-page markup re-render** (`apply_page_content_format`): pages keep their concatenated
  plain-text content. Rust re-renders each page's sub-document in the requested markup; that
  sub-document/index-remapping step is deferred.

**Folded-in behavior:** Rust's `derive_extraction_result` fills `content` with `render_plain`
and puts the requested-format render in a separate `formatted_content` field; the *pipeline*
(`core/pipeline/format.rs`) later swaps `content = formatted_content`. This port folds that swap
into `Derive` so `ExtractedDocument.Content` already reflects the requested `OutputFormat`
(plain when the format is Plain/Structured). `FormattedContent` is also retained (JSON-ignored).
`pre_rendered_content` is honored for Markdown/Djot when `metadata.output_format` matches.

Faithfully ported: relationship resolution, `build_pages` grouping, the stack-based
flat→tree `DocumentStructure` derivation (headings → Group + Heading child, containers,
definition-term/description pairing, `element_to_node_content`, `table_to_grid`, relationship
mapping, `finalize_node_types`), URI dedup, image Option-wrapping, and `extraction_method`
parsing from `metadata.additional["extraction_method"]`.

## Markdown / HTML / Djot renderer deviations

The Rust Markdown and HTML renderers build a **comrak** AST and format via
`format_commonmark` / `format_html` (~70 KB `comrak_bridge.rs`). Porting comrak's exact AST and
formatter is out of scope for the core spine, so:

- `MarkdownRenderer`, `HtmlRenderer`, `DjotRenderer` are **direct element-walk writers** producing
  structurally-equivalent output. Whitespace, escaping, and list/blockquote edge cases may differ
  from comrak; exact GFM/HTML golden parity is a follow-up once fixtures are wired.
- The Markdown **string post-processing helpers** (`UnescapeBackslashSequences`,
  `ReplaceHtmlEntities`, `CollapseExcessNewlines`) are ported **verbatim** and unit-tested.
- `Plain` and `Json` renderers are ported faithfully; `Json` uses relaxed escaping
  (`UnsafeRelaxedJsonEscaping`) so its emitted string matches `serde_json` (which does not escape
  `<`, `>`, `&`, `/`). These two are the byte-for-byte parity targets.

## `Metadata` deferrals

- All `Metadata` scalar/list/Option fields are ported. `FormatMetadata` variants relevant to
  content formats are present; `TextMetadata`, `ExcelMetadata`, `CsvMetadata`, `HtmlMetadata`
  (+ nested), `XmlMetadata`, `ImageMetadata` are reasonably complete. The less-common variants
  (Docx, Email, Pptx, Archive, Pdf, Bibtex, Citation, FictionBook, Dbf, Jats, Epub, Pst) are
  **stubbed** with their obvious fields. `Ocr`/`Audio`/`Code` variants are not registered
  (out of scope / OCR).
- `image_preprocessing`, `pages` boundary/info detail types are stubbed as `object?`.
- `HtmlMetadata` nested collections (`Headers`, `Links`, `Images`, `StructuredData`) are typed as
  `List<object>` stubs; the HTML extractor phase will populate concrete element types.

## MIME detection deferrals

- The **extension → MIME table** is ported verbatim (`Core/Mime.cs`).
- Content sniffing reproduces the common magic-byte signatures directly (JPEG, PNG, GIF, WEBP,
  BMP, TIFF, PDF, OLE2/CFB, 7z, gzip, ZIP), the ZIP→Office/iWork/HWPX subsequence scan, the PST
  signature, and the UTF-8 text heuristics (JSON / XML / HTML / PDF / plain) — all faithful.
- Rust delegates finer content detection to the `infer` crate (v0.19.0) and the `mime_guess`
  extension database for fallback. A **byte-exact port of `infer`/`mime_guess`** is deferred; the
  common formats that select an extractor are covered. `validate_mime_type` / `detect_or_validate`
  and tree-sitter fallbacks are not ported.

## Other notes

- **`ExtractedImage.data`** serializes as a JSON `u8` array (via `BytesAsU8ArrayConverter`) to
  match serde's default for `bytes::Bytes` (not base64).
- **Empty-collection omission:** serde's `skip_serializing_if = "Vec::is_empty"` is approximated
  with `[JsonIgnore(WhenWritingDefault)]`, which skips `null` but **not** empty non-null lists.
  Fields initialized to empty lists (e.g. `DocumentStructure.Relationships`, `PageContent.Tables`,
  `ExtractedDocument.ProcessingWarnings`) will emit `[]` rather than being omitted. This is a known
  deviation to revisit when golden-file diffing is wired; it does not affect the current unit tests.
- **BLAKE3** is a from-scratch pure-C# port of the reference implementation
  (`Internal/Blake3/Blake3.cs`), validated against the official test vectors (empty, 1-byte, 1023,
  1024) plus incremental/single-pass equivalence. Element IDs therefore match Rust byte-for-byte.
- **`elements`/ElementBased** and the `Extractor` public API register only `PlainTextExtractor`
  for now (`Registry.RegisterDefaults`); unknown MIME types yield a graceful empty result with a
  processing warning rather than an error.
