# Xberg C# Port — Work Breakdown

Legend: `[ ]` todo · `[~]` in progress · `[x]` done · `[-]` out of scope (dropped)

Each format is "done" when the `Xberg.TestRunner` output matches the locally generated
`{filename}-results-rust.json` golden files for its fixtures (documented deviations allowed).
See "Re-syncing after an upstream merge" in `Claude.md` for how to regenerate them.

> **Status.** Goldens are generated for **3165 fixtures**, the corpus having grown when the
> August upstream merge advanced `test_documents` — the new ones are maths-heavy HTML/XML and a
> large `office/regression` set of real-world HTML.
>
> **2755 of 2787 comparable fixtures (98.9%) match on every hard dimension**; **0
> catastrophes**; 1401 unit tests. Only **seven** of the 32 failures are outside PDF, and six of
> those are the flagged upstream defects; the seventh is `epub/features.epub`, whose golden
> predates the converter it is measured against (traced below). The denominator is the fixtures
> Rust itself can extract —
> 155 of the 2,942 walked, it cannot, and counting those against this port measures nothing.
> Measured on the whole corpus in one run, which is the only figure that means anything: a
> per-format run validated the mime sniff on md and txt while it was quietly handing three PDFs
> to the HTML extractor.
>
> Read the last digit with care. `PdfExtractor` enforces a per-document wall-clock deadline and
> drops whole pages when it trips, so a loaded machine moves the total by one to three fixtures
> between runs of identical code — two consecutive runs here gave 2639 and 2638, and PDF `ok`
> 274 and 273, with nothing changed in between. Treat a single-fixture move as noise and measure
> a real change on a quiet machine.
>
> The largest single pass since: **pdf_oxide's text pipeline is ported** — fonts, content
> stream, span assembly, reading order — and PDF spans now come from it. See "The PDF gap,
> measured".
>
> The last two passes took it from 2340 and were mostly a matter of finding things the port had
> never implemented at all rather than tuning what it had. Ordered by what they were worth:
>
> - **Markdown and plain text (md 737 → 767 of 775, txt 955 → 956).** Fifteen CommonMark/GFM
>   rules the port's pulldown-cmark stand-in had never had — lazy continuation, marker width,
>   definition lists, wikilinks, GFM alerts, the WHATWG sniffing table — each read out of
>   pulldown-cmark's own source first. Nine md fixtures were worth more than one rule apiece;
>   lazy continuation alone was worth eight. See the md/txt entry under "Known gaps".
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
> - **`<h0>` from a truncated heading depth**
>   (`vendored/docling/groundtruth/docling_v1/multi_page.doctags.txt`). `extractors/xml.rs`
>   computes a heading's level as `((depth as u8) + 1).min(6)` from a `u16` depth. A DocTags
>   stream opens `<page_4><loc_15>…` tags it never closes, so the depth climbs past 255; the
>   `as u8` truncates and `255u8 + 1` wraps to 0, so `.min(6)` returns 0. Upstream then writes a
>   heading with no `#` at all in markdown (` loc_14`) and `<h0>loc_14</h0>` in HTML, and
>   restarts at `#` for the next tag. This port clamps at six. One fixture, permanently red on
>   markdown/html/json.
> - **Stale goldens from corpus drift** (`ATTRIBUTIONS.md`, `LICENSES.md`,
>   `scripts/corpus-patterns.txt`). These three fixtures are `test_documents`' own documentation
>   and they grew after their goldens were generated — the golden for `LICENSES.md` stops at
>   "Vendored sources: 6." where the file now says 7, and this port extracts the rest of the
>   file faithfully. Nothing to fix; they pass again whenever the goldens are regenerated.
>
> ## The goldens are 3.10.6 output, and the lock has moved past them
>
> An earlier note here concluded these HTML goldens were stale and should be regenerated. They
> are not. The committed `-results-rust.json` files were all written in one run on Aug 12 by
> `dotnet/tools/xberg-reference-gen/target/release/xberg-reference-gen`, and that binary links
> **html-to-markdown-rs 3.10.6** (`strings` on it, and the only version under the vendored
> registry). Copy `html/sinthgunt.html`, `html/hip_13044_b.html` or
> `vendored/docling/html/wiki_duck.html` into a scratch directory, run that binary over it, and
> every content dimension, `tables` and the metadata come back identical to the committed file.
>
> What moved is `dotnet/tools/xberg-reference-gen/Cargo.lock`: it records **3.11.2** today, and a
> rebuild would resolve that instead. 3.11.2 is a different converter — `converter/main.rs` gained
> a `"template" | "noscript" => {}` arm that 3.10.6 does not have, so a regenerated golden drops
> `<noscript>` content — which is what the earlier "regenerated references differ" experiment was
> seeing. **Port against 3.10.6 while these goldens stand.** Whoever regenerates the corpus has to
> re-port the arms 3.11.x changed, `noscript`/`template` first.
>
> A corpus-wide regeneration is also **not safe to run as-is**, and was attempted and rolled back.
> Two reasons. First, `xberg-reference-gen` dies partway through: `mathemascii-0.4.0` panics on a
> char boundary (`end byte index 6 is not a char boundary; it is inside '≤'`) parsing asciimath
> reached through `extractors::asciidoc`, and the generator has no panic guard, so it aborts and
> leaves the goldens half-old and half-new. Second, a full run also *creates* ~222 goldens for
> fixtures the Rust extractors fail on, which the committed corpus deliberately omits — that
> changes the denominator and silently redefines every percentage in this file. Restore from a
> backup taken first (`tar czf` over `*-results-rust.json`), and delete anything the run added
> that the backup did not have.

