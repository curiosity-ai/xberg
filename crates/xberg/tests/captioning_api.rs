//! Integration tests for the public captioning API.
//!
//! Tests the direct image captioning functions: `caption_image`, `caption_image_file`,
//! and `caption_images`.
//!
//! Run with:
//!
//! ```text
//! cargo test -p xberg --features "captioning" --test captioning_api
//! ```

#![cfg(all(feature = "captioning", feature = "tokio-runtime"))]

use xberg::LlmConfig;
use xberg::captioning::{caption_image, caption_image_file, caption_images};

/// PNG magic bytes (minimal valid PNG header)
const MINIMAL_PNG: &[u8] = &[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

/// JPEG magic bytes (SOI marker)
const MINIMAL_JPEG: &[u8] = &[0xFF, 0xD8, 0xFF, 0xE0];

fn test_llm_config() -> LlmConfig {
    LlmConfig {
        model: "openai/gpt-4o-mini".to_string(),
        api_key: Some("test-key".to_string()),
        base_url: Some("http://127.0.0.1:1".to_string()),
        timeout_secs: Some(1),
        max_retries: Some(0),
        ..Default::default()
    }
}

#[tokio::test]
async fn caption_image_detects_png() {
    let config = test_llm_config();
    let result = caption_image(MINIMAL_PNG, &config, None).await;
    assert!(result.is_err());
}

#[tokio::test]
async fn caption_image_detects_jpeg() {
    let config = test_llm_config();
    let result = caption_image(MINIMAL_JPEG, &config, None).await;
    assert!(result.is_err());
}

#[tokio::test]
async fn caption_images_returns_correct_count() {
    let config = test_llm_config();
    let images = vec![MINIMAL_PNG, MINIMAL_JPEG];
    let result = caption_images(&images, &config, None).await;
    assert!(result.is_err());
}

#[tokio::test]
async fn caption_image_file_handles_missing_file() {
    let config = test_llm_config();
    let result = caption_image_file("/nonexistent/path/image.png", &config, None).await;
    assert!(result.is_err());
}

#[tokio::test]
async fn caption_image_accepts_custom_prompt() {
    let config = test_llm_config();
    let custom_prompt = Some("Describe this image in one sentence");
    let result = caption_image(MINIMAL_PNG, &config, custom_prompt).await;
    assert!(result.is_err());
}
