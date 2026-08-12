//! Prove the extraction *pipeline* runs the summarisation post-processor.
//!
//! `textrank_summarization.rs` and `llm_summarization_smoke.rs` between them cover the
//! processor thoroughly — but every one of those tests calls
//! `SummarizationProcessor::process(&mut result, &config)` directly against a hand-built
//! `ExtractedDocument`. None of them goes through `xberg::extract`, so none of them can
//! observe whether the pipeline ever *invokes* the processor. That is precisely the gap
//! the Python e2e (`summarization_extractive_smoke`) falls into when it reports
//! `results[0].summary is None`: a green processor suite next to a red end-to-end run.
//!
//! These tests close it at the lowest layer that can still see the defect, so a binding
//! failure is not the first place we learn about it.
//!
//! `use_cache` is forced off: the result cache is keyed on content plus config, and a
//! cached entry from an earlier no-summarisation run of the same document would mask the
//! very thing under test.

#![cfg(feature = "summarization")]
#![allow(clippy::print_stdout, clippy::print_stderr, clippy::dbg_macro)] // ~keep: test/bench binaries print by design; org logging policy exempts tests

use std::path::{Path, PathBuf};

use xberg::core::config::{ExtractionConfig, SummarizationConfig};
use xberg::types::summary::SummaryStrategy;
use xberg::{ExtractInput, extract};

/// The document the `summarization_extractive_smoke` fixture serves over its mock server.
const FIXTURE_RELATIVE_PATH: &str = "test_documents/text/book_war_and_peace_1p.txt";

/// The token budget the fixture asks for.
const FIXTURE_MAX_TOKENS: u32 = 80;

fn repo_root() -> PathBuf {
    Path::new(env!("CARGO_MANIFEST_DIR"))
        .parent()
        .and_then(Path::parent)
        .expect("crates/xberg has a grandparent")
        .to_path_buf()
}

fn extractive_config() -> ExtractionConfig {
    ExtractionConfig {
        summarization: Some(SummarizationConfig {
            strategy: SummaryStrategy::Extractive,
            max_tokens: Some(FIXTURE_MAX_TOKENS),
            llm: None,
        }),
        // See the module docs: a cache hit would answer from a run that never summarised.
        use_cache: false,
        ..Default::default()
    }
}

#[tokio::test]
async fn extract_populates_summary_when_summarization_is_configured() {
    let document = repo_root().join(FIXTURE_RELATIVE_PATH);
    if !document.is_file() {
        eprintln!(
            "SKIP: {} is absent — run `python3 test_documents/scripts/fetch_corpus.py`",
            document.display()
        );
        return;
    }

    let result = extract(ExtractInput::from_uri(document.to_string_lossy()), &extractive_config())
        .await
        .expect("extraction of a plain-text document succeeds");

    let document_result = result.results.first().expect("one input yields one result");

    // Surface the processor's own diagnostic before asserting, so a failure names the
    // cause instead of only the symptom. `apply_extractive_summary` pushes this warning
    // when TextRank produced nothing (#257).
    let warnings: Vec<String> = document_result
        .processing_warnings
        .iter()
        .map(|warning| format!("{warning:?}"))
        .collect();

    let summary = document_result.summary.as_ref().unwrap_or_else(|| {
        panic!(
            "the pipeline did not populate `summary` for {} even though \
             `config.summarization` was Some(Extractive). processing_warnings = {warnings:#?}",
            document.display()
        )
    });

    assert_eq!(
        summary.strategy,
        SummaryStrategy::Extractive,
        "the extractive strategy must be recorded on the summary it produced"
    );
    assert!(
        !summary.text.trim().is_empty(),
        "an extractive summary must carry text; got {:?}",
        summary.text
    );
    assert!(
        summary.text.len() < document_result.content.len(),
        "a summary must be shorter than the document it summarises ({} vs {} bytes)",
        summary.text.len(),
        document_result.content.len()
    );
}

/// The same request over HTTP, which is what the e2e fixture actually does.
///
/// `summarization_extractive_smoke` serves the document from a mock server and passes
/// `{"kind":"uri","uri":"$mock_url/text/book_war_and_peace_1p.txt"}`. A local filesystem
/// path and an `http://` URL take different routes into the extractor, so a local-path
/// test passing says nothing about the remote one — and the remote one is the path the
/// failing binding e2e exercises.
///
/// Gated on `url-ingestion`: without it there is no HTTP fetch path to exercise, and
/// `UrlExtractionConfig::crawl` — which this test has to reach to relax the SSRF policy —
/// does not exist at all (it is `#[cfg(any(feature = "url-ingestion", feature =
/// "url-config-types"))]` in `core/config/extraction/types.rs:307`).
#[cfg(feature = "url-ingestion")]
#[tokio::test]
async fn extract_populates_summary_for_a_remote_uri() {
    let document = repo_root().join(FIXTURE_RELATIVE_PATH);
    if !document.is_file() {
        eprintln!("SKIP: {} is absent", document.display());
        return;
    }
    let body = std::fs::read_to_string(&document).expect("fixture is readable utf-8");

    let server = wiremock::MockServer::start().await;
    wiremock::Mock::given(wiremock::matchers::method("GET"))
        .and(wiremock::matchers::path("/text/book_war_and_peace_1p.txt"))
        .respond_with(
            wiremock::ResponseTemplate::new(200)
                .set_body_string(body)
                .insert_header("content-type", "application/octet-stream"),
        )
        .mount(&server)
        .await;

    // wiremock binds loopback, which crawlberg's default SSRF policy rejects
    // (`deny_private`). Relaxing it is what makes the test reach the extractor at all;
    // it is not part of what is under test. ~keep
    let mut config = extractive_config();
    config.url.crawl.ssrf.deny_private = false;

    let uri = format!("{}/text/book_war_and_peace_1p.txt", server.uri());
    let result = extract(ExtractInput::from_uri(uri.clone()), &config)
        .await
        .expect("extraction over http succeeds");

    let document_result = result.results.first().expect("one input yields one result");
    let warnings: Vec<String> = document_result
        .processing_warnings
        .iter()
        .map(|warning| format!("{warning:?}"))
        .collect();

    let summary = document_result.summary.as_ref().unwrap_or_else(|| {
        panic!(
            "the pipeline did not populate `summary` for the remote uri {uri} even though \
             `config.summarization` was Some(Extractive) — this is the e2e's exact path. \
             processing_warnings = {warnings:#?}"
        )
    });

    assert_eq!(summary.strategy, SummaryStrategy::Extractive);
    assert!(
        !summary.text.trim().is_empty(),
        "remote extraction must still summarise"
    );
}

/// The negative half: without the config the pipeline must leave `summary` unset.
///
/// Without this, the test above would still pass if some other stage populated `summary`
/// unconditionally, which would make it evidence of nothing.
#[tokio::test]
async fn extract_leaves_summary_unset_when_summarization_is_not_configured() {
    let document = repo_root().join(FIXTURE_RELATIVE_PATH);
    if !document.is_file() {
        eprintln!("SKIP: {} is absent", document.display());
        return;
    }

    let config = ExtractionConfig {
        use_cache: false,
        ..Default::default()
    };
    let result = extract(ExtractInput::from_uri(document.to_string_lossy()), &config)
        .await
        .expect("extraction of a plain-text document succeeds");

    let document_result = result.results.first().expect("one input yields one result");
    assert!(
        document_result.summary.is_none(),
        "summary must stay unset when no summarization config is supplied, got {:?}",
        document_result.summary
    );
}
