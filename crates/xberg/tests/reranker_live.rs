//! Live HuggingFace reranker integration tests.
//!
//! Downloads every preset model from HF Hub and runs real cross-encoder
//! inference against fixture query/document triples. The tests assert:
//!
//! - The preset's `model_file` + `additional_files` exist on the hub (a 404
//!   here means the catalog drifted from the upstream fastembed-rs list and
//!   must be refreshed).
//! - Scores are in the sigmoid range `[0, 1]`.
//! - The ground-truth top-ranked document from each fixture suite actually
//!   ranks first.
//! - `top_k` truncates correctly.
//! - The same model selected via `Preset { name }` and via
//!   `Custom { model_id, ... }` produces byte-equal output (catches drift
//!   between the two code paths).
//!
//! # CI gating
//!
//! These tests are always on. Set `XBERG_HF_CACHE_DIR` to point at a
//! persistent cache directory (the `cache-hf-fastembed` GitHub Action does
//! this automatically); first runs take a few minutes per preset, subsequent
//! runs reuse the cache.
//!
//! Local opt-out (for laptop dev without network): set
//! `XBERG_SKIP_LIVE_HF=1` to skip these tests.

#![allow(clippy::print_stdout, clippy::print_stderr, clippy::dbg_macro)] // ~keep: test/bench binaries print by design; org logging policy exempts tests
#![cfg(all(feature = "reranker", feature = "reranker-presets", feature = "tokio-runtime"))]

use std::path::PathBuf;

use serde::Deserialize;
use xberg::core::config::RerankerModelType;
use xberg::{RerankerConfig, get_reranker_preset, rerank_async};

#[derive(Debug, Deserialize)]
struct FixtureSuite {
    id: String,
    languages: Vec<String>,
    query: String,
    documents: Vec<String>,
    expected_top_index: usize,
    #[allow(dead_code)]
    expected_worst_index: usize,
}

#[derive(Debug, Deserialize)]
struct FixtureFile {
    suites: Vec<FixtureSuite>,
}

fn load_fixture() -> FixtureFile {
    let raw = include_str!("fixtures/preset_live/query_doc_pairs.json");
    serde_json::from_str(raw).expect("query_doc_pairs.json must parse")
}

fn should_skip() -> bool {
    std::env::var("XBERG_SKIP_LIVE_HF").ok().as_deref() == Some("1")
}

fn cache_dir() -> Option<PathBuf> {
    std::env::var("XBERG_HF_CACHE_DIR").ok().map(PathBuf::from)
}

/// Pick the fixture suite(s) whose declared language(s) overlap with the
/// preset's language coverage. Cross-encoders trained only on English will
/// rank multilingual documents poorly; only score them on suites they can
/// reasonably handle.
fn pick_suites<'a>(suites: &'a [FixtureSuite], preset_languages: &[&str]) -> Vec<&'a FixtureSuite> {
    suites
        .iter()
        .filter(|s| s.languages.iter().any(|l| preset_languages.contains(&l.as_str())))
        .collect()
}

async fn run_preset_inference(preset_name: &str, suite: &FixtureSuite) -> xberg::Result<Vec<xberg::RerankedDocument>> {
    let config = RerankerConfig {
        model: RerankerModelType::Preset {
            name: preset_name.to_string(),
        },
        cache_dir: cache_dir(),
        batch_size: 8,
        ..Default::default()
    };
    rerank_async(suite.query.clone(), suite.documents.clone(), &config).await
}

fn assert_scores_in_unit_interval(results: &[xberg::RerankedDocument], context: &str) {
    for r in results {
        assert!(
            r.score >= 0.0 && r.score <= 1.0,
            "{context}: score {} for index {} not in [0,1]",
            r.score,
            r.index
        );
    }
}

fn assert_top_is_expected(results: &[xberg::RerankedDocument], expected_top: usize, suite_id: &str, preset: &str) {
    let actual_top = results.first().expect("non-empty results").index;
    assert_eq!(
        actual_top,
        expected_top,
        "preset {preset} on suite {suite_id}: expected top document index {expected_top}, got {actual_top}. Full ordering: {:?}",
        results.iter().map(|r| (r.index, r.score)).collect::<Vec<_>>(),
    );
}

#[tokio::test(flavor = "multi_thread")]
async fn bge_reranker_base_english_top_ranks_first() {
    if should_skip() {
        eprintln!("XBERG_SKIP_LIVE_HF=1, skipping");
        return;
    }

    let fixture = load_fixture();
    let suites = pick_suites(&fixture.suites, &["en"]);
    assert!(!suites.is_empty(), "must have English fixtures");

    let preset = get_reranker_preset("bge-reranker-base").expect("preset must exist");
    assert_eq!(preset.model_repo, "xberg-io/reranker-models");
    assert_eq!(preset.model_file, "bge-reranker-base/model.onnx");

    for suite in suites {
        let results = run_preset_inference("bge-reranker-base", suite)
            .await
            .unwrap_or_else(|e| panic!("bge-reranker-base on {}: {e}", suite.id));

        assert_eq!(
            results.len(),
            suite.documents.len(),
            "result count must match input count"
        );
        assert_scores_in_unit_interval(&results, &format!("bge-reranker-base / {}", suite.id));
        assert_top_is_expected(&results, suite.expected_top_index, &suite.id, "bge-reranker-base");
    }
}