## Every failing fixture, categorized

The corpus is 2,942 fixtures. **155 of them Rust itself cannot extract** (75 where this port is
also empty, 80 where it produces output and Rust errors), so they are not comparable in either
direction. That leaves **2,787 comparable fixtures, 2,732 fully matching (98.0%)** and **55
failing at least one hard dimension** — 54 once the docbook fix that landed after the sweep is
counted, and 50 once the four PDF fixtures the path-operator and rotated-frame fixes below closed
are taken off it (the whole-corpus figure has not been re-measured since; the PDF column has,
per format). 0 catastrophes. Regenerate the list with `--list-fail`, which prints one line per
failing fixture and the dimensions it failed.

Older notes in this file quote percentages against 2,942. Those divided by a total including the
incomparable fixtures and so scored this port against documents upstream never read.

**PDF is 28 of the remainder**, and the corpus double-counts: `nougat_033` is the same document
as `pdfa_008`, `nougat_046` as `pdfa_021`, and `issue-140-example` and `pr-138-example` each
appear twice, so those 28 are roughly 25 distinct documents.

`tables` is the largest lever again, not `plain` — `json` never fails alone, and seven fixtures
fail nothing hard but `tables` (plus `json`, which carries the same tables). Known shapes, each
re-verified rather than assumed:

- `issue-848` (×2) — **closed.** It differed *only* in `bounding_box.x1`: golden
  `127.70124053955078` against `127.70122528076172`, two f32 ULP. The geometry layer was
  innocent, as the earlier sweep had established; the drift is introduced deliberately upstream.
  `pipeline/page_order.rs:151` orders a page whose runs mostly share one quadrant rotation *in
  that rotated frame*: it turns every span origin into the frame, sorts, and turns it back, and
  the source itself notes at `:193` that `w - (w - x)` round-trips about one ULP of the page
  dimension away from `x` in f32. That page's spans are 90°-rotated, so the word origins
  `extract_words` reports — and the table box built from them — carry the drift, and
  `81.502 → 612 - (612 - 81.502)` reproduces the golden's `x1` bit for bit. `WordsFromOxSpans`
  now takes the same round trip (ordering is not reproduced: this path keeps its own span order).
- `issue-140-example` (×2): upstream reports no tables, this port one, from inside the
  intersection tier — `IsRealGrid` and `LooksLikeProseTable` are faithful, and the drawn paths
  are now byte-identical to upstream's (331 primitives, matched term for term). The words are
  not: this is a `/Rotate 90` page, and the two sides disagree about the *frame*. Upstream's word
  boxes are the flattened ones `postprocess_spans` leaves on a rotated page (`CENTRAL` at
  `(166.21, 482.09)` with `height = 53.34`, an advance-length height); this port's are derotated
  (`(59.57, 166.21)`, `height = 7.50`, the font size). Ours reads better and finds a grid where
  upstream finds none. See the span-source note under "The PDF gap, measured". One nearby rule is
  genuinely unported: pdf_oxide abandons the spatial sweep above `MAX_TABLE_EDGES = 1500` paths
  (`document.rs:19590`). Not the cause here (that page has 331), but missing.
- `nougat_040`/`pdfa_015` (one document), `pdfa_033`, `a_brief_introduction_…`,
  `an_introduction_to_statistical_learning_…` and `docling/2203.01017v2` are all the *heuristic*
  tier, not the native one: running pdf_oxide's `strict()` detector over these pages directly
  returns nothing at all, so every table in their goldens comes from xberg's own
  `extract_tables_heuristic`. Its inputs were checked and are not the cause —
  `segments_to_words`, `compute_adaptive_column_gap` and `heuristic_column_gap` are faithful, and
  on `nougat_040` the page's `extract_words` output matches upstream's 841 words exactly bar five
  glyphs. What differs is the *segments* the tier is fed: on `nougat_040` upstream's grid comes
  out 9 columns wide against this port's 7, and on `pdfa_033` the divergence is purely the order
  of words inside one cell. Both point at `extract_all_segments`, not at the reconstruction.
- `pdfa_021`/`nougat_046`: upstream splits the page at a column boundary this port reads straight
  through, cutting mid-word. That lives in pdf_oxide's XY-cut region split, upstream of the
  assembler.
- `pr-138-example` (×2): the separator rule is a branch-for-branch match and the geometry helpers
  match exactly, so the trailing space is inside the span's own text — span production, not the
  separator.
- `nougat_011`, and `nougat_033`/`pdfa_008` tipping the other way, all sit on
  `span_start - previous_end > span.font_size * 0.15` with bbox widths differing by well under
  one glyph advance. Font metrics, not a missing rule.

Three plain failures are this port's own 25 s wall-clock guard truncating the corpus's largest
documents, not behaviour: `intel_64_…_sdm`, `algebra_topology_…` and `bayesian_data_analysis_…`
all pass `plain` when the guard is raised to 300 s. The guard stays — it exists so a pathological
file cannot hang extraction, and upstream simply has no deadline to match.

**The other 22**, each with a named cause:

