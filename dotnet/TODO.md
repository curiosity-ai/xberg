# Xberg C# Port — Work Breakdown

Legend: `[ ]` todo · `[~]` in progress · `[x]` done · `[-]` out of scope (dropped)

Each format is "done" when the `Xberg.TestRunner` output matches the locally generated
`{filename}-results-rust.json` golden files for its fixtures (documented deviations allowed).
See "Re-syncing after an upstream merge" in `Claude.md` for how to regenerate them.

> **Status.** Goldens are generated for **3165 fixtures**, the corpus having grown when the
> August upstream merge advanced `test_documents` — the new ones are maths-heavy HTML/XML and a
> large `office/regression` set of real-world HTML.
>
> **2456 fixtures of 2942 (83.5%) match on every hard dimension**; **0 catastrophes**; 4
> fixtures still lose content (svg, eml, epub, odt). 1075 unit tests.
>
> The largest single pass since: **pdf_oxide's text pipeline is ported** — fonts, content
> stream, span assembly, reading order — and PDF spans now come from it. See "The PDF gap,
> measured".
>
> The last two passes took it from 2340 and were mostly a matter of finding things the port had
> never implemented at all rather than tuning what it had. Ordered by what they were worth:
>
> - **Plain text (txt 957 → 989 of 1008, plain 991/991).** The text extractor builds its
>   document directly, which bypasses the paragraph splitter downstream, so nothing normalized
>   its line endings: a CRLF document came out as one paragraph carrying every carriage return.
> - **Markdown table shape (md 718 → 741 of 782, tables 781/782).** A GFM table's header fixes
>   its width and every other row is squared against it. Emitting rows at whatever width they
>   had put a row's second value under the second heading in a four-column table.
> - **XML entity references (xml 8 → 14 of 15, svg 30 → 40 of 41).** The tokenizer split text
>   at a reference and dropped it, so `Trinidad &amp; Tobago` became two paragraphs, neither of
>   them a country. That modelled raw quick-xml; upstream wraps it in an `EntityReader` that
>   coalesces and resolves, and the port had the first half without the second.
> - **RST (2 → 13 of 15).** Every table was emitted twice — upstream removed the second raw
>   pass, the port still had it — and an unhandled directive's body was dropped rather than kept
>   as the text it is.
> - **PDF metadata (324 → 359 of 388).** XMP was missing from the port entirely; a scanned
>   confidence widened an `f32` differently from serde; and pdf_oxide's page classifier never
>   reports JBIG2, so naming it scored four scanned fixtures 0.10 high.
> - **Three formats that had no extractor at all**: AsciiDoc (0 → 6/6), ODP (0 → 5/5) and
>   WebVTT (0 → 1/1), plus Quarto and R Markdown, which reached no extractor because the
>   markdown extractor claimed six of its ten MIME types.
> - **CommonMark fence indentation, YAML number lexemes, ODF list styles, heading attribute
>   blocks, display-math line structure** — each a small rule with a document-shaped consequence.
> - **docx (40 → 46 of 46, every dimension).** Reviewer comments were missing end to end; a text
>   box's inner paragraphs were emitted as document structure, so a numbered `w:p` inside a shape
>   became a list in the body; and page boundaries were measured against text that dropped every
>   equation, which put both boundaries of a maths-heavy document 700 bytes short.
> - **HTML catastrophes 7 → 4.** The DOM builder had implied-close rules for list items but none
>   for tables, so `<table><td>…` left every cell hanging off the table where no consumer looks
>   for it. On one fixture that table held 99% of the document. Content losses fell from 327K
>   characters to 245K.
> - **PDF tables in the structured document.** The markdown and HTML renderers were fed a
>   document with no tables in it at all. Upstream reaches them through a layout detector with a
>   geometric fallback; the reference outputs were generated without the detector, so the
>   fallback is the whole path — and it needs nothing but the words on the page. 163 fixtures had
>   a table in the reference markdown and none in ours; 132 now.
> - **PDF table guards (218 → 222, ok 100 → 103).** The well-formedness gate was a simplified
>   version of upstream's, rejecting dense numeric ledgers and accepting reflowed paragraphs.
> - **HTML entities and comment-prefixed pages.** The markdown converter knew forty entity names
>   where upstream's HTML5 parser knows 2125, and a page whose first line is a comment was
>   handed to the XML extractor as a tag outline.
> - **HTML metadata (43 → 54 of 157).** The document scanner was a rough approximation of the
>   collector it mirrors. It stopped at the first `</style` written with whitespace before its
>   bracket, swallowing the rest of the page; it collected navigation chrome as document
>   structure; it read head metadata from anywhere rather than the head; it flattened a
>   heading's markdown to text; and it understood none of Dublin Core. Author mismatches went
>   from 17 fixtures to none, meta tags from 29 to 5, titles from 25 to 12.
>
> ## Upstream defects, flagged and left alone
>
> Per the standing instruction to flag rather than reproduce an incorrect upstream extract:
>
> - **UTF-16 XML** (`vendored/unstructured/xml/factbook-utf-16.xml`). Upstream ignores the
>   byte-order mark and reads the file as UTF-8, emitting `? x m l` with the NULs as spaces.
>   This port decodes it. One fixture, permanently red.
> - **Escaped markup in XMP `dc:description`.** quick-xml emits an entity reference as its own
>   event, splitting the text run, so upstream keeps only the first fragment — `div` for a value
>   that is a whole HTML document. This port returns the value. Four PDF fixtures.
> - **Inherited `/MediaBox`** (`pdf/pdfa_034.pdf`). ISO 32000-1 §7.7.3.4 inherits from the
>   nearest ancestor that defines the attribute; on a document with two nested `Pages` nodes
>   upstream reports the root's A4 box where the page's own parent says Letter.
> - **AsciiDoc math macros.** Upstream's current source converts `latexmath:`/`asciimath:`/
>   `stem:` and `[latexmath]` + `++++` blocks; the reference outputs predate that and carry them
>   verbatim. The port matches the references, which is also what the AsciiMath path can do
>   without a converter it does not have.
> - **`H2SO4` on `pdf/embedded_images_tables.pdf`**, where upstream reads `H SO4`.
> - **Duplicated body text on `office/regression/000_000213.html`.** A 7 KB page extracts to
>   39 KB upstream, with one paragraph repeated ten times. The page leaves `<p>` and `<a>`
>   unclosed across blocks, and html5ever's adoption-agency algorithm reparents the formatting
>   element into each block it spans. This port's simpler tree builder does not, so it extracts
>   the page once. Counted as an under-extraction by the harness; it is not one.
> - **An empty bullet before every list item on `pdf/multi_page.pdf`.** Upstream emits `- ` with
>   nothing after it, then the item's text as a separate bold paragraph, wherever a bullet glyph
>   is its own text run. *No longer flagged, and no longer red*: this is not a special case
>   upstream added, it is what `blocks_to_paragraphs` produces — the bullet and the bold text are
>   separate spans, the gap between them is too wide for `is_inline_style_transition`, so the
>   weight change breaks the paragraph and the lone bullet finalizes as a contentless list item.
>   The port emits the same thing now because it ports that rule, not because it special-cases
>   the shape. The fixture matches byte for byte.
> - **Markdown loses the spaces that the same golden's plain text keeps** (`pdf/copy_protected.pdf`).
>   One file, two answers: `content.plain` reads
>   `Keywords: Document Image Analysis · Deep Learning · Layout Analysis` and `content.markdown`
>   reads `Keywords:DocumentImageAnalysis`, from the same document and the same spans. The two
>   paths ask different questions. `assemble_page_text` (`pdf/oxide/text.rs`) uses pdf_oxide's own
>   space decision, with char-level context and TJ offsets; `extract_text_and_annotations`
>   (`structure/assembly.rs`) joins two segments only when `segments_need_space`
>   (`structure/lines.rs`) says so, which for two same-style segments on one baseline reduces to
>   `advance_gap > next.font_size * SEGMENT_GAP_SPACE_RATIO` (0.15). That ratio exists to rejoin
>   *kerning-run* splits inside one word (`eli` + `t`); where a producer sets every *word* as its
>   own tight span it fuses the words instead, and the markdown contradicts the plain text of the
>   very same extraction.
>
>   `segments_need_space` is ported faithfully — the port reaches the right answer because its
>   own span geometry for this file leaves real gaps where upstream's does not, so the same rule
>   decides differently. Reproducing upstream here would mean degrading the geometry on purpose.
>   Permanently red on markdown and html.