#[tokio::test(flavor = "multi_thread")]
async fn jina_reranker_v1_turbo_en_english_top_ranks_first() {
    if should_skip() {
        eprintln!("XBERG_SKIP_LIVE_HF=1, skipping");
        return;
    }

    let fixture = load_fixture();
    let suites = pick_suites(&fixture.suites, &["en"]);

    let preset = get_reranker_preset("jina-reranker-v1-turbo-en").expect("preset must exist");
    assert_eq!(preset.model_repo, "xberg-io/reranker-models");
    assert_eq!(preset.model_file, "jina-reranker-v1-turbo-en/model.onnx");

    for suite in suites {
        let results = run_preset_inference("jina-reranker-v1-turbo-en", suite)
            .await
            .unwrap_or_else(|e| panic!("jina-reranker-v1-turbo-en on {}: {e}", suite.id));

        assert_scores_in_unit_interval(&results, &format!("jina-v1-turbo / {}", suite.id));
        assert_top_is_expected(
            &results,
            suite.expected_top_index,
            &suite.id,
            "jina-reranker-v1-turbo-en",
        );
    }
}

#[tokio::test(flavor = "multi_thread")]
async fn ettin_reranker_150m_english_top_ranks_first() {
    if should_skip() {
        eprintln!("XBERG_SKIP_LIVE_HF=1, skipping");
        return;
    }

    let fixture = load_fixture();
    let suites = pick_suites(&fixture.suites, &["en"]);
    assert!(!suites.is_empty(), "must have English fixtures");

    let preset = get_reranker_preset("ettin-reranker-150m").expect("preset must exist");
    assert_eq!(preset.model_repo, "xberg-io/reranker-models");
    assert_eq!(preset.model_file, "ettin-reranker-150m/model.onnx");
    assert!(preset.additional_files.is_empty(), "ettin ships as a single model.onnx");

    for suite in suites {
        let results = run_preset_inference("ettin-reranker-150m", suite)
            .await
            .unwrap_or_else(|e| panic!("ettin-reranker-150m on {}: {e}", suite.id));

        assert_eq!(
            results.len(),
            suite.documents.len(),
            "result count must match input count"
        );
        assert_scores_in_unit_interval(&results, &format!("ettin-reranker-150m / {}", suite.id));
        assert_top_is_expected(&results, suite.expected_top_index, &suite.id, "ettin-reranker-150m");
    }
}

/// `bge-reranker-v2-m3` ships the weights split into `model.onnx` +
/// `model.onnx.data`. This test exists primarily to exercise the
/// `additional_files` download path. We do not require the multilingual
/// suite ordering to be perfect — m3 is large enough that even imperfect
/// downloads load. Instead we assert: the preset's `additional_files`
/// declaration is non-empty AND inference produces non-empty results in the
/// sigmoid range.
#[tokio::test(flavor = "multi_thread")]
async fn bge_reranker_v2_m3_loads_via_additional_files() {
    if should_skip() {
        eprintln!("XBERG_SKIP_LIVE_HF=1, skipping");
        return;
    }

    let preset = get_reranker_preset("bge-reranker-v2-m3").expect("preset must exist");
    assert_eq!(preset.model_repo, "xberg-io/reranker-models");
    assert_eq!(preset.model_file, "bge-reranker-v2-m3/model.onnx");
    assert_eq!(
        preset.additional_files,
        vec!["bge-reranker-v2-m3/model.onnx.data".to_string()],
        "v2-m3 must declare its weight-blob sibling (full repo-relative path)"
    );

    let fixture = load_fixture();
    let suite = fixture
        .suites
        .iter()
        .find(|s| s.id == "english_basic")
        .expect("english_basic suite must exist");

    let results = run_preset_inference("bge-reranker-v2-m3", suite)
        .await
        .unwrap_or_else(|e| panic!("bge-reranker-v2-m3 download/load failed: {e}"));

    assert_eq!(
        results.len(),
        suite.documents.len(),
        "result count must match input count"
    );
    assert_scores_in_unit_interval(&results, "bge-reranker-v2-m3 / english_basic");
    assert_top_is_expected(&results, suite.expected_top_index, &suite.id, "bge-reranker-v2-m3");
}

