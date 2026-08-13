# Xberg — C# / .NET 10 Port

This directory contains a **native C# port** of the content-extraction engine from the
Rust [`xberg`](../crates/xberg) crate. It is *not* a wrapper around the Rust library or its
NuGet package — every extractor, type, and renderer is reimplemented in managed C#, shipped
as the `Xberg` NuGet package.

The original Rust sources under [`../crates`](../crates) are left untouched so that upstream
Rust work can be merged and the C# port re-synchronized.

---

## Scope

**In scope — content extraction only:**

- All supported document/file formats (Office first, then the rest).
- All original *output formats*: plain text, Markdown, HTML, and the structured
  object model / JSON tree.
- Metadata, tables, images (bytes + references), URIs, per-page content.

**Out of scope (explicitly excluded):**

- OCR (Tesseract / PaddleOCR / candle VLMs), audio/video transcription.
- Embeddings, GLiNER/NER, LLM/structured-extraction, reranking.
- Server mode (REST API, MCP), chunking-for-RAG, keyword extraction.
- Code intelligence (tree-sitter, 306 languages) — **low priority**, only after
  every other format is ported and validated.

When a Rust code path branches into an excluded feature (e.g. `if config.ocr … `),
the C# port takes the "native extraction only" branch and drops the OCR path.

---

## Architecture (mirrors the Rust crate)

The Rust pipeline is:

```
input bytes ─▶ MIME detection ─▶ pick Extractor ─▶ InternalDocument
            ─▶ derive_extraction_result(output_format) ─▶ ExtractedDocument
                    │
                    └─ renderer (plain / markdown / html / json tree) fills `content`
```

The C# port keeps the same shape. Key concept: **every extractor produces one
intermediate representation — `InternalDocument` — and the output format is a
*rendering* concern applied afterwards.** Extractors never format text themselves
(except when they set `PreRenderedContent`, see below).

### The intermediate representation — `InternalDocument`

Rust: `crates/xberg/src/types/internal.rs`. This is the spine of the whole system.

- `InternalDocument`
  - `Elements: List<InternalElement>` — flat, in reading order.
  - `Tables: List<Table>`, `Images: List<ExtractedImage>` — referenced by index from elements.
  - `Metadata`, `SourceFormat`, `MimeType`, `Uris`, `Relationships`.
  - `PreRenderedContent: string?` — when an extractor already produced high-quality
    output (e.g. HTML→Markdown), the pipeline returns it verbatim instead of
    re-rendering from elements.
  - `PrebuiltPages`, `Children` (archives), warnings, annotations, form fields, etc.

- `InternalElement`
  - `Kind: ElementKind`, `Text: string`, `Depth: ushort`, `Page: uint?`,
    `Bbox`, `Layer` (Body/Header/Footer/Footnote), `Annotations`, `Attributes`, `Anchor`.
  - OCR-only fields are omitted in the port.

- `ElementKind` (discriminated union — model as a C# abstract record hierarchy or a
  struct with a tag enum + payload). Full variant list:
  `Title`, `Heading{level:byte}`, `Paragraph`, `ListItem{ordered:bool}`, `Code`,
  `Formula`, `FootnoteDefinition`, `FootnoteRef`, `Citation`, `Slide{number:uint}`,
  `DefinitionTerm`, `DefinitionDescription`, `Admonition`, `RawBlock`, `MetadataBlock`,
  `ListStart{ordered:bool}`, `ListEnd`, `QuoteStart`, `QuoteEnd`, `GroupStart`,
  `GroupEnd`, `Table{tableIndex:uint}`, `Image{imageIndex:uint}`, `PageBreak`,
  `OcrText{level}` (OcrText only appears from excluded OCR paths — keep the variant
  for completeness but no extractor emits it).

  Each variant has a stable string `Discriminant()` (see Rust `discriminant()`), used
  for the deterministic element ID.

- `InternalElementId` — `"ie-" + 12 hex chars`, first 6 bytes of a **BLAKE3** hash of
  `(discriminant, text, page.unwrap_or(u32::MAX) LE, index LE)`. Port BLAKE3 (see
  Dependencies). IDs must match byte-for-byte for golden comparison of the structured model.

### The public result — `ExtractedDocument`

Rust: `crates/xberg/src/types/extraction.rs`. This is the public output type. Fields we
keep: `Content`, `MimeType`, `Metadata`, `ExtractionMethod` (Native/Ocr/Mixed — always
`Native` in the port), `Tables`, `DetectedLanguages`, `Images`, `Pages`, `Elements`
(element-based format), `DjotContent`, `Document` (DocumentStructure tree), `Uris`,
`Revisions`, `Annotations`, `Children`, `ProcessingWarnings`. Drop: chunks, embeddings,
ocr_elements, keywords, quality_score, llm_usage.

