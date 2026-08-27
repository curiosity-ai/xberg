//! Registry-routing and `parse_options` tests for the Candle VLM-OCR backends.
//!
//! These tests verify that:
//! - `OcrBackendRegistry::new` seeds each backend under its canonical name.
//! - Unknown backend names return `None` from the registry (via `list()`).
//! - Network-gated e2e tests (tagged `#[ignore]`) drive real inference through the
//!   unified `OcrBackend::process_image` interface with device=auto to respect GPU
//!   acceleration when built with `candle-cuda`.
//!
//! `parse_options` is a private inherent method on each backend, so it cannot be
//! exercised from this external test crate. Its defaults + non-object JSON
//! resilience + empty-object-defaults behaviour is covered by the `#[cfg(test)]`
//! unit tests inside each backend module instead (`glm_ocr_backend.rs`,
//! `deepseek_ocr_backend.rs`, `paddleocr_vl_backend.rs`).
//!
//! Models are gated uniformly via `XBERG_REQUIRE_MODELS`:
//! - When unset or falsy: tests skip gracefully if required weights/env vars are missing.
//! - When set to "1", "true", or "yes": tests panic if weights cannot be obtained.
//!
//! Run just the registry tests (no network required):
//! ```
//! cargo test -p xberg --features candle-vlm-ocr --test candle_backends
//! ```
//!
//! Run e2e tests with local weights (GLM/TrOCR auto-download):
//! ```
//! XBERG_REQUIRE_MODELS=1 \
//! cargo test -p xberg --features candle-vlm-ocr --test candle_backends -- --ignored --nocapture
//! ```
//!
//! Run e2e tests with local-weight models (supply via environment):
//! ```
//! XBERG_REQUIRE_MODELS=1 \
//! XBERG_DEEPSEEK_OCR_MODEL_PATH=/models/deepseek \
//! XBERG_PADDLEOCR_VL_MODEL_PATH=/models/paddleocr-vl \
//! cargo test -p xberg --features candle-vlm-ocr --test candle_backends -- --ignored --nocapture
//! ```

#![allow(clippy::print_stdout, clippy::print_stderr, clippy::dbg_macro)] // ~keep: test/bench binaries print by design; org logging policy exempts tests
#![cfg(feature = "candle-ocr")]

use xberg::core::config::OcrConfig;
use xberg::plugins::registry::OcrBackendRegistry;

fn new_registry_names() -> Vec<String> {
    let registry = OcrBackendRegistry::new();
    registry.list()
}

/// Determine whether missing required weights should cause a panic or a skip.
///
/// When `XBERG_REQUIRE_MODELS` is set to a truthy value ("1", "true", "yes"),
/// missing weights cause a panic, so CI test failures are visible. Otherwise,
/// missing weights cause a graceful skip for local development without model cache.
fn require_models() -> bool {
    matches!(
        std::env::var("XBERG_REQUIRE_MODELS").as_deref(),
        Ok("1" | "true" | "yes")
    )
}

/// Check for a required local model path environment variable.
///
/// - If the env var is set: return the path.
/// - If unset and `XBERG_REQUIRE_MODELS` is truthy: panic with a helpful message.
/// - If unset and `XBERG_REQUIRE_MODELS` is falsy: return None (for graceful skip).
///
/// Only the local-weight models call this; gate it so single-model feature
/// builds (e.g. the per-model GPU matrix) don't see it as dead code.
#[cfg(any(feature = "candle-deepseek-ocr", feature = "candle-paddleocr-vl"))]
fn check_local_model_path(env_var: &str, model_name: &str) -> Option<String> {
    match std::env::var(env_var) {
        Ok(p) => Some(p),
        Err(_) => {
            if require_models() {
                panic!(
                    "{} model path required: set {} env var pointing to local model weights",
                    model_name, env_var
                );
            } else {
                println!("{} not set — skipping {} e2e test", env_var, model_name);
                None
            }
        }
    }
}

/// The global registry seeds a "candle-glm-ocr" backend when the feature is on.
#[cfg(feature = "candle-glm-ocr")]
#[test]
fn registry_resolves_candle_glm_ocr_backend() {
    let names = new_registry_names();
    assert!(
        names.contains(&"candle-glm-ocr".to_string()),
        "Expected 'candle-glm-ocr' in registry; got: {:?}",
        names,
    );
}

