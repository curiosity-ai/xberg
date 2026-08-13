//! Live integration tests for liter-llm features.
//!
//! These tests hit real provider APIs and require API keys in the workspace
//! `.env` (`OPENAI_API_KEY`, `ANTHROPIC_API_KEY`, `GEMINI_API_KEY`). Each
//! test skips gracefully when its required key is missing.
//!
//! Run with:
//!
//! ```text
//! cargo test -p xberg --features "liter-llm,pdf" --test llm_integration -- --nocapture --test-threads=1
//! ```
//!
//! `--test-threads=1` keeps concurrent provider calls below rate limits.
//!
//! All tests exercise the **public** extraction surface (`extract_uri_document` +
//! `ExtractionConfig`), matching how downstream callers (xberg-enterprise,
//! xberg-py) invoke the engine.

#![allow(clippy::print_stdout, clippy::print_stderr, clippy::dbg_macro)] // ~keep: test/bench binaries print by design; org logging policy exempts tests
#![cfg(feature = "liter-llm")]

mod helpers;
use helpers::extract_uri_document;

use serde_json::json;
use xberg::core::config::{ExtractionConfig, LlmConfig, OcrConfig, StructuredExtractionConfig, VlmFallbackPolicy};
use xberg::{ExtractInput, extract};

const MEMO_PDF: &str = "../../test_documents/pdf/fake_memo.pdf";
const HELLO_PNG: &str = "../../test_documents/images/test_hello_world.png";
const SCANNED_PDF: &str = "../../test_documents/pdf_scanned/nougat_001_scanned.pdf";

macro_rules! require_env {
    ($var:expr) => {
        match std::env::var($var) {
            Ok(val) if !val.is_empty() => val,
            _ => {
                eprintln!("SKIP: {} not set, skipping live integration test", $var);
                return;
            }
        }
    };
}

fn init() {
    let _ = dotenvy::dotenv();
}

fn llm(model: &str, api_key: String) -> LlmConfig {
    LlmConfig {
        model: model.to_string(),
        api_key: Some(api_key),
        timeout_secs: Some(120),
        max_retries: Some(2),
        ..Default::default()
    }
}

fn memo_schema() -> serde_json::Value {
    json!({
        "type": "object",
        "properties": {
            "title": { "type": "string" },
            "date": { "type": "string" },
            "summary": { "type": "string" }
        },
        "required": ["title", "date", "summary"],
        "additionalProperties": false
    })
}

async fn run_vlm_ocr(model: &str, api_key: String) {
    let config = ExtractionConfig {
        ocr: Some(OcrConfig {
            backend: "vlm".to_string(),
            language: vec!["eng".to_string()],
            vlm_config: Some(llm(model, api_key)),
            ..Default::default()
        }),
        force_ocr: true,
        ..Default::default()
    };
    let result = extract_uri_document(HELLO_PNG, None, &config)
        .await
        .expect("VLM OCR extraction failed");
    assert!(
        result.content.to_lowercase().contains("hello"),
        "expected 'hello' in OCR result, got: {}",
        result.content
    );
}

#[tokio::test]
async fn test_vlm_ocr_openai() {
    init();
    let api_key = require_env!("OPENAI_API_KEY");
    run_vlm_ocr("openai/gpt-4o-mini", api_key).await;
}

#[tokio::test]
async fn test_vlm_ocr_anthropic() {
    init();
    let api_key = require_env!("ANTHROPIC_API_KEY");
    run_vlm_ocr("anthropic/claude-haiku-4-5-20251001", api_key).await;
}

#[tokio::test]
async fn test_vlm_ocr_gemini() {
    init();
    let api_key = require_env!("GEMINI_API_KEY");
    run_vlm_ocr("gemini/gemini-2.5-flash", api_key).await;
}

async fn run_structured(model: &str, api_key: String, strict: bool) {
    let config = ExtractionConfig {
        structured_extraction: Some(StructuredExtractionConfig {
            schema: memo_schema(),
            schema_name: "memo_data".to_string(),
            schema_description: Some("Extract memo metadata".to_string()),
            strict,
            prompt: None,
            llm: llm(model, api_key),
        }),
        ..Default::default()
    };
    let output = extract(ExtractInput::from_uri(MEMO_PDF), &config)
        .await
        .expect("structured extraction failed");
    assert_eq!(output.summary.inputs, 1);
    assert_eq!(output.summary.results, 1);
    assert_eq!(output.summary.errors, 0);
    assert!(
        output.errors.is_empty(),
        "structured extraction returned public errors: {:?}",
        output.errors
    );

    let result = output.results.first().expect("expected one public extraction result");
    let output = result
        .structured_output
        .as_ref()
        .expect("expected structured_output to be populated");
    assert!(output.is_object(), "expected JSON object, got: {output}");
    assert!(output.get("title").is_some(), "expected 'title' in result: {output}");
    let usage = result
        .llm_usage
        .as_ref()
        .expect("expected llm_usage populated for a structured-extraction run");
    assert!(!usage.is_empty(), "llm_usage was Some but empty");
    assert!(
        usage.iter().any(|u| u.source == "structured_extraction"),
        "expected at least one usage entry with source=structured_extraction, got {:?}",
        usage.iter().map(|u| u.source.as_str()).collect::<Vec<_>>()
    );
}

#[tokio::test]
async fn test_structured_extraction_openai() {
    init();
    let api_key = require_env!("OPENAI_API_KEY");
    run_structured("openai/gpt-4o-mini", api_key, true).await;
}

