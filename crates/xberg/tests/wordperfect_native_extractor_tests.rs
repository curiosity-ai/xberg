//! Integration tests for the native WordPerfect extractor.
//!
//! These drive the full public extraction path — `WordPerfectExtractor::extract`
//! over `ExtractInput::from_bytes` — against the real `.wpd`/`.wp` corpus in the
//! `test_documents` submodule, so they exercise the vendored libwpd/librevenge
//! shim, the DTO decode, and the `InternalDocument` walk together.
//!
//! Assertions are content/structure based (non-empty extracted text, table
//! structure present, error paths never panic) rather than an exact string
//! match against the ground-truth fixtures: the exact markdown rendering of
//! notes/superscripts/tables is the shared derive pipeline's concern and is
//! covered by unit tests here and in `xberg-libwpd`. Every test skips when the
//! submodule is absent or its LFS objects are unfetched.

#![allow(clippy::print_stdout, clippy::print_stderr, clippy::dbg_macro)] // ~keep: test/bench binaries print by design; org logging policy exempts tests
#![cfg(feature = "wordperfect")]

use std::path::PathBuf;
use xberg::ExtractInput;
use xberg::core::config::ExtractionConfig;
use xberg::extractors::WordPerfectExtractor;
use xberg::plugins::DocumentExtractor;

const WPD_MIME: &str = "application/vnd.wordperfect";

fn test_documents() -> PathBuf {
    PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../../test_documents")
}

/// Reads a corpus file, or returns `None` when the submodule is absent or the
/// file is still an unfetched git-LFS pointer (mirrors the libwpd corpus tests).
fn corpus(rel: &str) -> Option<Vec<u8>> {
    let bytes = std::fs::read(test_documents().join(rel)).ok()?;
    if bytes.starts_with(b"version https://git-lfs") {
        return None;
    }
    Some(bytes)
}

async fn extract(rel: &str) -> Option<xberg::types::ExtractedDocument> {
    let bytes = corpus(rel)?;
    let extractor = WordPerfectExtractor;
    let config = ExtractionConfig::default();
    let input = ExtractInput::from_bytes(bytes, WPD_MIME, None);
    Some(
        extractor
            .extract(input, &config)
            .await
            .unwrap_or_else(|e| panic!("{rel}: {e}")),
    )
}

#[tokio::test]
async fn every_corpus_document_extracts_non_empty_text() {
    // WP 4.2 → Corel WP6 plus the Macintosh variants: each must produce some
    // extracted text through the full public path without panicking.
    let docs = [
        "wordperfect/wp42.wp",
        "wordperfect/wp50.wp",
        "wordperfect/wp51.wp",
        "wordperfect/wp6.wp",
        "wordperfect/corel_wp6.wpd",
        "wordperfect/wp_mac1.wpd",
        "wordperfect/wp_mac3.wpd",
    ];
    let mut checked = 0;
    for rel in docs {
        let Some(result) = extract(rel).await else {
            continue;
        };
        assert!(
            result.content.split_whitespace().count() > 0,
            "{rel}: expected extracted text, got {:?}",
            result.content
        );
        assert_eq!(result.mime_type.as_ref(), WPD_MIME, "{rel}: mime propagated");
        checked += 1;
    }
    eprintln!("extracted {checked}/{} documents", docs.len());
}

#[tokio::test]
async fn corel_wp6_yields_tables_through_the_full_path() {
    // corel_wp6 is the structurally richest corpus document (tables, a footnote,
    // superscripts — verified at the DTO layer in `xberg-libwpd`'s ground_truth
    // tests and at the mapping layer in this crate's unit tests). Here we assert
    // the table structure survives the full public path into the structured
    // result; how tables/notes/superscripts are *rendered* into the content
    // string is the shared derive pipeline's concern, not the WordPerfect
    // extractor's.
    let Some(result) = extract("wordperfect/corel_wp6.wpd").await else {
        return;
    };
    assert!(!result.tables.is_empty(), "corel_wp6 should contain at least one table");
    assert!(result.content.split_whitespace().count() > 0, "expected extracted text");
}

#[tokio::test]
async fn cve_documents_are_rejected_or_extracted_without_panicking() {
    // The vendored CVE regression fixtures and a wrong-format `.wpg` must never
    // crash the extractor: either a clean error or a bounded result is fine.
    for rel in [
        "wordperfect/cve_2007_1735_1.wpd",
        "wordperfect/cve_2015_1760_1.wpd",
        "wordperfect/cve_2015_1760_2.wpd",
        "wordperfect/graphic_v1.wpg",
    ] {
        let Some(bytes) = corpus(rel) else {
            continue;
        };
        let extractor = WordPerfectExtractor;
        let config = ExtractionConfig::default();
        let input = ExtractInput::from_bytes(bytes, WPD_MIME, None);
        // Result is intentionally unused: the contract under test is "no panic".
        let _ = extractor.extract(input, &config).await;
    }
}

#[tokio::test]
async fn truncated_document_never_panics() {
    let Some(full) = corpus("wordperfect/wp51.wp") else {
        return;
    };
    // Feed progressively longer truncations; a partial WordPerfect stream must
    // fail gracefully, never panic.
    let step = (full.len() / 8).max(1);
    let mut len = 0;
    while len < full.len() {
        let extractor = WordPerfectExtractor;
        let config = ExtractionConfig::default();
        let input = ExtractInput::from_bytes(full[..len].to_vec(), WPD_MIME, None);
        let _ = extractor.extract(input, &config).await;
        len += step;
    }
}