- Six upstream defects, listed below.
- `email/empty.pst` — PST is unported. One fixture, and it is empty, so it would be easy to fake
  a `format` block for; that would be wrong for every real PST.
- `xls/test_excel.xls`, `xlsx/data-with-macros.xla` — upstream writes a per-sheet formula dump
  into `metadata.additional` (`formulas_Sheet1 = "A1=K2*L2*12; …"`). XLSX already collects these;
  the legacy BIFF path needs formula-token decoding.
- `iwork/test.key`, `test.numbers`, `test.pages`, `ppt/simple.ppt` — untraced.
- `jats/sample_article.nxml` — needs `extraction/formula_xml.rs`, unported.
- `epub/features.epub` — three separate divergences, and the largest is not the maths one:
  1. ~~Content loss, 499 chars.~~ **Closed.** Two causes, both in this port. `EpubExtractor`'s
     node conversion had no `DefinitionItem` arm — the same bug upstream's issue #127 fixed, and
     its `~keep` comment says only the `DefinitionList` and `List` *containers* are skipped. And
     the `</dd>` handler cleared `_inDd` before calling `FlushDefinitionItem`, whose emit is
     guarded on exactly that flag, so every `<dd>` body was accumulated and then discarded — the
     trace showed the term set and 263 characters buffered with the flag already false. The
     corpus now has **no content losses at all**.
  2. ~~Over-extraction: an `epub:switch` branch this port renders and upstream does not.~~
     **Closed.** `resolve_epub_switch_elements` (`extractors/epub/content.rs:215`, added
     2026-08-01 in `b017ba20b8`) was simply unported. A switch keeps the first `epub:case` whose
     `required-namespace` the renderer draws and cuts every other branch out of the markup by
     byte range; the namespace set differs per renderer (`MARKUP_SWITCH_NAMESPACES` adds MathML,
     `PLAIN_SWITCH_NAMESPACES` does not — `epub/mod.rs:38`), so plain and markdown legitimately
     select different branches of the same switch. Ported as `EpubContent.
     ResolveEpubSwitchElements`; the fixture's markdown dimension now matches.
  3. **The two maths spellings are a stale golden, not a port bug**, and it is now traced. The
     committed golden is the 2026-08-12 binary's output — re-running that binary over the fixture
     reproduces its `content` byte for byte (only the `file`/`metadata` path fields differ) — and
     commit **`ec94d8c0bd`** (2026-08-16, "populate formulas for every format") postdates it. At
     `ec94d8c0bd^`, `MmlNode::Over` rendered unconditionally as `\overset{over}{base}`
     (mathml.rs:380 of that revision) with no accent-command mapping at all, and
     `math_symbols::render_run_text` had no structural escaping. That commit added
     `over_script_command`/`under_script_command` — including `"\u{23DE}" => "\overbrace"`
     (mathml.rs:932) and `"\u{23DF}" => "\underbrace"` (mathml.rs:941) — and
     `push_mapped_char`'s `escape_tex_structural` (math_symbols.rs:101), which is what turns a
     stretchy `<mo>{</mo>` into `\{`. This port reproduces *current* upstream on both counts, so
     both must stay red until the goldens are regenerated. Do not "fix" either one.
  Remaining: `plain`, `html` and `json` carry the two maths spellings above; `markdown` matches.
- Four `.md` fixtures opening with an HTML comment (`ground_truth/pdf/160428551.md`,
  `french_minutes_vision.md`, `docling/md/2023-06-20-PV.md`, `docling.md`). Both implementations
  route them to the HTML extractor; the whole difference is **one leading space** — html gets
  `<p> # tidylog…` where upstream has `<p># tidylog…`, and plain gains one blank first line.
  Narrowed: the whitespace-only-node guard in `converter/text_node.rs:80` (`had_newlines` and
  `output.is_empty()` → emit nothing) *is* ported correctly, and does not apply here because the
  node is `\n\n# tidylog…`, not whitespace-only. The prefix space therefore comes from the
  non-whitespace path, whose `skip_prefix` (`text_node.rs:162`) upstream computes with no
  is-empty check at all — so either `output` is non-empty on this side and empty upstream at
  that point, or the leading comment is consumed differently. Not settled.
- ~~`email-replace-mime-encodings-error-5.eml` — malformed MIME with no closed boundary.~~
  **Closed**, and it was a rule to port after all. `mail_parser`'s part decoders
  (`parsers/mime.rs:56` `mime_part`, `decoders/quoted_printable.rs:94`) both return
  `offset_end == usize::MAX` when a non-empty boundary never turns up before end of stream, and
  `parsers/message.rs:243` treats that as an encoding problem: the encoding drops to `None` so
  the part is re-read raw, its MIME type demotes from `TextHtml` to `TextOther` (text/plain
  keeps its type), `is_inline` goes false and the boundary is cleared. The demoted type then
  matches neither arm of the `MultipartAlternative` classifier at `:284`, so `add_to_html` and
  `add_to_text` are both false and `:333` pushes the part onto `attachments` instead. Ported as
  `MimePart.IsEncodingProblem`, set from the one `SplitBoundary` segment no boundary closed.
  The attachment's raw byte count now matches the golden's `unnamed|text/html|7043` exactly.