`ExtractionResult` is the batch envelope: `Results: List<ExtractedDocument>` + errors.

### Renderers

Rust: `crates/xberg/src/rendering/`. One function per output format, all consuming
`InternalDocument`:

| Format | Rust file | Notes |
|---|---|---|
| Plain | `plain.rs` | Concatenate element text, no formatting. |
| Markdown | `markdown.rs` (+ `comrak_bridge.rs`) | GFM. Rust renders an AST via comrak. |
| HTML | `html.rs`, `html_styled.rs` | HTML5. |
| Djot | `djot.rs` | Djot markup. |
| JSON tree | `json.rs` | Heading-driven section tree (`JsonDocument`/`JsonNode`). Port verbatim — it is simple and self-contained. |

`common.rs` holds shared walking state (container nesting, `is_body_element`,
`is_container_end`, `get_language`, `handle_container_end`) — port it first; all
renderers depend on it.

### Config

Rust: `crates/xberg/src/core/config/`. Port a trimmed `ExtractionConfig` with the fields
content extraction actually reads: `OutputFormat`, format-specific options (PDF, HTML,
Excel, email), `IncludeDocumentStructure`, image-extraction toggles, `ResultFormat`
(Unified vs ElementBased). Drop OCR/embedding/chunking/LLM config sections.

`OutputFormat`: `Plain` (default), `Markdown`, `Djot`, `Html`, `Json`, `Structured`,
`Custom(name)`.

### MIME detection & format registry

Rust: `crates/xberg/src/core/mime.rs`, `core/formats.rs`. Maps extension + magic bytes to
a canonical MIME type, which selects the extractor. Port the detection table and magic-byte
sniffing. Each extractor advertises the MIME types / extensions it handles; a registry
dispatches by MIME.

### Extraction pipeline

Rust: `core/pipeline/` + `extraction/derive.rs` (the `InternalDocument → ExtractedDocument`
derivation, ~1600 lines — includes page splitting, structure derivation, language detection).
Port the native-only path. `derive.rs` is large; port incrementally, guided by golden diffs.

---

## Dependency mapping (Rust crate ➜ C#)

| Rust crate | Purpose | C# replacement |
|---|---|---|
| `image` | Image decode/encode/resize | **SixLabors.ImageSharp** |
| (font metrics, PDF glyphs) | Font parsing | **SixLabors.Fonts** |
| `blake3` | Element IDs | Port BLAKE3 (small) or a vetted C# BLAKE3 package; must match bytes. |
| `serde`/`serde_json` | (De)serialization | `System.Text.Json` with custom converters for the tagged enums. |
| `zip` | OOXML/ODF/EPUB/iWork containers | `System.IO.Compression.ZipArchive`. |
| `quick-xml` / `roxmltree` | XML parsing | `System.Xml` (`XmlReader`/`XDocument`). |
| `calamine` | XLS/XLSX/ODS | Port reader logic on top of ZipArchive + XML (xlsx) and CFB (xls). |
| `cfb` | OLE compound files (doc/ppt/xls/hwp/msg) | Port a small CFB reader (no good maintained NuGet; ~500 lines). |
| `pdf_oxide` / `lopdf` | PDF | Largest effort. Port the reader or evaluate a permissive managed PDF lib; must be pure-managed. |
| `mail-parser` | EML/MSG | Port MIME parsing; `System.Net.Mail` is insufficient. |
| `html-to-markdown-rs` | HTML→Markdown | Port; or AngleSharp for parsing + custom MD writer. |
| `roxmltree`/`org`/`biblatex`/`biblib`/`dbase`/`unhwp`/`sevenz-rust2`/`tar`/`flate2` | misc | Port or find managed equivalents (see TODO per-format). |

> **Rule:** if a Rust crate dependency has no suitable managed C# equivalent, port it
> (into `src/Xberg/Internal/<name>/`). Prefer BCL types where they are faithful.

---

## Project layout

```
dotnet/
  Xberg.sln
  Directory.Build.props          # net10.0, nullable, implicit usings
  src/Xberg/                     # the NuGet library
    Types/                       #   InternalDocument, ElementKind, Metadata, Table, ExtractedDocument, ...
    Rendering/                   #   Plain / Markdown / Html / Json renderers + common
    Core/                        #   Config, MIME detection, format registry, pipeline, derive
    Extractors/                  #   one file/folder per format
    Internal/                    #   ported dependencies (Cfb, Blake3, Zip helpers, ...)
  tests/Xberg.Tests/             # xUnit unit tests (renderers, types, per-extractor)
  tools/Xberg.TestRunner/        # CLI: runs every test_documents fixture, diffs vs *-results-rust.json
  tools/xberg-reference-gen/     # Rust helper that produces the golden *-results-rust.json files
```

