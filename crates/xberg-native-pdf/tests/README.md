# Integration tests

Each `tests/*.rs` file is its own test binary. Unit tests live beside the code in
`#[cfg(test)]` modules under `src/`; this directory is for tests that exercise
the crate through its public API.

## Running

```bash
# The canonical lane — the one that must never go red.
cargo test --features rendering --lib --no-fail-fast

# Everything: lib, integration tests, doctests.
cargo test --features rendering --all-targets --no-fail-fast
```

Always pass `--no-fail-fast`. Fail-fast reports the first failing target and
hides every other one, turning a ten-failure regression into a one-line report.

## Shared helpers

`tests/common/mod.rs` is included per binary with `mod common;`. It holds the
synthetic-PDF builders (`build_minimal_pdf_raw`, `finalize_pdf`). Use it rather
than copying a builder into a new file — the copies are how the previous ones
drifted apart.

## Fixtures

`tests/fixtures/` holds **small, synthetic** PDFs only. A new reproducer must be
a minimal PDF constructed in code, not a third-party or reporter-supplied file:
see the `fixture-hygiene` rule in `CLAUDE.md` for why this is a hard rule and not
a preference.

**There is no external corpus.** The suite once carried tests that read
real-world documents from the original author's private machine — a gitignored
`tests/fixtures/real/`, `$HOME/projects/native_pdf_tests/`, a local veraPDF
checkout, `/tmp`. Those documents were never published and cannot be obtained,
so the tests were removed rather than left as permanently-skipped placeholders.
Do not reintroduce a test that depends on a document this repository cannot
provide.

## A test must never pass because its input was missing

This is the rule that matters most here, because the suite used to break it in
about 25 places. A test that does

```rust
if !path.exists() {
    return; // fixture not available; skip
}
```

reports `ok`. It is green on CI, green for every reviewer, and green on every
machine except the one that has the file — so it proves nothing while looking
like coverage.

Instead:

- **Prefer a synthetic reproducer.** Most defect classes — spacing, reading
  order, cmap handling, path extraction — reproduce fine on a hand-built content
  stream, and isolate the defect better than a 4 MB real document does.
- **If another running test already covers it**, delete it and say which one
  does.
- **Never leave a placeholder.** A test that can never run — because its input
  does not exist anywhere — is the same lie as the soft-skip, only quieter.

The same applies to `assert!(true)` and to asserting a boolean the test itself
just set: if a test cannot fail, it is not testing anything.

## Naming

Name tests and files by defect **class**, not by tracking number or reporter —
`type0_identity_h_tj_word_seam`, not `issue847` or `test_acme_corp_pdf`. Credit
reporters in `CHANGELOG.md`, which is what it is for.