- ~~`fake-email-multiple-attachments.msg` — PDF text quality inside an attachment.~~ **Half
  right, and the half that mattered was PPTX.** The hard dimensions failed on element *order*
  inside `Engineering Onboarding.pptx`: `extraction/pptx/parser.rs:786` reads a shape's position
  only from an `xfrm` **in the DrawingML namespace**, and a `p:graphicFrame` carries `p:xfrm`
  instead — so upstream gives every table, chart and SmartArt frame the default `(0, 0)` and it
  sorts to the top of its slide. This port matched `xfrm` by local name, found the real
  coordinates, and put the slide's table after the body text that shares its row. Fixed in
  `PptxReader.ExtractPosition`, and the slide sort is now stable (`sort_by_key` is;
  `List.Sort` is not, and shapes with no position all share one key). Extracting that pptx
  standalone and running the reference generator over it now matches byte for byte.
  What is left is the soft `markdown`/`html` pair, and *that* is the PDF one: `dense_doc.pdf`
  diverges at `theextractiveQAsetting` — upstream's markdown fuses the words this port keeps
  apart, the same `segments_need_space` geometry difference documented for
  `pdf/copy_protected.pdf` above, with the sides reversed.

### The goldens are frozen at 2026-08-12, and cannot be regenerated here

This bounds every claim in this file, so read it before concluding anything about a failing
fixture.

`dotnet/tools/xberg-reference-gen/target/release/xberg-reference-gen` was dated **2026-08-12
16:44**, every golden is stamped 16:45, and **79 files under `crates/xberg/src` are newer than
the binary**. So the goldens record what upstream did on 12 August, not what its source says
today.

**This has now been settled empirically, not just argued.** `--offline` does fail on the
unvendored `mathemascii`, but a plain `cargo build --release --locked` succeeds — it took 7m17s
and fetched `html-to-markdown-rs` 3.11.2, which until then had never been vendored. Regenerating
with the rebuilt binary:

| fixture | committed golden | rebuilt binary |
|---|---|---|
| `epub/features.epub` | `\overset{⏞}` | `\overbrace` |
| `html/sinthgunt.html` | — | does not reproduce |

So both categories below are confirmed: the goldens genuinely predate current upstream, and this
port matches the source rather than the snapshot. Note the hazard this creates — the binary in
the tree is no longer the one that made the corpus, so a regeneration now would move every
format at once.

That makes one obvious-looking test worthless. Running the existing binary over a fixture and
finding it reproduces the committed golden byte for byte proves only that the August binary still
agrees with itself — it says nothing about whether current source would agree. This was used
here to argue a golden was current, and the argument does not hold.

A worked example of the difference it makes. `epub/features.epub` renders U+23DE/U+23DF as
`\overbrace`/`\underbrace` in this port, where the golden keeps them literal inside
`\overset`/`\underset`. Both sides' `over_script_command` map U+23DE to `\overbrace`
identically, and the EPUB path reaches the same converter, so the port and *current* upstream
agree — but commit `ec94d8c0bd` (16 August, "populate formulas for every format") postdates the
golden. This is a third category: neither a port bug nor an upstream defect, but a golden the
current source would not reproduce. Confirmed by the rebuild above: the fresh binary emits
`\overbrace`, exactly as this port does.

The same bound applies to the seven HTML fixtures. Their goldens are `html-to-markdown-rs`
**3.10.6** output — the only version vendored — while the lock records 3.11.2. Porting against
3.10.6 is what took html to 41/41, and that is correct *while these goldens stand*; 3.11.0 added
a `"template" | "noscript" => {}` dispatch arm that 3.10.6 lacks, so a regenerated corpus would
need those arms re-ported in the other direction.

**To settle any of this**, restore network access, `cargo update`/vendor `mathemascii`, rebuild
the generator, regenerate, and re-measure. Expect the generator to abort partway: it panics in
`mathemascii-0.4.0/src/scanner.rs:48` on a char boundary inside `≤` via the asciidoc extractor,
and a full run also creates ~222 goldens for fixtures the extractors fail on, which changes the
denominator. Back up `*-results-rust.json` first.

### Genuine upstream defects — 6 fixtures, safe to ignore

Each is traced to a specific line, not assumed.

- **Hash-ordered dBASE columns** (`dbf/stations.dbf`). `extractors/dbf.rs` builds column headers
  from `reader.fields()` in declared order, then fills each row with `record.into_iter()`, whose
  `IntoIter` is `std::collections::hash_map::IntoIter` (dbase 0.8.0, `src/record.rs:61`). Values
  therefore arrive in hash order and land under the wrong headers, differently on every row —
  the golden has `blue rail-metro #0000ff Van Dorn Street` on one row and
  `Franconia-Springfield blue #0000ff rail-metro` on the next. This port emits declared order
  consistently and is correct.
- **`<h0>` from a truncated heading depth** (`vendored/docling/.../multi_page.doctags.txt`), the
  `u16` depth through `((depth as u8) + 1).min(6)` described below.
- **UTF-16 BOM ignored** (`vendored/unstructured/xml/factbook-utf-16.xml`).
- **Corpus drift** (`ATTRIBUTIONS.md`, `LICENSES.md`, `scripts/corpus-patterns.txt`): the fixtures
  are `test_documents`' own docs and grew after their goldens were made.

### This port's gaps — 142 fixtures

**PDF, 114.** By failing dimension: 32 fail only on markdown/html plus tables, 33 add `plain`,
28 are json-and-soft, 10 are json alone, 4 tables alone, 3 metadata. The soft-dimension mass is
one cause — heading classification — and `tables` is the ruled/heuristic tier boundary.

