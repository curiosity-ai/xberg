//! Regression test for a reachable panic in JATS `<history>` date metadata formatting.
//!
//! `crates/xberg/src/extractors/jats/mod.rs` titlecased a `history_dates` entry's
//! `date-type` by slicing it at byte offset 1:
//!
//!   date_type[..1].to_uppercase() + &date_type[1..]
//!
//! `date_type` comes straight from the verbatim `date-type` XML attribute on a
//! `<date>` element inside `<history>` (`crates/xberg/src/extractors/jats/elements.rs`),
//! which only guards on the value being non-empty
//! (`if !date_text.is_empty() && !date_type.is_empty()`). Non-empty rules out the
//! out-of-bounds case but says nothing about UTF-8 char boundaries: any `date-type`
//! whose first codepoint is multi-byte (e.g. `"é"`, 2 bytes) makes `date_type[..1]`
//! slice inside that codepoint's encoding and panic. This is reachable from
//! `extract_bytes` with the default config, with no gate and no `catch_unwind`
//! anywhere upstream, so a crafted JATS/XML article aborts the calling process.

// The JATS extractor is `#[cfg(feature = "xml")] pub mod jats;`
// (crates/xberg/src/extractors/mod.rs), and `office` does NOT imply `xml`. Without this
// gate the file still compiles and runs everywhere, but means nothing: MEASURED under
// `--features office`, the panic test passes VACUOUSLY (its `catch_unwind` wraps an
// extraction that never reaches JATS) while the positive control fails outright on its
// `.expect(...)`. Gate the whole target so it either runs for real or does not run at all.
#![cfg(feature = "xml")]
// ~keep: test/bench binaries print by design; org logging policy exempts tests
#![allow(clippy::print_stdout, clippy::print_stderr, clippy::dbg_macro)]

mod helpers;
use helpers::extract_bytes_document_blocking;

use std::panic::{self, AssertUnwindSafe};
use xberg::ExtractionConfig;

const JATS_MIME_TYPE: &str = "application/x-jats+xml";

/// Build a minimal, otherwise well-formed, JATS article whose `<history>` section
/// declares a single `<date>` with `date-type` set to `date_type` verbatim.
fn build_jats_with_history_date_type(date_type: &str) -> Vec<u8> {
    format!(
        r#"<?xml version="1.0" encoding="UTF-8"?>
<article>
  <front>
    <article-meta>
      <article-title>Test Article</article-title>
      <history>
        <date date-type="{}">
          <year>2024</year>
        </date>
      </history>
    </article-meta>
  </front>
</article>"#,
        date_type
    )
    .into_bytes()
}

/// A crafted JATS article whose history date declares `date-type="é"` (`é` is a
/// 2-byte UTF-8 sequence) must not unwind the calling thread. Before the fix,
/// `date_type[..1]` sliced at byte offset 1, which lands inside `é`'s encoding,
/// and panicked with:
///
///   byte index 1 is not a char boundary; it is inside 'é' (bytes 0..2) of `é`
#[test]
fn test_multibyte_leading_char_in_date_type_does_not_panic() {
    let jats_bytes = build_jats_with_history_date_type("\u{e9}");
    let config = ExtractionConfig {
        use_cache: false,
        ..Default::default()
    };

    let outcome = panic::catch_unwind(AssertUnwindSafe(|| {
        extract_bytes_document_blocking(&jats_bytes, JATS_MIME_TYPE, &config)
    }));

    assert!(
        outcome.is_ok(),
        "extraction panicked on history date-type=\"\\u{{e9}}\" instead of failing gracefully"
    );
}

/// Positive control: an ordinary ASCII `date-type="received"` must still produce
/// the exact same metadata subject string it produces today. This is a crash fix,
/// not a behaviour change: the titlecasing of legal, ASCII `date-type` values must
/// be byte-for-byte identical to before the fix.
///
/// The expected string is derived directly from the production assembly in
/// `crates/xberg/src/extractors/jats/mod.rs`:
///   - `metadata.title` is non-empty, so `subject_parts` gets `"Title: {title}"`
///     first (pushed before the `<history>` loop runs).
///   - the single history date then contributes `"{Titlecased date-type}: {date}"`,
///     i.e. `"Received: 2024"` (`"received"` capitalized, `<year>2024</year>` is the
///     only text content of the `<date>` element, so `extract_text_content` yields
///     the trimmed string `"2024"`).
///   - the final `metadata.subject` is `subject_parts.join(" | ")`, overwriting the
///     title-only value set earlier.
#[test]
fn test_well_formed_ascii_date_type_still_produces_expected_subject() {
    let jats_bytes = build_jats_with_history_date_type("received");
    let config = ExtractionConfig {
        use_cache: false,
        ..Default::default()
    };

    let result = extract_bytes_document_blocking(&jats_bytes, JATS_MIME_TYPE, &config)
        .expect("well-formed JATS history date must extract successfully");

    assert_eq!(
        result.metadata.subject.as_deref(),
        Some("Title: Test Article | Received: 2024"),
        "history date-type titlecasing must be unchanged for ASCII input"
    );
}
