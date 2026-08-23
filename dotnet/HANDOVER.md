# Handover — C# port of xberg (`dotnet/`)

Written 2026-08-23. Read this with `dotnet/TODO.md`, which carries the deeper
per-format detail; this file is the *state of play* and the *what to do next*.

---

## 1. Where things stand

Branch **`claude/merge-xberg-review-dotnet-yac9fa`**, one unmerged commit ahead of `main`:

```
048aade8fc  Revert "pdf: build hocr words in the segment's upright frame, as upstream does"
48bafc2b67  Merge pull request #18   <- main is here
```

**PR #18 is merged.** Everything up to `6c84b6f285` is in `main`. The single commit above was
rebased onto the new base and force-pushed with `--force-with-lease`. If you open a PR for
further work it is a **new** PR — #17 and #18 are both merged and must not be reused.

Corpus parity, whole corpus in one run (the only figure that means anything — a per-format run
once validated the mime sniff on md/txt while quietly handing three PDFs to the HTML extractor):

| | |
|---|---|
| fixtures walked | 2942 |
| comparable (Rust extracts something) | 2787 |
| **matching on every hard dimension** | **2770 (99.4%)** |
| catastrophes | 0 |
| content losses | 0 |
| unit tests | 1494 |

PDF line: `389  378 380/388 305/388 305/388 379/388 388/388 384/388`
(n, ok, plain, md, html, json, metadata, tables).

Hard dimensions are **plain, json, metadata, tables**. md/html are soft (reported, not counted)
unless you pass `--strict-md`.

> Re-derive these numbers rather than trusting them. The running count went stale twice in the
> last session — it read "about eleven port gaps" long after the real figure was four, and then
> zero.

---

## 2. What is actually left — 17 failures, all classified

**Genuine port gaps: 0.** Every remaining failure is one of these four things.

### 2a. Order-dependent goldens — 7, not fixable
`nougat_011`, `nougat_012`, `nougat_046`, `pdfa_021`, `pdfa_027`, `pdfa_031`, `pdfa_044`.

pdf_oxide keeps a **process-global FIFO font cache** (`fonts/global_cache.rs:111`), so upstream's
output is a function of what was extracted before it in the same process. Measured directly:
`nougat_011` yields 30143 chars alone, 30213 after `pdfa_044`, against a golden of 30147. No
single-document-per-process consumer can reproduce these.

**Approach: don't.** The only honest fixes are to regenerate the goldens one-process-per-document
(changes the corpus) or to replicate the global cache (imports the bug). Leave them.

### 2b. Upstream defects / corpus drift — 7, safe to ignore
`ATTRIBUTIONS.md`, `LICENSES.md`, `scripts/corpus-patterns.txt`, the DocTags `<h0>` depth
truncation (`multi_page.doctags.txt`), `factbook-utf-16.xml`'s BOM, `dbf/stations.dbf`'s
hash-ordered columns, `epub/features.epub`'s stale golden. Detail in `TODO.md`.

### 2c. Deliberate non-port — 1
`vendored/docling/pdf/2305.03393v1.pdf` (`plain`, `json`).

That page's text layer literally contains HTML table markup, and upstream's `html-to-markdown-rs`
leaves the unclosed `<td>` open: it swallows the rest of the page's prose into that cell and then
drops it. Reduced to pure HTML, no PDF involved:

```
before\n<table> <tr> </tr> <td> </table>\nafter text here\n
  upstream -> "before\n\n|  |\n| --- |\n"
  port     -> "before\n\nafter text here\n"
```