## Known gaps after the merge

Ordered by corpus impact. Each is a real upstream behaviour the port does not yet reproduce —
not a cosmetic difference.

- [x] **PDF metadata (388 fixtures).** Was 0/388, now 359/388. Four causes, all fixed: the
      missing scanned-detection fields (`pdf/scan_detect.rs` is ported as
      `Internal/Pdf/PdfScanDetect.cs`, fed by a dedicated content-stream pass); serde's float
      spelling, where an integral `f32` is `0.0` and not `0`; the *width* of that float, since
      Rust widens an `f32` to `f64` before printing and 0.85 becomes `0.8500000238418579`; and
      XMP (ISO 32000-1 §14.3.2), which the port had not implemented at all — the richer of a
      PDF's two metadata channels and the only one many modern producers write.

      Two scan-detection differences came from reading pdf_oxide rather than guessing: its page
      classifier asks each image only whether it carries CCITT parameters and whether its data
      decodes as JPEG, so a JBIG2 image is `Other` and never earns the bilevel bonus; and the
      score terms accumulate in single precision, so 0.50 + 0.35 + 0.05 is 0.90000004.

      The remaining 29 are the flagged upstream defects above (the `dc:description` fragment
      and the inherited `/MediaBox`) plus genuine scan-signal differences.