/// `Preset { name: "..." }` and `Custom { model_id: "<same_repo>", ... }`
/// pointed at the same catalog entry must produce identical scores.
/// Catches accidental divergence between the two resolution paths.
#[tokio::test(flavor = "multi_thread")]
async fn preset_and_custom_are_equivalent_for_same_repo() {
    if should_skip() {
        eprintln!("XBERG_SKIP_LIVE_HF=1, skipping");
        return;
    }

    let fixture = load_fixture();
    let suite = fixture
        .suites
        .iter()
        .find(|s| s.id == "english_basic")
        .expect("english_basic suite must exist");

    let preset_name = "jina-reranker-v1-turbo-en";
    let preset = get_reranker_preset(preset_name).expect("preset must exist");

    let preset_results = {
        let config = RerankerConfig {
            model: RerankerModelType::Preset {
                name: preset_name.to_string(),
            },
            cache_dir: cache_dir(),
            batch_size: 8,
            ..Default::default()
        };
        rerank_async(suite.query.clone(), suite.documents.clone(), &config)
            .await
            .expect("preset path must succeed")
    };

    let custom_results = {
        let config = RerankerConfig {
            model: RerankerModelType::Custom {
                model_id: preset.model_repo.clone(),
                model_file: Some(preset.model_file.clone()),
                additional_files: preset.additional_files.clone(),
                max_length: Some(preset.max_length as i64),
                head: xberg::core::config::reranker::RerankerHead::CrossEncoder,
            },
            cache_dir: cache_dir(),
            batch_size: 8,
            ..Default::default()
        };
        rerank_async(suite.query.clone(), suite.documents.clone(), &config)
            .await
            .expect("custom path must succeed")
    };

    assert_eq!(preset_results.len(), custom_results.len(), "result lengths must match");
    for (p, c) in preset_results.iter().zip(custom_results.iter()) {
        assert_eq!(p.index, c.index, "indices must match: {p:?} vs {c:?}");
        assert!(
            (p.score - c.score).abs() < 1e-5,
            "scores diverge: preset {p:?} vs custom {c:?}",
        );
    }
}

/// `qwen3-reranker-0.6b` is a generative reranker: a causal-LM yes/no head, not
/// a BERT cross-encoder. This exercises the self-hosted custom ONNX export
/// (`[batch, seq, vocab]` logits + `model.onnx.data` sibling), the last-real-token
/// read under right-padding, and the tokenizer-driven yes/no id resolution — all
/// the seams the synthetic unit tests can't cover. Scores are `P(yes)` in `[0, 1]`.
#[tokio::test(flavor = "multi_thread")]
async fn qwen3_reranker_generative_head_top_ranks_first() {
    if should_skip() {
        eprintln!("XBERG_SKIP_LIVE_HF=1, skipping");
        return;
    }

    let preset = get_reranker_preset("qwen3-reranker-0.6b").expect("preset must exist");
    assert_eq!(preset.model_repo, "xberg-io/reranker-models");
    assert_eq!(preset.model_file, "qwen3-reranker-0.6b/model.onnx");
    assert_eq!(
        preset.additional_files,
        vec!["qwen3-reranker-0.6b/model.onnx.data".to_string()],
        "qwen3 must declare its weight-blob sibling (full repo-relative path)"
    );

    let fixture = load_fixture();
    let suite = fixture
        .suites
        .iter()
        .find(|s| s.id == "english_basic")
        .expect("english_basic suite must exist");

    let results = run_preset_inference("qwen3-reranker-0.6b", suite)
        .await
        .unwrap_or_else(|e| panic!("qwen3-reranker-0.6b download/inference failed: {e}"));

    assert_eq!(
        results.len(),
        suite.documents.len(),
        "result count must match input count"
    );
    assert_scores_in_unit_interval(&results, "qwen3-reranker-0.6b / english_basic");
    assert_top_is_expected(&results, suite.expected_top_index, &suite.id, "qwen3-reranker-0.6b");
}

/// `top_k` truncates after sorting — verify the top result is still correct
/// even when only the top-1 is returned.
#[tokio::test(flavor = "multi_thread")]
async fn top_k_returns_only_top_scoring() {
    if should_skip() {
        eprintln!("XBERG_SKIP_LIVE_HF=1, skipping");
        return;
    }

    let fixture = load_fixture();
    let suite = fixture
        .suites
        .iter()
        .find(|s| s.id == "english_technical")
        .expect("english_technical suite must exist");

    let config = RerankerConfig {
        model: RerankerModelType::Preset {
            name: "bge-reranker-base".to_string(),
        },
        top_k: Some(1),
        cache_dir: cache_dir(),
        batch_size: 8,
        ..Default::default()
    };

    let results = rerank_async(suite.query.clone(), suite.documents.clone(), &config)
        .await
        .expect("top_k path must succeed");

    assert_eq!(results.len(), 1, "top_k=1 must yield exactly 1 result");
    assert_eq!(
        results[0].index, suite.expected_top_index,
        "top_k=1 must yield the highest-relevance document"
    );
    assert!(results[0].score >= 0.0 && results[0].score <= 1.0);
}