/// The global registry seeds a "candle-deepseek-ocr" backend when the feature is on.
#[cfg(all(feature = "candle-deepseek-ocr", not(target_arch = "wasm32")))]
#[test]
fn registry_resolves_candle_deepseek_ocr_backend() {
    let names = new_registry_names();
    assert!(
        names.contains(&"candle-deepseek-ocr".to_string()),
        "Expected 'candle-deepseek-ocr' in registry; got: {:?}",
        names,
    );
}

/// The global registry seeds a "candle-paddleocr-vl" backend when the feature is on.
#[cfg(feature = "candle-paddleocr-vl")]
#[test]
fn registry_resolves_candle_paddleocr_vl_backend() {
    let names = new_registry_names();
    assert!(
        names.contains(&"candle-paddleocr-vl".to_string()),
        "Expected 'candle-paddleocr-vl' in registry; got: {:?}",
        names,
    );
}

/// An unknown candle backend name is not present in the registry.
#[test]
fn registry_returns_none_for_unknown_candle_backend() {
    let names = new_registry_names();
    assert!(
        !names.contains(&"candle-doesnotexist".to_string()),
        "Expected 'candle-doesnotexist' to be absent from registry; got: {:?}",
        names,
    );
}

// Network-gated e2e tests — #[ignore] until env vars are set.

/// End-to-end DeepSeek-OCR extraction through `OcrBackend::process_image`.
///
/// Requires `XBERG_DEEPSEEK_OCR_MODEL_PATH` to point to a local model directory.
/// Uses device=auto to respect GPU acceleration when built with `candle-cuda`.
/// Skip cleanly when the variable is absent (unless XBERG_REQUIRE_MODELS=1).
#[cfg(all(feature = "candle-deepseek-ocr", not(target_arch = "wasm32")))]
#[tokio::test]
#[ignore = "requires XBERG_DEEPSEEK_OCR_MODEL_PATH env var pointing to local model weights"]
async fn candle_deepseek_ocr_e2e_extraction() {
    let model_path = match check_local_model_path("XBERG_DEEPSEEK_OCR_MODEL_PATH", "DeepSeek-OCR") {
        Some(p) => p,
        None => return,
    };

    use xberg::candle_ocr::DeepseekOcrBackend;
    use xberg::plugins::OcrBackend as _;

    let image_bytes = include_bytes!("../../../fixtures/images/test_hello_world.png");

    let backend = DeepseekOcrBackend::new();

    let config = OcrConfig {
        backend_options: Some(serde_json::json!({"model_path": model_path})),
        ..Default::default()
    };

    let result = backend
        .process_image(image_bytes, &config)
        .await
        .expect("DeepseekOcrBackend::process_image should succeed");

    assert!(
        !result.content.is_empty(),
        "DeepSeek-OCR extraction returned empty content"
    );
    assert_eq!(
        result.mime_type.as_ref(),
        "text/markdown",
        "DeepSeek-OCR must emit text/markdown"
    );

    println!(
        "DeepSeek-OCR result ({} chars): {}",
        result.content.len(),
        result.content
    );
}

/// End-to-end PaddleOCR-VL extraction through `OcrBackend::process_image`.
///
/// Requires `XBERG_PADDLEOCR_VL_MODEL_PATH` to point to a local model directory.
/// Uses device=auto to respect GPU acceleration when built with `candle-cuda`.
/// Skip cleanly when the variable is absent (unless XBERG_REQUIRE_MODELS=1).
#[cfg(feature = "candle-paddleocr-vl")]
#[tokio::test]
#[ignore = "requires XBERG_PADDLEOCR_VL_MODEL_PATH env var pointing to local model weights"]
async fn candle_paddleocr_vl_e2e_extraction() {
    let model_path = match check_local_model_path("XBERG_PADDLEOCR_VL_MODEL_PATH", "PaddleOCR-VL") {
        Some(p) => p,
        None => return,
    };

    use xberg::candle_ocr::PaddleOcrVlBackend;
    use xberg::plugins::OcrBackend as _;
    use xberg_candle_ocr::models::PaddleOcrVlTask;

    let image_bytes = include_bytes!("../../../fixtures/images/test_hello_world.png");

    let backend = PaddleOcrVlBackend::new(PaddleOcrVlTask::default());

    let config = OcrConfig {
        backend_options: Some(serde_json::json!({"model_path": model_path})),
        ..Default::default()
    };

    let result = backend
        .process_image(image_bytes, &config)
        .await
        .expect("PaddleOcrVlBackend::process_image should succeed");

    assert!(
        !result.content.is_empty(),
        "PaddleOCR-VL extraction returned empty content"
    );
    assert!(
        result.content.to_lowercase().contains("hello world"),
        "PaddleOCR-VL should read the fixture text, got: {}",
        result.content
    );
    assert_eq!(
        result.mime_type.as_ref(),
        "text/markdown",
        "PaddleOCR-VL must emit text/markdown"
    );

    println!(
        "PaddleOCR-VL result ({} chars): {}",
        result.content.len(),
        result.content
    );
}

