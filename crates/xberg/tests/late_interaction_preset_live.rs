//! Live validation for the self-hosted ColBERT late-interaction preset.
//!
//! Downloads the `"colbert"` preset's ONNX model (`colbert-small-v1`, 96-dim)
//! from `xberg-io/late-interaction-models` and runs real inference, asserting
//! a well-formed multi-vector (per-token) embedding with the preset's
//! declared dimensionality, and that ColBERT query augmentation pads the
//! query to a fixed token-row count while the document stays unpadded. Opt
//! out on offline dev with `XBERG_SKIP_LIVE_HF=1`.

#![allow(clippy::print_stdout, clippy::print_stderr, clippy::dbg_macro)] // ~keep: test/bench binaries print by design; org logging policy exempts tests
#![cfg(feature = "late-interaction")]

use xberg::MultiVectorEmbedding;
use xberg::core::config::{LateInteractionConfig, LateInteractionModelType};

fn should_skip() -> bool {
    std::env::var("XBERG_SKIP_LIVE_HF").is_ok()
}

/// Iterate `data` as `dim`-wide per-token row slices, mirroring the crate's
/// internal (non-`pub`) `MultiVectorEmbedding::rows` helper — this test lives
/// outside the crate so it cannot call that `pub(crate)` method directly.
fn rows(embedding: &MultiVectorEmbedding) -> impl Iterator<Item = &[f32]> {
    embedding.data.chunks_exact(embedding.dim as usize)
}

#[test]
fn colbert_small_v1_preset_metadata_is_pinned() {
    let preset = xberg::get_late_interaction_preset("colbert").expect("colbert preset must exist");
    assert_eq!(preset.model_repo, "xberg-io/late-interaction-models");
    assert_eq!(preset.model_file, "colbert-small-v1/model.onnx");
    assert!(
        preset.additional_files.is_empty(),
        "colbert-small-v1 ships as a single model.onnx"
    );
    assert_eq!(preset.dim, 96, "colbert-small-v1 emits 96-dim per-token vectors");
    assert_eq!(preset.max_length, 512);
    assert_eq!(preset.query_max_length, 32);
}

#[test]
fn colbert_small_v1_multi_vector_embeds() {
    if should_skip() {
        eprintln!("XBERG_SKIP_LIVE_HF=1, skipping");
        return;
    }
    let preset = xberg::get_late_interaction_preset("colbert").expect("preset must exist");
    assert_eq!(preset.model_repo, "xberg-io/late-interaction-models");
    assert_eq!(preset.model_file, "colbert-small-v1/model.onnx");
    assert_eq!(preset.dim, 96);

    let config = LateInteractionConfig {
        model: LateInteractionModelType::Preset {
            name: "colbert".to_string(),
        },
        ..Default::default()
    };
    let out = xberg::embed_multi_vector(
        vec!["the quick brown fox jumps over the lazy dog".to_string()],
        &config,
        false,
    )
    .expect("late-interaction embed must succeed");

    assert_eq!(out.len(), 1, "one multi-vector embedding per input");
    let mv = &out[0];
    assert!(mv.num_tokens > 0, "multi-vector embedding must have at least one token");
    assert_eq!(mv.dim, 96, "colbert-small-v1 embeddings are 96-dimensional");
    assert!(mv.is_well_formed(), "data length must equal num_tokens * dim");
    assert_eq!(
        mv.data.len() % (mv.dim as usize),
        0,
        "flat buffer length must be a multiple of dim"
    );
    assert!(!mv.data.is_empty(), "multi-vector data must be non-empty");
    assert!(
        mv.data.iter().all(|v| v.is_finite()),
        "all per-token embedding values must be finite"
    );
}