#[tokio::test]
async fn test_structured_extraction_anthropic() {
    init();
    let api_key = require_env!("ANTHROPIC_API_KEY");
    run_structured("anthropic/claude-haiku-4-5-20251001", api_key, false).await;
}

#[tokio::test]
async fn test_structured_extraction_gemini() {
    init();
    let api_key = require_env!("GEMINI_API_KEY");
    run_structured("gemini/gemini-2.5-flash", api_key, false).await;
}

#[tokio::test]
async fn test_structured_extraction_custom_prompt() {
    init();
    let api_key = require_env!("OPENAI_API_KEY");
    let config = ExtractionConfig {
        structured_extraction: Some(StructuredExtractionConfig {
            schema: json!({
                "type": "object",
                "properties": {
                    "word_count": { "type": "integer" },
                    "language": { "type": "string" }
                },
                "required": ["word_count", "language"],
                "additionalProperties": false
            }),
            schema_name: "doc_stats".to_string(),
            schema_description: None,
            strict: true,
            prompt: Some(
                "Analyze this document and return statistics.\n\n\
                 Document:\n{{ content }}\n\n\
                 Return JSON with word_count and language."
                    .to_string(),
            ),
            llm: llm("openai/gpt-4o-mini", api_key),
        }),
        ..Default::default()
    };
    let public_output = extract(ExtractInput::from_uri(MEMO_PDF), &config)
        .await
        .expect("structured extraction with custom prompt failed");
    assert_eq!(public_output.summary.inputs, 1);
    assert_eq!(public_output.summary.results, 1);
    assert_eq!(public_output.summary.errors, 0);
    assert!(
        public_output.errors.is_empty(),
        "structured extraction returned public errors: {:?}",
        public_output.errors
    );

    let result = public_output
        .results
        .first()
        .expect("expected one public extraction result");
    let output = result.structured_output.as_ref().expect("structured_output missing");
    assert!(output.is_object(), "expected JSON object: {output}");
    assert!(output.get("word_count").is_some(), "missing word_count");
    assert!(output.get("language").is_some(), "missing language");
}

#[tokio::test]
async fn test_vlm_fallback_always_routes_to_vlm() {
    init();
    let api_key = require_env!("OPENAI_API_KEY");
    let config = ExtractionConfig {
        ocr: Some(OcrConfig {
            backend: "tesseract".to_string(),
            language: vec!["eng".to_string()],
            vlm_fallback: VlmFallbackPolicy::Always,
            vlm_config: Some(llm("openai/gpt-4o-mini", api_key)),
            ..Default::default()
        }),
        force_ocr: true,
        extraction_timeout_secs: Some(300),
        ..Default::default()
    };
    let result = extract_uri_document(SCANNED_PDF, None, &config)
        .await
        .expect("VlmFallbackPolicy::Always extraction failed");
    assert!(
        !result.content.trim().is_empty(),
        "VlmFallbackPolicy::Always produced empty content"
    );
    let usage = result
        .llm_usage
        .expect("expected llm_usage populated when VLM fallback ran");
    assert!(
        usage.iter().any(|u| u.source == "vlm_ocr"),
        "expected vlm_ocr LlmUsage entry, got sources {:?}",
        usage.iter().map(|u| u.source.as_str()).collect::<Vec<_>>()
    );
}

#[tokio::test]
async fn test_vlm_fallback_on_low_quality() {
    init();
    let api_key = require_env!("OPENAI_API_KEY");
    let config = ExtractionConfig {
        ocr: Some(OcrConfig {
            backend: "tesseract".to_string(),
            language: vec!["eng".to_string()],
            vlm_fallback: VlmFallbackPolicy::OnLowQuality {
                quality_threshold: 0.95,
            },
            vlm_config: Some(llm("openai/gpt-4o-mini", api_key)),
            ..Default::default()
        }),
        force_ocr: true,
        extraction_timeout_secs: Some(300),
        ..Default::default()
    };
    let result = extract_uri_document(SCANNED_PDF, None, &config)
        .await
        .expect("VlmFallbackPolicy::OnLowQuality extraction failed");
    assert!(
        !result.content.trim().is_empty(),
        "OnLowQuality fallback produced empty content"
    );
    if let Some(usage) = result.llm_usage {
        assert!(
            usage.iter().any(|u| u.source == "vlm_ocr"),
            "llm_usage present but no vlm_ocr source: {:?}",
            usage.iter().map(|u| u.source.as_str()).collect::<Vec<_>>()
        );
    }
}

#[tokio::test]
async fn test_vlm_fallback_disabled_does_not_call_llm() {
    init();
    {
        use xberg::plugins::registry::get_ocr_backend_registry;
        let registry = get_ocr_backend_registry();
        let registry = registry.read();
        if !registry.list().iter().any(|n| n == "tesseract") {
            eprintln!("SKIP: tesseract backend not registered in this feature set");
            return;
        }
    }
    let config = ExtractionConfig {
        ocr: Some(OcrConfig {
            backend: "tesseract".to_string(),
            language: vec!["eng".to_string()],
            vlm_fallback: VlmFallbackPolicy::Disabled,
            vlm_config: None,
            ..Default::default()
        }),
        force_ocr: true,
        extraction_timeout_secs: Some(300),
        ..Default::default()
    };
    let result = extract_uri_document(SCANNED_PDF, None, &config)
        .await
        .expect("Disabled-fallback extraction failed");
    assert!(
        result.llm_usage.as_ref().is_none_or(|u| u.is_empty()),
        "Disabled policy must not produce LLM usage records, got {:?}",
        result.llm_usage
    );
}