/// End-to-end GLM-OCR extraction through `OcrBackend::process_image`.
///
/// GLM-OCR auto-downloads weights from HuggingFace Hub (~3 GB on first run, cached).
/// Uses device=auto to respect GPU acceleration when built with `candle-cuda`.
/// When XBERG_REQUIRE_MODELS=1: fails if download or inference fails (for CI).
/// Otherwise: skips gracefully if the download/inference fails (for local dev).
#[cfg(feature = "candle-glm-ocr")]
#[tokio::test]
#[ignore = "downloads ~3 GB of GLM-OCR weights from HuggingFace Hub on first run"]
async fn candle_glm_ocr_e2e_extraction() {
    use xberg::candle_ocr::GlmOcrBackend;
    use xberg::candle_ocr::glm_ocr_backend::LayoutMode;
    use xberg::plugins::OcrBackend as _;
    use xberg_candle_ocr::models::GlmOcrTask;

    let image_bytes = include_bytes!("../../../fixtures/images/test_hello_world.png");

    let backend = GlmOcrBackend::new(GlmOcrTask::default(), LayoutMode::default());

    let config = OcrConfig::default();

    let result = match backend.process_image(image_bytes, &config).await {
        Ok(r) => r,
        Err(e) => {
            if require_models() {
                panic!("GLM-OCR inference failed (XBERG_REQUIRE_MODELS=1): {}", e);
            } else {
                println!("GLM-OCR inference failed (dev mode); skipping: {}", e);
                return;
            }
        }
    };

    assert!(!result.content.is_empty(), "GLM-OCR extraction returned empty content");
    assert_eq!(
        result.mime_type.as_ref(),
        "text/markdown",
        "GLM-OCR must emit text/markdown"
    );

    println!("GLM-OCR result ({} chars): {}", result.content.len(), result.content);
}

/// End-to-end TrOCR extraction through `OcrBackend::process_image`.
///
/// TrOCR auto-downloads weights from HuggingFace Hub (~1.5 GB on first run, cached).
/// Uses device=auto to respect GPU acceleration when built with `candle-cuda`.
/// When XBERG_REQUIRE_MODELS=1: fails if download or inference fails (for CI).
/// Otherwise: skips gracefully if the download/inference fails (for local dev).
#[cfg(feature = "candle-trocr")]
#[tokio::test]
#[ignore = "downloads ~1.5 GB of TrOCR weights from HuggingFace Hub on first run"]
async fn candle_trocr_e2e_extraction() {
    use xberg::candle_ocr::TrocrBackend;
    use xberg::plugins::OcrBackend as _;
    use xberg_candle_ocr::models::TrocrVariant;

    let image_bytes = include_bytes!("../../../fixtures/images/test_hello_world.png");

    let backend = TrocrBackend::new(TrocrVariant::default());

    let config = OcrConfig::default();

    let result = match backend.process_image(image_bytes, &config).await {
        Ok(r) => r,
        Err(e) => {
            if require_models() {
                panic!("TrOCR inference failed (XBERG_REQUIRE_MODELS=1): {}", e);
            } else {
                println!("TrOCR inference failed (dev mode); skipping: {}", e);
                return;
            }
        }
    };

    assert!(!result.content.is_empty(), "TrOCR extraction returned empty content");
    assert_eq!(result.mime_type.as_ref(), "text/plain", "TrOCR must emit text/plain");

    println!("TrOCR result ({} chars): {}", result.content.len(), result.content);
}