Identical under `TierStrategy::Auto` and `Tier2`, so it is the parser's behaviour, not the fast
path. Matching it means making an unclosed `<td>` absorb and discard the remainder of any
document. No `.html` fixture in the corpus has an unclosed `td`/`th` (0 of 42); exactly one PDF
golden shows the swallow. **Approach: leave it.** This fixture's `markdown` and `html` already
match byte-for-byte. This is the case the standing instruction ("only ignore files genuinely
parsed wrong by upstream") carves out.

### 2d. The two large PDFs — 2, THE ONLY OPEN TECHNICAL THREAD
Intel SDM (`tables` only) and `algebra_topology` (`json` + `tables`). Both pass `plain` now; the
SDM passes `json` too. What remains is real. **See §3.**

---

## 3. The open thread: table column boundaries

### What is known

- **Detection is exact.** `algebra_topology` produces 1701 tables on the same 1033 pages as the
  golden — zero surplus, zero missing, `page_number` never differs.
- Only **5 of 1701** tables differ, plus **1 bounding box** 2pt out. Every one is *column
  boundary placement*: same words, split at different x positions. Affected pages: 807, 999,
  1183, 1194, 1240.
- The mechanism is a **cliff** in `compute_adaptive_column_gap`
  (`crates/xberg/src/pdf/structure/regions/tables.rs:294`):

  ```
  any gap >= 40  ->  clamp(median(large_gaps) / 2, 20, 60)
  otherwise      ->  clamp(median(all_gaps)  * 3, 20, 60)
  ```

  The branches land 2–3x apart. The affected regions sit exactly on the boundary — page 807 has
  gaps of **39 and 40**; page 1194 has `[33,33,33,34,34,34,34,34,38,38,38,38,39,41,42,43,44,48]`;
  one page 1183 region has 2 outlier gaps out of 51 swinging the threshold from 20 to 60. A
  sub-unit difference in one word's integer x coordinate flips `large_gaps` membership and
  changes the column granularity of the whole table.

### What is ruled out (do not re-investigate)

| hypothesis | verdict |
|---|---|
| `compute_adaptive_column_gap` mis-ported | No. **Both** C# copies (`PdfTableReconstruct:254`, `PdfLayoutTables:405`) are faithful, sort stability included. |
| Rounding mode (the banker's-rounding trap) | No. `RoundClamp` uses `MidpointRounding.AwayFromZero`, correctly matching Rust's `.round()`. |
| Table count / page distribution | No. Identical. |
| Building words in the segment's upright frame | **Tried and reverted — see below.** |

### The reverted attempt — read before retrying

`988b17ba14` changed `SplitSegmentToWords` to take its origin from `seg.UprightOrigin()` instead
of raw `seg.X`/`seg.Y`, because upstream's `segment_to_hocr_word` and `split_segment_to_words`
both do, with an explicit comment on why, and C#'s `UprightOrigin`/`IsUnrotated` are faithful
ports of theirs. **It regressed the corpus badly** and was reverted in `048aade8fc`:

```
before   378 ok  305 md/html  379 json  384 tables
after    369 ok  302 md/html  374 json  375 tables
```

Nine fixtures newly failing, all on `tables`: `a_brief_introduction_to_neural_networks`,
`a_comprehensive_study_of_convergent`, `an_introduction_to_statistical_learning`,
`bayesian_data_analysis`, `perfect_hash_functions_slides`, `proof_of_concept_or_gtfo_v13`,
`pdfa_042`, and both `senate-expenditures` copies.

**Unresolved, and the thing to settle first:** whether the ported spans carry rotation values that
differ from what Rust's `SegmentData` holds at this point, or whether Rust's segments arrive
already in the upright frame so its `upright_origin()` call is an identity there too. Do not
re-apply this change on reasoning alone — the measurement disagrees very clearly.

### How to approach it

The remaining question is *why the word coordinates differ from Rust's at all*. Reading upstream
will not answer it; this session produced four confident, wrong hypotheses that only measurement
killed. The method that has worked every time on this port:

1. Build a **probe crate** against the real `pdf_oxide 0.3.77` (the approach that settled
   `right_to_left_03` and `2203.01017v2`). Dump, for `algebra_topology` page 807, the
   `SegmentData` list *and* the derived `HocrWord` list — `text`, `left`, `top`, `width`,
   `height`, `rotation_degrees`.
2. Dump the same from C# (`--dump-tables` does not carry geometry; add a temporary dump).
3. Diff word by word. Expect a small number of words differing by 1 unit. The first word whose
   `left` differs by 1 is the whole answer — walk *its* span back through
   `PdfOxideSegments.FromPage` to the ported span, and compare that span's bbox with Rust's.
4. Only then decide whether the fix belongs in the span pipeline or the segment conversion.

**Set expectations before spending much on this.** It is worth 2 fixtures out of 2787, both
already correct on `plain`. `algebra_topology` also fails `json`, which may or may not be the same
cause — check that first, it is cheap and might be a different and more tractable bug.

---

## 4. How to work on this repo

### Build, test, measure

```bash
cd dotnet
dotnet build src/Xberg -c Release
dotnet test  tests/Xberg.Tests -c Release          # 1494 tests, ~2s
dotnet run --project tools/Xberg.TestRunner -c Release --no-build -- \
    ../test_documents --ext pdf --list-fail        # one format
dotnet run --project tools/Xberg.TestRunner -c Release --no-build -- \
    ../test_documents --list-fail                  # whole corpus (long)
```

Harness flags: `--filter <substr>`, `--ext <ext>`, `--show N`, `--diff`, `--strict-md`,
`--list-ok`, `--list-fail`, `--dump <dir>`, `--dump-tables <file>`, `--dump-metadata <file>`,
`--extract <file> [--format plain|markdown|html|json]`.

### Configuration

The library **never reads the environment** — a test (`XbergOptionsTests`) fails the build if
`GetEnvironmentVariable` reappears anywhere in library code outside `Core/Options.cs`. Configure
via `XbergOptions`:

```csharp
XbergOptions.Default = new XbergOptions { PdfMaxSecondsPerDocument = 0 };   // once at startup
new ExtractionConfig { Options = new XbergOptions { UsePortedPdfSpans = false } };  // per call
```

| knob | default | env (harnesses only, via `XbergOptions.FromEnvironment()`) |
|---|---|---|
| `UsePortedPdfSpans` | `true` | `XBERG_OXIDE_SPANS=0` to disable |
| `PdfBaseSeconds` | 25 | `XBERG_PDF_BASE_SECONDS` |
| `PdfMillisecondsPerPage` | 50.0 | `XBERG_PDF_MS_PER_PAGE` |
| `PdfMaxSecondsPerDocument` | 3600 (cap; `<=0` disables the guard) | `XBERG_PDF_MAX_SECONDS` |

The PDF guard is `clamp(25 + 0.05 * pages, 25, 3600)` seconds. Both harnesses call
`FromEnvironment()` explicitly at startup, which is what lets one variable drive this port and the
Rust original through the same comparison.

---

## 5. Traps that cost real time last session

**The container resets without warning.** It happened four times, rewinding `HEAD` ~100 commits
and leaving a stale working tree (`PdfSameLineReorder.cs`, `PdfSubSuperscript.cs`, and modified
`PdfContentExtractor/PdfFont/PdfPageText`). **Always check `git ls-remote origin <branch>` before
concluding anything is lost** — every time, the work was safely on the remote. Recovery:

```bash
rm -f dotnet/src/Xberg/Internal/Pdf/PdfSameLineReorder.cs dotnet/src/Xberg/Internal/Pdf/PdfSubSuperscript.cs
git checkout -- dotnet/src/Xberg/Internal/Pdf/
git fetch origin <branch> && git checkout -B <branch> origin/<branch>
git submodule update --init --recursive
```

Commit and push often; the scratchpad under `/tmp` is wiped too.

**`--no-build` will silently run a stale test assembly.** This produced a false "1466 tests pass"
after a change that did not compile: the first `dotnet test` failed to build, its output was
filtered through `tail -2` so only warnings showed, and the follow-up `--no-build` ran the
*previous* binary. Never filter a build's output down to the point where you cannot see whether it
succeeded, and never trust `--no-build` right after editing.

**Do not rebuild while a sweep is running.** Sweeps use `--no-build`; rebuilding swaps the
binaries underneath them. Either wait, or work in an isolated copy
(`cp -r` the tree, symlink `test_documents`, build and sweep there).

**Verify every agent claim yourself.** A subagent once committed an 862-line file
(`PdfSpatialTables.Cluster.cs`) that had **no callers** — "build clean, tests pass" validated
nothing. Agents also leaked instrumentation into `crates/` and pointed `[patch.crates-io]` at a
scratchpad copy of pdf_oxide. Before committing agent work: check the code is actually reachable,
`git status` for stray files, and `git diff HEAD -- crates/` is empty.

**Measure before believing.** Roughly half of everything that looked obviously right in this
codebase was wrong: the double-parse and font-cache theories in the profiling (7% and 0.5s, not
the bulk), the upright-frame fix (regressed 9 fixtures), a `ConditionalWeakTable` memoization
(−9%), `TensorPrimitives.Max` pooling (inside noise). SIMD never once paid; every performance win
came from not allocating and not throwing.

**Baseline noise is now gone — keep it that way.** Under the old flat 25s guard, totals moved by
1–3 fixtures between runs of identical code. With the page-scaled guard, the flat-120s, the
600s-no-guard, and the shipped scaled runs all produce byte-identical results. If you see a
one-fixture move now, it is real, not noise.

---

## 6. Performance, for context

The port is **~2x faster than the Rust original** on the largest fixture: Intel SDM 55s in C#
versus ~105s per extraction in Rust (419s for its four formats). Both scale linearly — SDM ~9
ms/page over 4778 pages, `algebra_topology` ~20 ms/page over 1962. Nothing hangs.

SDM time breakdown: page loop 45.1s of 55s, then `tables:heuristic` 6.4s, `scan-detect` 1.7s,
ruled tiers 1.6s. Inside the page loop each page takes **three** content-stream passes —
`ExtractChars` 9.0s, `ExtractSpans` 5.8s, and the old interpreter 3.2s (still needed for the drawn
paths the table tiers read) — plus `WordsFromOxSpans` 10.4s.

If you want to optimise, the ranked targets are `ExtractChars` (9.0s, one object per glyph plus a
sort, and it exists only to feed `OxCharXOffsets.Stamp`), `WordsFromOxSpans` (10.4s, a pure
conversion), and `PdfFilters.Inflate` (~5%). The clearest cross-format regression is `jsonl` at
2.5x Rust. None of this is urgent.

Note also: upstream's golden generator wraps each extraction in
`tokio::time::timeout(45s)`, but `extract` is CPU-bound synchronous work inside an async fn, so
**that guard never fires** — the goldens for the large files are complete ~105s extractions.

---

## 7. Suggested order of work

1. **Check `algebra_topology`'s `json` failure** — cheap, may be unrelated to the table issue and
   more tractable.
2. **The probe-crate word diff of §3** if the table divergence is worth 2 fixtures to you.
3. Optional performance work per §6.

There is no *porting* work left. The remaining items are a narrow geometry question and
deliberate non-goals.