- [ ] **PDF content (389 fixtures, 272 fully matching; plain 356/388, markdown 215/388,
      html 206/388, json 315/388, tables 317/388; content-identical 95.1%, 0 catastrophes).**
      The largest remaining area, and upstream landed 127 PDF commits in this window. This is
      extraction *quality*, not a missing feature, and it does not decompose into a few
      systematic fixes — measured, not assumed:
      - Plain-text divergence spreads across **216 distinct first-divergence clusters** for
        the failing fixtures, i.e. mostly reading-order and whitespace, one document at a time.
      - Tables: of the mismatches only a minority are "we found no table at all" (the missing
        ruling-line/bordered-grid tier). Most are cell-segmentation differences.
      - The structured document now emits tables (see "The PDF gap, measured" below): the
        geometric region fallback and its reconstruction path are ported, which took the count
        of fixtures whose reference markdown has a table and ours has none from 163 to 132.

      **The structure pipeline is now much closer to upstream's.** Ported in the last pass:
      `sparse_multi_page_heading_map` and `has_repeated_sparse_peer_heading_tier` (a font tier
      repeated at the top of several pages is peer sections, and repetition is evidence the
      block count cannot supply); the baseline-advance paragraph break, which is what a blank
      line actually produces — the whitespace-band rule is blind to it, since with leading of
      1.1–1.3 glyph heights a blank line only leaves 1.2–1.6 and the threshold is 1.5; the
      continuation-merge gates (bold boundary, vertical distance, numbered section heading),
      without which every bold lead-in was absorbed into the paragraph after it before anything
      could classify it; the segment-level text repair chain; `split_colon_semicolon_run_in_lists`
      and `compact_final_heading_hierarchy`.

      Still unported from `pdf/structure/`: `mark_validated_page_numbers` (`page_number.rs`, 965
      lines), `recover_headings_from_outline` (needs PDF bookmarks), `stitch_fragmented_tables`,
      `merge_spatial_footnote_markers`, `suppress_table_dominant_paragraph_spill`, and everything
      that depends on layout regions (ML).

      **Where the remaining plain gap actually is.** Upstream reaches text through `pdf_oxide`,
      a 62k-line native library, and the divergence is that extractor's *geometry*, not a
      missing xberg module. **Transplanting its assembly loop does not work** — this was
      measured three ways and all three were reverted:
      - The whole break cascade from `Document::extract_text_column_aware`, faithfully ported:
        plain 122 → 72.
      - `same_line_threshold` alone (`max(min_fs*1.2, max_fs*0.3)`, replacing our
        `max(height, fs*0.5)*0.5`): plain 122 → 100.
      - Its same-line font-transition space rule alone: plain 122 → 121.

      The reason is structural: pdf_oxide's loop runs over spans its own merger produced, and
      ours runs over spans ours produced. Its constants are calibrated to that granularity and
      do not transfer. Our loop compensates differently (a row-reset rule and a tighter y
      tolerance) and is at a local optimum for the spans it actually sees. Closing this gap
      means porting the span merger first, not the loop that consumes it.

      Three passes from pdf_oxide *were* ported successfully and are correct but score-neutral:
      - Ligature expansion for encoding-derived mappings (never ToUnicode — a ToUnicode CMap is
        the font's own statement and is taken at its word). The embedded Type 1 program's own
        `/Encoding` array is parsed now, which is where a TeX font declares its ligature slots
        (codes 11-15, which no named encoding assigns): spurious ligatures across the corpus fell
        from 55 to 13, the remainder being CFF fonts whose encoding lives in `/FontFile3`.
      - `merge_sub_superscript_spans`, which reattaches raised and lowered runs to the words
        they modify. On `pdf/embedded_images_tables.pdf` this makes our output *better* than
        the golden — we produce `H2SO4` where upstream produces `H SO4`.
      - The inter-span space threshold, judged against the larger of the two spans.

      `reorder_same_line_runs` was ported too and **reverted**: it costs two fixtures, because
      it assumes upstream's span ordering and ours differs going in.

      A caution learned the hard way: `pdf/structure/text_repair.rs`'s element-level chain
      belongs *only* to documents the structure pipeline assembled. Applying it to the flat
      native-text split as well cost 33 of the 114 matching plain fixtures. The *segment*-level
      chain, which upstream runs inside `segments_to_paragraphs`, is a different pass and is
      now ported.
- [x] **DocTags ingestion.** Not a new format after all: upstream types these by content, and
      `*.doctags.txt` reached the plain-text extractor only because the port let the extension
      decide. Fixed by the extension/content change; the fixtures now route as markup.
- [x] **TOML key ordering (now 5/5).** Rust's `toml::Value::Table` is a `BTreeMap`, so keys are
      emitted in sorted order where the C# parser preserved file order; datetimes also needed the
      `$__toml_private_datetime` wrapper that toml→serde_json produces, while the flattened view
      keeps them plain. Both done.
- [x] **pptx metadata (was 0/11, now 11/11).** Not slide numbering: `app.xml`'s `TitlesOfParts`
      is one flat vector concatenating fonts, theme and slide titles, and `HeadingPairs` says
      how many entries each group owns. Taking it whole put the font list in every
      presentation's slide titles. Chart and SmartArt frames are followed now too.
- [x] **Reviewer comments (`CommentRef` / `CommentDefinition`).** Upstream gave docx comments
      their own element kinds (GH#300), which the C# `ElementKindTag` did not have. Both kinds
      now exist and are wired through the plain, JSON and markdown/HTML renderers: a
      `w:commentReference` writes a `[cmt:N]` marker into the run text, the assembled paragraph
      text is scanned for those markers (the same post-hoc scan upstream uses, which also
      recovers `[^N]` footnote references and a run's hyperlink URIs), and `word/comments.xml`
      supplies the definitions. Comment bodies render at the end like footnote definitions
      rather than being dropped.
- [x] **Email attachment text.** Attachments now go back through the pipeline and contribute a
      level-2 heading plus their text (eml 20/43 → 39, msg 4/16 → 14). Images are deliberately
      excluded: what this port recovers from one is EXIF, which belongs to the attachment's own
      metadata rather than the message body.
- [x] **Embedded message attachments (`afEmbeddedMessage`).** Such an attachment has no binary
      stream — the message is a storage to descend into — so reading only the data stream gave a
      zero-byte attachment and no text. Two further defects surfaced with it: a message's own
      recipients and attachments were gathered by walking the whole container, so an outer
      message claimed the inner one's as well; and the property-stream header is 24 bytes for an
      embedded message where it is 32 at the top level and 8 for an attachment storage, three
      sizes the port had collapsed into two. msg 4/16 → 14/16, the last failure being PDF text
      quality inside an attachment rather than anything about email.
- [x] **Markdown math (md 671/775 → 691).** Inline `$…$` keeps its delimiters in the text;
      display `$$…$$` becomes a Formula element with them stripped. The delimiter rules are
      pulldown-cmark's, read from its source rather than guessed: a run of one `$` is inline and
      two is display, an opening delimiter must not be followed by whitespace (so a price in
      prose cannot open a span), an inline span closes on a `$` not preceded by whitespace, and
      a display span closes only on another `$$`. Not ported: the brace-context rule that lets
      `$}$` stay literal.
- [x] **Citation formats (nbib, ris).** Both at full parity. The parsers were fine; the element
      carried only the title, so everything else was parsed and then dropped.
- [~] **html 33/157 (was 27).** Two defects fixed: cells
      were placed by advancing through each row on its own, which ignores the columns a rowspan
      from an earlier row still covers and slides everything beneath one out from under its
      header (upstream keeps that placement rule in one helper, `grid_flatten.rs`, so the
      geometry cannot drift between formats, and this port now does too); and every table was
      recorded twice, a second unreferenced copy upstream had already removed. Two more
      recovered documents that produced *nothing*: `<body>` now closes an unterminated
      `<head>` rather than the head running to the last byte, and a document that yields no
      elements at all falls back to the loose text it gathered.

      **The 117 `office/regression` fixtures are the real remaining work**, and 4 of them are
      still catastrophes (7 before the table-cell fix below). Diagnosed, not guessed:
      - A cell written straight into a `<table>` with no `<tr>` around it left every cell
        hanging off the table where no consumer looks for it. The HTML5 in-table insertion
        modes are implemented now; that alone fixed three of the seven catastrophes.
      - **The structured document is built by the wrong walker.** `HtmlWalker` ports
        `extraction/html/structure.rs`, which upstream uses for *email and epub* — its own HTML
        extractor maps `html-to-markdown-rs`'s `DocumentStructure` instead. That is why plain
        and json sit at 38 and 40 of 157 while markdown, which does go through the converter,
        is at 78: a `<br>` reaches the reference as a markdown hard break (`"  \n"`), and the
        structure walker has no notion of one. Closing this means teaching the ported converter
        to emit a document structure (~1500 lines upstream) and mapping that instead.
      - The walker captures a whole `<table>` subtree and hands it to the markdown converter,
        while upstream's structure walker handles tables inline with its own cell accumulator.
        So anything inside a layout table — and 1990s HTML puts the entire page inside one —
        goes through cell rendering. A `<pre>` in a cell comes out fenced and dedented
        (` ```…``` ` in *plain* output, indentation collapsed) where upstream emits a Code
        element carrying the raw indented text. `000_000448.html` loses three quarters of its
        content this way. Fixing it means giving the walker its own table path rather than
        delegating.
      - Malformed markup gets no error recovery. `000_000190.html` has 5 `<tr>` opens against
        55 closes; upstream's parser synthesises the rows anyway, ours finds 3 tables where it
        finds 7.

      **Metadata** is at 54 of 157, up from 43. What remains is concentrated in three
      collections — links (100 fixtures), images (70) and headers (36) — and all three now turn
      on things a linear scanner cannot reproduce: the DOM depth the tree builder computes, and
      the order in which a table's contents repeat across the converter's passes. The
      field-level rules themselves are ported (Dublin Core, social-card keys, head-only
      collection, preformatted skipping, heading markdown).

      Loose text outside a `<p>` is still dropped, and that is deliberate: flushing at every
      block boundary is what upstream's `flush_paragraph` does, but it was measured twice —
      once on the 41-fixture set and again on the 157 — and costs far more than it fixes
      (27 matching → 15). This walker buffers text in places upstream does not; until that is
      reconciled the narrow no-elements fallback is the honest fix.
- [x] **odp 0/5 → 5/5.** There was no ODP extractor: every presentation fell through to the
      ZIP archive lister. See `Internal/Odf/OdfPresentationParser.cs`.
- [ ] **mdx 0/5.** Not yet triaged; use `--cluster`.
- [x] **txt 957/1008 → 989.** The text extractor builds its InternalDocument directly, which
      bypasses the paragraph splitter downstream, so nothing normalized its line endings and a
      CRLF document came out as one paragraph carrying every carriage return.
- [x] **rst 2/15 → 13.** Every table was emitted twice (upstream removed the second raw pass;
      the port still had it), and an unhandled directive's body was dropped instead of kept.
      A directive and a comment are told apart by shape: a directive's name is a single word
      immediately followed by `::`.
- [x] **xml 8/15 → 14, svg 30/41 → 40.** Entity references were treated as boundaries and
      dropped. Upstream wraps quick-xml in an `EntityReader` that coalesces the surrounding text
      and resolves the reference; the port modelled the splitting without the coalescing.
- [x] **yaml 4/10 → 8.** A number now keeps the lexeme it was written with, so a 64-bit hash
      above `long.MaxValue` stays exact and `397.0` stays a float. A failed parse falls through
      rather than throwing out of the extractor, which had been losing four documents whole.
- [x] **markdown tables (761/782 → 781).** A GFM table's header fixes its width; short rows are
      padded and long ones truncated, so a row's nth value stays under the nth heading.
- [x] **AsciiDoc, ODP, WebVTT, Quarto, R Markdown.** Five formats that reached no extractor.
      The first three had none written; the last two were unclaimed MIME types.
- [x] **typ 0/8 → 7/8.** Five separate missing branches; the last fixture needs `@label`
      reference resolution, which is a feature rather than a fix.

### The PDF gap, measured

PDF was where the remaining distance was: 106 of 389 fixtures matched on every hard dimension,
and the shortfall was not one bug but three layers. **Two of the three are now closed.**

- [x] **Span assembly (plain 123 → 172 of 388).** The single biggest cause, and the reason the
      other two were hard to see past. pdf_oxide's text layer merges glyph runs into spans on its
      own thresholds, and every downstream rule — spacing, reading order, paragraph breaks, table
      cell assignment — is calibrated to that granularity. Three transplants of individual
      constants onto the port's own merger were tried and each regressed the corpus (plain
      122 → 72, → 100, → 121); all were reverted. What closed it was porting the producer:
      `extractors/text.rs` (9.2k lines of logic) with the font, content-stream and reading-order
      layers under it, now in `Internal/PdfOxide`. Fully matching 106 → 142, json 118 → 163,
      content-identical 35.0% → 49.1%, real content misses 20 → 12.
- [x] **Table recognition beyond the geometric fallback (tables 222 → 237 of 388).** The
      intersection pipeline both ruling-line tiers share is ported — edges, snap/join, dotted-line
      reconstitution, coverage filtering, cells, union-find grouping, extended grids, span
      assignment, section-divider splitting — along with the drawn-rule admission gate the
      borderless heuristic was missing. `regions/table_recognition.rs` (2412 lines) is the
      ML-only tier and stays out of scope while the reference runs without the detector.
- [x] **Font programs other than Type 1.** `cff_encoding.rs` is ported, so a CFF subset font's
      own encoding — and the ligature slots in it — are read from `/FontFile3` rather than
      guessed at.

**What the PDF port still owes.** The ruling-line tiers read their drawn paths from the older
content interpreter, which is why both span producers still run per page; the paths belong in
the ported pipeline. `PageText.chars` is left empty, since page text is assembled from spans
alone. Pattern marking is a no-op, and that is faithful rather than a gap: its only caller is
`process_tj_array_primary`, which runs solely under `WordBoundaryMode::Primary`, and both
pdf_oxide and xberg leave the mode at `Tiebreaker`. Porting `pattern_detector.rs` would be 400
lines nothing reaches. And `.Showing.cs` re-implements three rules — the MCID scope, the artifact type and
the content-suppression test — that `.Core.cs` also owns; they agree today and should be one
copy. Still unported from `pdf/structure/`: `stitch_fragmented_tables`,
`merge_spatial_footnote_markers`, `suppress_table_dominant_paragraph_spill`,
`demote_structure_annotation_headings`, `split_embedded_list_items`, the paragraph-level
`dehyphenate_paragraphs`, and everything that depends on layout regions (ML).

**Measured, on the same corpus and the same base.** Going span-granular and finishing the
structure pipeline's own passes took the PDF numbers from `219 ok / 356 plain / 62 md / 60 html
/ 315 json / 382 meta / 239 tables` to `272 / 356 / 215 / 206 / 315 / 382 / 317`, with
content-identical unchanged at 95.1% and catastrophes at 0. Plain, json and metadata are
untouched by design: they do not read these segments at all. The order the levers landed in was
`finalize_paragraph` (+9 md), `synchronize_paragraph_text_metadata` (+3), the span producer
(+39 md, +69 tables), `segments_need_space` and `mark_validated_page_numbers` together (+83 md),
and `list_item_is_ordered` (+14 md). Beware measuring any of this on a loaded machine: the
per-document deadline in `PdfExtractor` drops whole pages under CPU contention, which moves
plain and content-identical by one or two fixtures between otherwise identical runs.

**The hierarchy segments are now span-granular** (`Internal/Pdf/PdfOxideSegments.cs`), which
was the single largest thing standing between the port and upstream's structured output. It
sat upstream of two gaps at once, because heuristic tables and heading classification both read
these segments. The port used to emit one `SegmentData` per assembled *line*; upstream emits
one per pdf_oxide *span*, and a line is simply the wrong unit — a heading, a bold lead-in and
the prose that follows it share a paragraph and must still come out as separate styled runs.
On `pdf/multi_page.pdf` upstream writes `**IBM MT/ST (Magnetic Tape/Selectric Typewriter)** :
Introduced in 1964…`, a bold run, a geometry-derived space and a non-bold run inside one
paragraph, which a line-granular segment cannot represent at all. The same coarseness showed
up in table cells: a header upstream splits into `(`, `Ω`, `)` arrived as one `(Ω)` token and
fell into the next column over. Ported with it: the `TopToBottom` row-band order the structure
path asks for (*not* the column-aware order the text path uses), `reorder_page_reading_order`
with its dense-repair short-circuit, `select_reading_order` and its gutter/prose/balance gates,
`rejoin_inline_scripts`, artifact filtering and `dedupe_redrawn_segments`.

`SegmentData.rotation_degrees` is still absent, so the four places upstream asks
`has_same_rotation` — the redraw dedupe, the inter-segment space rule, the paragraph break and
the continuation merge — treat a rotated run as if it shared the page axis, and
`order_segments_in_reading_frames` (which sorts each rotated run in its own upright frame) has
no equivalent. Rotated pages are the only ones affected.

**The structure-tree path is still absent, and that is the remaining half of this note.**
`extract_all_segments` tries `extract_segments_with_structure_tree` *first* and only falls back
to font-size clustering when `mark_info().is_structure_reliable()` fails or the tree carries
fewer than three headings; on a tagged PDF the port infers what upstream reads. It is a
different pipeline branch, not a producer tweak: it populates `SegmentData.assigned_role`, and
`process_single_page` then takes the `struct_paragraphs` arm through `classify_paragraphs`
(`classify.rs`), which the port does not have. 74 of the 389 corpus PDFs carry a
`/StructTreeRoot` — an upper bound on the yield, not the yield, since the reliability and
three-heading gates cut into it and none of the currently failing markdown fixtures has been
attributed to it.

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
      lists, hyperlinks, images, headers/footers, tracked changes, text boxes, reviewer
      comments. 46/46 on every dimension.
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
- [ ] **Inference performance.** ~0.95 s per 640x640 page on 4 cores against ONNX Runtime's
      ~0.51 s measured in the same session — roughly 1.8x, down from 16x. The matrix multiply
      is structured after MLAS (packed cache-line-aligned operand panels, a twelve-row AVX-512
      register block whose accumulators stay in registers) and convolution is an implicit GEMM
      that never materialises its receptive fields. What moved it, what was measured and
      rejected, and how to measure anything at all on a VM whose host throughput moves by 2x
      between runs, is recorded in `tools/onnx-parity/README.md`.

      The routes still open, in rough order of expected value: a direct convolution for
      small-channel layers, where the nine-fold im2col expansion stops paying for itself;
      in-place unary operators, since the session already knows which values die at each node;
      and a specialised max-pooling path. Past those it is what C# cannot express — prefetch
      hints and hand-scheduled assembly — plus MLAS's per-CPU kernel variants.
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