**HTML, 0.** The seven that used to fail (`hip_13044_b`, `international_emergency_medicine`,
`sinthgunt`, `taylor_swift`, `wiki_duck`, both `test_wikipedia` copies) now match on every
dimension. None of it was table calibration: the causes were the two pre-parse strips
(`strip_script_and_style_tags`, `strip_hidden_elements`), the missing `noscript`/`template`
fall-through to the unknown handler, `handle_span` popping markdown hard breaks, a table ending
with a blank line instead of one newline, the attribute canonicalization the html5ever repair
leaves behind, and — for the metadata dimension — how many times a table's subtree is walked.

**Spreadsheet metadata, 3** (`xls/test_excel.xls`, `xlsx/data-with-macros.xla`,
`data_formats/test_01.ods`). Upstream writes a per-sheet formula dump into
`metadata.additional` — `formulas_Sheet1 = "A1=K2*L2*12; B1=B2; …"` — plus `source_uri`,
`final_uri` and `source_index`; this port writes `sheet_count`/`sheet_names` there instead and
extracts no formulas. `test_01.ods` additionally loses the document properties upstream reads
(`authors`, `created_at`, `modified_at`, `created_by`, `modified_by`).

**`email/empty.pst`.** Upstream emits `format: {format_type: "pst", message_count: 0}`; this port
emits no `format` block at all for an empty PST.

**`opml/opml-reader.opml`.** The `_note` attribute is dropped. Upstream keeps it inline:
`**Nevada** (_note: I lived here *once*.\n\nLoved it.)`.

**Markdown, 6.** Four (`ground_truth/pdf/160428551.md`, `french_minutes_vision.md`,
`docling/md/2023-06-20-PV.md`, `docling/md/docling.md`) open with an HTML comment, so both
implementations route them to the HTML extractor and differ only in the fallback paragraph's
whitespace — the output-format gap below. Two are the corpus-drift goldens above.

**MathML → LaTeX, 0.** `extraction/mathml.rs` and `math_symbols.rs` are ported
(`Internal/MathMarkup`); `odt/formula.odt` passes and `epub/features.epub`'s equations match
current upstream. Its two remaining spellings are the stale golden traced above, not this.

**Remaining singles**, each its own cause and none yet traced: `archives/documents.tar`,
`archives/documents.tgz`, `docbook/docbook-reader.docbook`, `jats/sample_article.nxml`,
`iwork/test.key`, `iwork/test.numbers`, `iwork/test.pages`, `ppt/simple.ppt`,
`vendored/unstructured/eml/email-replace-mime-encodings-error-5.eml` and
`vendored/unstructured/msg/fake-email-multiple-attachments.msg` (both traced and closed on every
hard dimension — see "Every failing fixture, categorized" above).

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
- [ ] **PDF content (389 fixtures, 360 fully matching; plain 368/388, markdown 227/388,
      html 219/388, json 364/388, tables 373/388; content-identical 95.9%, 0 catastrophes).**
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
- [~] **html 34/41 on the goldens that exist (was 29).** Eight conversion rules that had never
      been ported, all read out of `html-to-markdown-rs` rather than guessed at:
      - **`<pre>` came from the wrong module.** The crate carries two copies of the handler;
        `block/preformatted.rs` is dead code and `converter/main.rs` dispatches to
        `handlers/code_block.rs`. The live one dedents unconditionally and keeps a
        whitespace-only line verbatim — the markdown has its line ends trimmed globally
        afterwards, but the structure collector's Code node does not.
      - **`<cite>` gained emphasis it should not have.** `semantic/attributes.rs::handle_cite`
        italicizes, but nothing dispatches to it; the tag falls to the unknown handler.
      - **`<abbr>` never spelled itself out** (`text (title)`).
      - **The whole form family rendered nothing.** `<button>`, `<label>`, `<fieldset>`,
        `<legend>`, `<select>`/`<option>`/`<optgroup>`, `<textarea>` and the measurement
        elements each render their children and differ only in the spacing they add. A page's
        close button holding an inline `<svg>` is how three fixtures lost their icons.
      - **The media family too.** `<video>`/`<audio>` become a link to their source plus their
        fallback content, `<iframe>` a link to its src, and `<picture>` reduces to the first
        `<img>` it holds — which is where Wikipedia's footer logos live.
      - **An inline `<svg>` is an image**, serialized to a base64 data URI with its attributes
        sorted and their canonical camelCase restored (`converter/utility/svg_attrs.rs`).
      - **`<table><caption>` was dropped.** The converter's grid carries only cells, so upstream
        re-scans the raw HTML and inserts the nth caption before the nth table element
        (`extractors/html.rs::recover_table_captions`).
      - **Plain output uses a different walker.** `converter/plain_text.rs` replaces the returned
        text when the caller asks for plain; the structure is the markdown walk's either way, so
        it only shows on a page that yields no structured blocks and falls back to the whole
        conversion as one paragraph. That fallback also stopped trimming the text, which upstream
        pushes verbatim.

      Two metadata rules with it: an autolink returns from the link handler *before* the metadata
      collector runs, so `<https://example.com>` is a link in the output and no entry in `links`;
      and a permalink anchor inside a heading is collected by the ordinary `<a>` handler, which
      the heading path does not bypass.
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
- [x] **yaml 4/10 → 8 → 10/10.** A number now keeps the lexeme it was written with, so a 64-bit
      hash above `long.MaxValue` stays exact and `397.0` stays a float. A failed parse falls
      through rather than throwing out of the extractor, which had been losing four documents
      whole. Two more, in the second pass: a double-quoted scalar resolves its escapes (`\uXXXX`
      above all — a document that writes a curly apostrophe as `’` had the literal characters
      `u2019` in its text), and one that runs past the end of its line folds back onto one line,
      a plain break to a space and a backslash-escaped break to nothing (YAML 1.2 §7.3.1);
      without the fold the scalar never closes and everything after it parses as if inside it,
      which truncated one fixture by a third.

      The float spelling also depends on which value the flattener was handed: YAML deserializes
      into a `serde_json::Value` first, so its floats print serde's way and keep a fractional
      part, while TOML flattens from `toml::Value` and prints through Rust's own `Display for
      f64`, which drops the trailing `.0`. One `DisplayNumber` for both had `397.0` collapsing to
      `397` in a YAML document.
- [x] **svg 40/41 → 41/41.** The SVG text-element filter — only text inside `text`/`tspan`/
      `title`/`desc`/`textPath` counts as content — was applied to CDATA sections as well.
      Upstream guards only the `Text` arm; a CDATA section is how an SVG carries its script and
      style bodies, and on one flamegraph fixture that was half the document (34,399 characters
      against our 17,782).
- [x] **rtf 18/19 → 19/19.** A `\'hh` escape was always decoded as Windows-1252. It decodes with
      the active code page: the font table's `\fcharsetN` for the font in scope (or the `\deffN`
      default), then `\ansicpgNNNN`, then 1252 — and `\fcharset` numbers are RTF's own font-charset
      enumeration, not code page numbers, so they need translating. Adjacent escapes are decoded
      as one byte string, since a multi-byte code page spells one character across several.