/// Exercises the ColBERT-specific `[Q]`/`[D]` marker insertion and query
/// augmentation: a query and a document of similar surface length must still
/// diverge in token-row count once encoded, because only the query is padded
/// to the preset's fixed `query_max_length`. Also asserts every returned
/// per-token vector is L2-normalized, per `late_interaction::engine::normalize_tokens`.
#[test]
fn colbert_small_v1_query_augmentation_pads_query_not_document() {
    if should_skip() {
        eprintln!("XBERG_SKIP_LIVE_HF=1, skipping");
        return;
    }

    let preset = xberg::get_late_interaction_preset("colbert").expect("colbert preset must exist");

    let config = LateInteractionConfig {
        model: LateInteractionModelType::Preset {
            name: "colbert".to_string(),
        },
        ..Default::default()
    };

    let query_out = xberg::embed_multi_vector(vec!["what is a late-interaction retriever".to_string()], &config, true)
        .unwrap_or_else(|e| panic!("colbert query embed failed: {e}"));
    let doc_out = xberg::embed_multi_vector(
        vec!["ColBERT scores documents with the MaxSim late-interaction operator".to_string()],
        &config,
        false,
    )
    .unwrap_or_else(|e| panic!("colbert document embed failed: {e}"));

    assert_eq!(query_out.len(), 1, "one multi-vector embedding per input text");
    assert_eq!(doc_out.len(), 1, "one multi-vector embedding per input text");

    let query_embedding = &query_out[0];
    let doc_embedding = &doc_out[0];

    assert_eq!(
        query_embedding.dim, preset.dim as u32,
        "query per-token dim must match preset dim"
    );
    assert_eq!(
        doc_embedding.dim, preset.dim as u32,
        "document per-token dim must match preset dim"
    );
    assert!(
        query_embedding.is_well_formed(),
        "query embedding data length must match num_tokens * dim"
    );
    assert!(
        doc_embedding.is_well_formed(),
        "document embedding data length must match num_tokens * dim"
    );

    assert_eq!(
        query_embedding.num_tokens as usize, preset.query_max_length,
        "ColBERT query augmentation must pad the query to exactly query_max_length live token rows"
    );

    assert!(
        doc_embedding.num_tokens >= 4,
        "document embedding must retain at least [CLS], [D] marker, one real token, and [SEP], got {}",
        doc_embedding.num_tokens
    );
    assert!(
        (doc_embedding.num_tokens as usize) < preset.query_max_length,
        "this short document must not reach the query augmentation length"
    );

    for row in rows(query_embedding) {
        let norm: f32 = row.iter().map(|v| v * v).sum::<f32>().sqrt();
        assert!(
            (norm - 1.0).abs() < 1e-2,
            "query token row must be L2-normalized, got norm {norm}"
        );
    }
    for row in rows(doc_embedding) {
        let norm: f32 = row.iter().map(|v| v * v).sum::<f32>().sqrt();
        assert!(
            (norm - 1.0).abs() < 1e-2,
            "document token row must be L2-normalized, got norm {norm}"
        );
    }
}

#[test]
fn gte_moderncolbert_multi_vector_embeds() {
    if should_skip() {
        eprintln!("XBERG_SKIP_LIVE_HF=1, skipping");
        return;
    }
    let preset = xberg::get_late_interaction_preset("gte-moderncolbert").expect("preset must exist");
    assert_eq!(preset.model_repo, "xberg-io/late-interaction-models");
    assert_eq!(preset.model_file, "gte-moderncolbert-v1/model.onnx");
    assert_eq!(preset.dim, 128);

    let config = LateInteractionConfig {
        model: LateInteractionModelType::Preset {
            name: "gte-moderncolbert".to_string(),
        },
        ..Default::default()
    };
    let out = xberg::embed_multi_vector(
        vec!["the quick brown fox jumps over the lazy dog".to_string()],
        &config,
        false,
    )
    .expect("late-interaction embed must succeed");

    assert_eq!(out.len(), 1, "one multi-vector embedding per input");
    let mv = &out[0];
    assert!(mv.num_tokens > 0, "multi-vector embedding must have at least one token");
    assert_eq!(mv.dim, 128, "gte-moderncolbert embeddings are 128-dimensional");
    assert!(mv.is_well_formed(), "data length must equal num_tokens * dim");
    assert_eq!(
        mv.data.len() % (mv.dim as usize),
        0,
        "flat buffer length must be a multiple of dim"
    );
    assert!(!mv.data.is_empty(), "multi-vector data must be non-empty");
    assert!(
        mv.data.iter().all(|v| v.is_finite()),
        "all per-token embedding values must be finite"
    );
}