---

## Testing & validation strategy

1. **Golden reference generation (Rust):** `tools/xberg-reference-gen` walks
   `../test_documents`, runs the *original* Rust extractors in each output format, and
   writes `{filename}-results-rust.json` next to each fixture. The goldens are **generated
   locally, not committed** — `test_documents` is upstream's repo, and the goldens must be
   re-derived from whatever Rust revision you are syncing against anyway. Regenerate them
   whenever `crates/xberg` or the submodule pin moves. Format:

   ```json
   {
     "file": "docx/sample.docx",
     "mime_type": "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
     "success": true,
     "extraction_method": "native",
     "metadata": { ... },
     "tables": [ ... ],
     "detected_languages": ["en"],
     "content": { "plain": "...", "markdown": "...", "html": "...", "json": "..." }
   }
   ```

2. **C# test CLI (`Xberg.TestRunner`):** runs the C# extractor over the same fixtures and
   diffs against the golden JSON. Reports per-format match / mismatch and a summary. This is
   the primary parity signal — a format is "done" when its fixtures match (allowing for
   documented, intentional differences).

3. **Unit tests (`Xberg.Tests`):** port the Rust `#[cfg(test)]` cases (renderers have rich
   ones) as xUnit tests for fast, isolated feedback.

Exact byte-for-byte parity is the goal for plain/json; Markdown/HTML may differ in
whitespace where the Rust path uses comrak — document any deliberate normalization.

---

## Re-syncing after an upstream merge

The Rust tree under `../crates` is deliberately left untouched, so merging upstream is
clean and the whole job is re-deriving the C# port's behaviour. The loop that works:

1. **Merge upstream, then materialize the corpus.** The `test_documents` submodule is
   LFS-free: text fixtures are in git, but every binary (office, PDF, epub, images) lives
   in a public bucket listed in `corpus.lock.json`. Fetch them first, or the office and
   PDF fixtures silently do not exist:

   ```sh
   git submodule update --init --depth 1 test_documents
   python3 test_documents/scripts/fetch_corpus.py     # ~580 MiB, re-runnable
   ```

2. **Regenerate the goldens against the merged Rust.** This is the whole point — the
   goldens encode current upstream behaviour, so the diff against them *is* the list of
   upstream changes that still need porting:

   ```sh
   cargo build --release --manifest-path tools/xberg-reference-gen/Cargo.toml
   tools/xberg-reference-gen/target/release/xberg-reference-gen ../test_documents
   ```

   It skips fixtures that already have a golden, so pass `--overwrite` after a Rust bump.

3. **Measure, then triage by cluster, not by fixture.** `--cluster` groups plain-text
   mismatches by the text at their first divergence, which turns "410 markdown fixtures
   fail" into "395 of them diverge at the same smart-quote character":

   ```sh
   dotnet run --project tools/Xberg.TestRunner -c Release -- ../test_documents --ext md --cluster
   dotnet run --project tools/Xberg.TestRunner -c Release -- ../test_documents --ext docx --diff --show 3
   dotnet run --project tools/Xberg.TestRunner -c Release -- --dump-metadata ../test_documents/x.pdf
   ```

4. **Fix against the Rust source, not against the golden.** Read the current Rust for the
   behaviour, port it, then confirm the numbers move. A golden tells you *that* something
   differs; only the Rust tells you what the rule is.

5. **Expect unit tests to fail where upstream changed behaviour.** A red test that pins
   the old behaviour is the correct outcome — update it to the new rule and say so in the
   commit, rather than working around it.

---

## Porting order (see `TODO.md` for the full checklist)

1. **Core spine:** types, renderers, config, MIME, registry, minimal pipeline. Validate with
   text/markdown/csv/json fixtures (no heavy deps).
2. **Office (priority):** docx, xlsx, pptx, odt, doc, ppt, rtf, epub.
3. **Structured & markup:** html, xml, json/yaml/toml, csv, ods, jats, docbook, opml.
4. **Email & archives:** eml, msg, pst; zip, tar, 7z, gzip.
5. **Remaining:** pdf, images (metadata/exif only, no OCR), hwp/hwpx, iwork, latex, rst,
   org, typst, bibtex, fictionbook, jupyter, dbf, mdx.
6. **Code files (lowest priority):** tree-sitter equivalent — only after everything above
   is ported and green.

## Conventions

- Match Rust field/variant names in the serialized JSON (snake_case) via
  `JsonSerializerOptions` / `[JsonPropertyName]` so golden diffs are meaningful.
- Keep extractor logic close to the Rust source; cite the Rust file at the top of each C#
  extractor so re-syncing after upstream changes is mechanical.
- No `unsafe`, no P/Invoke to native libs — pure managed so the NuGet package is portable.