- [x] **tex 8/9 → 9/9.** `longtable`, `tabularx` and `tabulary` hold the same `&`-separated grid
      as `tabular` and were reaching no table handler at all; and longtable's page-break markers
      (`\endhead`, `\endlastfoot`, the booktabs rules) are scaffolding rather than rows.
- [x] **pptx 10/11 → 11/11.** Only `a:r` was read. Two siblings carry text as well: `a:br`, an
      explicit in-paragraph line break, and `a:fld`, a field — a slide number — whose rendered
      value PowerPoint caches in a nested `a:t`.
- [x] **odt 17/21 → 20/21.** Three defects: a note's key lost the `fn`/`en` prefix that is the
      only thing telling a footnote from an endnote; the inline run was read one level deep, so a
      span inside a span lost its tail and a caption inside a `draw:text-box` was seen only by the
      dedicated caption pass rather than twice as upstream sees it; and a pagination field's
      cached display text was emitted as document content. The last fixture, `odt/formula.odt`,
      needs the MathML→LaTeX converter below.
- [x] **eml 39/43 → 42/43.** Only a *missing* image description becomes the `[Image]`
      placeholder. `alt=""` is a deliberate "this image carries no meaning" and stays the empty
      string; treating it as absent put a column of `[Image]` through every marketing email.

      **eml is 43/43 now.** The last one, `email-replace-mime-encodings-error-5.eml`, was
      malformed MIME recovery, and it *is* a rule: `mail_parser` flags a part no boundary closes
      as an encoding problem, re-reads it raw and demotes its type, which drops the `text/html`
      alternative out of the body and onto `attachments` while the `text/plain` one stays the
      message. Traced line by line in the fixture list above.
- [x] **epub 5/9 → 8/9.** `<audio>`/`<video>` subtrees are stripped before conversion — they are
      delivery controls, and the conversion otherwise emits their source URLs and serialized
      fallback markup beside the prose. And a `<blockquote>` is recorded even though its contents
      are not inside it: the walker's nodes are flat, so the quote opens and closes at once and
      the quoted paragraph follows as a sibling (upstream #127).
- [x] **MathML → LaTeX (`extraction/mathml.rs`, ~1000 lines plus `math_symbols`).** Ported.
      `odt/formula.odt` reads `E=m\cdot c^{2}` and odt is 21/21. It also feeds
      `recover_mathml_formulas`, which recovers a `<math>` subtree the conversion library's
      structure has no node kind for. Note that the converter has moved since the goldens were
      taken: `ec94d8c0bd` (2026-08-16) added the accent-command mapping and the TeX structural
      escaping, so `epub/features.epub` reads `\overbrace`/`\{` where its golden — written four
      days earlier — has `\overset{⏞}`/`{`. The port follows the source.
- [x] **markdown tables (761/782 → 781).** A GFM table's header fixes its width; short rows are
      padded and long ones truncated, so a row's nth value stays under the nth heading.
- [x] **AsciiDoc, ODP, WebVTT, Quarto, R Markdown.** Five formats that reached no extractor.
      The first three had none written; the last two were unclaimed MIME types.
- [x] **typ 0/8 → 7/8.** Five separate missing branches; the last fixture needs `@label`
      reference resolution, which is a feature rather than a fix.
- [x] **md 737/775 → 767, txt 955/975 → 956.** Fifteen block and inline rules the port's
      pulldown-cmark stand-in did not have. Each was read out of pulldown-cmark 0.13.4's
      `firstpass.rs`/`scanners.rs`/`parse.rs` (or `infer`'s `text.rs`) before it was written:

      - **Lazy continuation.** A list item's paragraph continues onto an under-indented line at
        *any* indentation, including none. Cutting the item off at its own indent left the
        continuation to be re-read as a top-level paragraph, splitting one bullet into two
        blocks. Worth 8 fixtures on its own — and the first attempt regressed 100, because the
        "ordered lists interrupt a paragraph only at 1" rule governs *starting* a list, not
        continuing one: inside a list, any sibling marker ends the item.
      - **List-marker width.** CommonMark's N: the marker takes the whole space run for one to
        four spaces, but only one when five or more follow — which is what leaves the rest to be
        an indented code block. `2.  item` is a marker of width 4, not 3.
      - **A marker on the item's own first line** (`- - text`) opens a sublist; it was being kept
        as text, and escaped back out as `\-`.
      - **Changing the bullet starts a new list** (`* … - … + …` is three lists, not one) — and,
        the other way round, **a marker further left continues the list it is in**. A line that
        fails a list item's indentation closes the item, never the list around it, and
        `continue_list` then matches on the bullet character alone; a list that opens at column
        two and continues at column zero is one list. Closing and reopening it put an empty list
        between the two halves of `pdf/44498957.md`.
      - **GFM alerts.** `> [!NOTE]` is an admonition with no quote container at all; the tag is
        scanned right after the quote marker and must be alone on its line.
      - **Definition lists.** `ENABLE_DEFINITION_LIST` was not implemented. A `:` at the start of
        a line turns the paragraph above it into the term and opens a definition; further `:`
        lines add further definitions; and a `:` interrupts an open paragraph. The one block that
        emits no events — a thematic break — has to be remembered, or the paragraph before it
        still looks like the last thing emitted.
      - **Wikilinks.** `ENABLE_WIKILINKS` was missing. `[[name|label]]` shows the half after the
        pipe; without a pipe the display text is a fresh node over the raw source range, so it
        never passes the first pass and keeps its straight quotes. Popping the wikilink stack
        disables enclosing links, which is why `[[1]](#cite_note-1)` is a wikilink followed by
        literal text.
      - **Reference-link fallback.** An inline destination that does not parse (a space in an
        unbracketed URL) leaves the label to resolve as a shortcut reference, with the
        parentheses staying literal.
      - **Email autolinks** keep the bare address as the destination; only pulldown's own HTML
        writer prepends `mailto:`, and the extractor reads the destination.
      - **`#309` is not a heading** and ```` ```cmd``` ```` is not a fence: an ATX opening needs a
        space after its hashes, and a backtick fence's info string may hold no backtick.
      - **Only a "heavy" table interrupts a paragraph** — one whose header row opens with `|`.
        A bare `Claim ID | Claim type` under a line of prose is more prose.
      - **Soft breaks are their own event.** Folding them into the text node gave an image's alt
        text a space the alt buffer never receives (`[![Build\nStatus](…)]` names *BuildStatus*),
        and a hard break's backslash has to survive to the inline pass or the `**` before it is
        not left-flanking.
      - **Annotation trimming clamps rather than filters** (upstream's #226): a span running into
        trimmed trailing whitespace still covers real words. Filtering it dropped whole links.
      - **`<` only opens a tag** before a letter, `/`, `!` or `?` (the HTML tag-open state).
        Our tokenizer took everything to the next `>`, so `a <- filter(x, y > 0)` in an R README
        vanished.
      - **The WHATWG sniffing table**, which upstream reaches through `infer::is_html`: a
        document opening with `<!--` (or `<P>`, `<A `, …) is HTML however its markup continues.
        The port had a comment-skipping approximation that reached the XML extractor whenever the
        comment was never closed.

      What is left on md is 8 fixtures. Two are the stale goldens listed above. The other six
      are `.md` files that open with an HTML comment and so route to the HTML extractor in both
      implementations (`fictionbook/emphasis.md`, `fictionbook/poem.md`, `pdf/160428551.md`,
      `pdf/french_minutes_vision.md`, `docling/md/2023-06-20-PV.md`, `docling/md/docling.md`).
      They differ only in the leading and trailing whitespace of the converter's one paragraph,
      and they are the **html** gap above rather than a markdown one: upstream passes the output
      format *into* `convert_html_to_markdown_with_tables`, so its plain text and its markdown
      are two different conversions, while this port renders every format from the markdown one.
      Pushing that paragraph untrimmed (which is what upstream does) was measured: it wins html
      and json on those six and loses plain on the same six, with no fixture flipping either way,
      so the trim stays until the plain conversion exists.

      On txt what is left is 2, both listed above: the DocTags `<h0>` defect and one stale
      golden. (The other 17 of the 975 txt fixtures are ones upstream itself fails on — large
      DocTags streams — and the harness counts them separately.)

### The PDF gap, measured

PDF was where the remaining distance was: 106 of 389 fixtures matched on every hard dimension,
and the shortfall was not one bug but three layers. **Two of the three are now closed.**

Where it stands after the segment and paragraph work, and the path-operator and rotated-frame
fixes below: **360 of 389 fully matching**, plain 368/388, md 227/388, html 219/388, json
364/388, metadata 388/388, tables 373/388, catastrophes 0. The two rules that moved it furthest were both cases of the port having implemented the wrong
function rather than implementing one badly — hierarchy segments built per line where upstream
builds them per span, and `classify_paragraphs` where the untagged path calls
`finalize_paragraph`.

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

**The native table tier's real gap is the cluster fallback, and only that.** A raw function
count says this port has 23 of `spatial_table_detector.rs`'s 73 production functions, but most of
the difference is unreachable from xberg and counting it as a gap is wrong.
`detect_tables_with_lines` dispatches on `(horizontal_strategy, vertical_strategy)`, and *both*
xberg tiers set `TableStrategy::Lines` on both axes — `strict()` does, and `extract_tables_bordered`
builds its own config that does. `relaxed()`, the only preset using `Text`, is never used by
xberg. So the whole text-edge strategy (`detect_tables_from_spans`, `_column_aware`,
`detect_tables_hybrid`, `detect_text_edge_columns`, `detect_tables_from_horizontal_rules`) and the
gates reached only from it (`passes_spatial_quality_gate`, `is_regular_lattice`,
`looks_like_prose_paragraph`, `looks_like_bulleted_list`, `looks_like_cjk_prose`,
`filter_columns_by_row_coverage`, `consolidate_adjacent_table_fragments`) is dead code here, in
the same way pattern marking is. `detect_merged_cells` joins them for a subtler reason: it is only
the `None` arm of `grid_to_table`'s `visual_merge_info`, and the sole reachable caller always
passes `Some(detect_merged_cells_visually(..))`.

Note that `validate_table_structure_internal` and `has_split_modal_column_groups` are NOT in that
dead set, despite being easy to file there — `detect_tables_in_cluster` calls them directly. They
are ported, as `ValidateClusterGrid` and `HasSplitModalColumnGroups`.

**Closed.** Taking the call closure from the `(Lines, Lines)` arm gives 41 reachable functions,
and all 41 are now ported: the intersection pipeline, plus the cluster fallback that runs when no
rules cross (`group_lines_into_clusters`, `detect_tables_in_cluster`, `cluster_values`,
`detect_header_row`/`_above`, `grid_to_table`, `detect_merged_cells_visually`,
`trim_empty_columns`, the structural validation). Measured with the fallback live: PDF `ok`
273 → 274, tables 318 → 318, catastrophes 0 — inside the noise band, so neutral. It is not inert
though: instrumenting the branch shows it firing 420 times across the PDF corpus at one to four
tables a hit, with the output absorbed by the caller's grid and prose guards or displacing
heuristic-tier tables on pages a native hit suppresses.

The lesson is worth keeping for the next audit: a raw function-count diff between the two trees
reads as a large gap and is mostly noise. Take the call closure from the entry point xberg
actually uses, and map names by hand — a snake_case-to-PascalCase match falsely reported twelve
of these as missing when eleven were present under different names.

**Two upstream quirks the port had quietly corrected.** Both were found by dumping pdf_oxide's
own answer for a page and diffing it against this port's, and both cost fixtures until the port
stopped being right:

- **`extract_paths` drops `B`, `B*` and `b*` entirely.** Its operator match
  (`document.rs:17590`) answers `S`, `f`/`F`, `f*`, `b` and `n`; every other painting operator
  falls to the catch-all `_ => {}` arm, which neither emits the path nor clears the operations
  already built up — so a run of fill-and-stroke subpaths accumulates until the next `W n` clip
  discards it. The XObject walker (`:18060`) has the same set. This port painted them, and on
  `pdfa_029`, whose producer draws every cell border as `m l l l h B*`, that turned page 10's 27
  table primitives into 231 and flooded the edge grid with rules upstream never sees. Matching
  the omission reproduces upstream's path list exactly on every page checked, and took PDF
  `ok` 356 → 358, `json` 361 → 364, `tables` 369 → 371 with no dimension falling.
- **`span_text_for_cell` was unported** (`structure/table_extractor.rs:515`). It is the one
  helper the earlier `spatial_table_detector.rs` function audit could not see, because it lives
  in the neighbouring file; `extract_cell_text` calls it on every span it joins. A run reading
  `N.M` whose box is far wider than its digit count can account for is two values straddling a
  column boundary, not a decimal, and is split at the dot. It reaches `Table.rows[].cells[].text`,
  which the validation gates read — xberg's own `cell_text_in_reading_order` overrides the text
  whenever a cell has spans, so the split is only ever visible through those gates.

**`extract_words` and `extract_page_text_with_options` are different span sources, and the port
has only one.** `extract_tables_with_config` is fed `extract_words`, which reaches spans through
`pipeline::page_reading_order` → `PdfDocument::extract_spans` → `postprocess_spans`; the text and
hierarchy paths reach them through `extract_spans_with_reading_order`, which applies
`drop_offpage_spans` and a sort and nothing else. Both start at `extract_spans_raw`, so they
mostly agree, but not always: `postprocess_spans` runs `apply_super_sub_script_substitutions`
(upstream's words carry `¹`/`₁`/`₂` where the text path has ASCII digits — five of 841 words on
`nougat_040` page 2), and on a `/Rotate`d page it maps rotated content into the displayed frame,
which is the whole of the `issue-140-example` divergence above. This port bridges one span list
to every consumer. Closing it means giving the word path its own producer, which is a structural
change, not a tweak; the rotated-frame round trip landed for `issue-848` is the one piece of it
that could be lifted out on its own.

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
