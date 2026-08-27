//! Coverage for per-item `FileExtractionConfig` overrides inside a batch call.
//!
//! This is the one genuine coverage gap left by deleting the dead,
//! `cfg(test)`-only `core::extractor::batch` module (never compiled into a
//! release build) in favor of the production `engine::extract_impl` batch
//! path used by `xberg::extract_batch`. Every other case the old module's
//! tests covered (empty batch, basic file/bytes batch, mixed valid/invalid,
//! all-invalid, large batch, partial results on failure) already has an
//! equivalent test elsewhere in this crate's `tests/` suite exercising the
//! same public `xberg::extract_batch` entry point; per-item config overrides
//! did not.

mod helpers;
use helpers::{BytesInput, UriBatchInput, extract_bytes_documents, extract_uri_documents};

use std::fs::File;
use std::io::Write;
use tempfile::tempdir;
use xberg::{ExtractionConfig, FileExtractionConfig};

fn trim_trailing_newlines(value: &str) -> &str {
    value.trim_end_matches(['\n', '\r'])
}

fn assert_text_content(actual: &str, expected: &str) {
    assert_eq!(
        trim_trailing_newlines(actual),
        expected,
        "Content mismatch after trimming trailing newlines"
    );
}

/// A per-file override on one item of a URI batch must not disturb extraction
/// of that item or any other item in the same batch.
#[tokio::test]
async fn test_batch_extract_file_with_per_file_config_override() {
    let dir = tempdir().unwrap();

    let file1 = dir.path().join("test1.txt");
    let file2 = dir.path().join("test2.txt");
    File::create(&file1).unwrap().write_all(b"content 1").unwrap();
    File::create(&file2).unwrap().write_all(b"content 2").unwrap();

    let config = ExtractionConfig::default();
    let items = vec![
        UriBatchInput {
            path: file1,
            config: Some(FileExtractionConfig {
                force_ocr: Some(true),
                ..Default::default()
            }),
        },
        UriBatchInput {
            path: file2,
            config: None,
        },
    ];

    let results = extract_uri_documents(items, &config).await;

    assert!(results.is_ok());
    let results = results.unwrap();
    assert_eq!(results.len(), 2);
    assert_text_content(&results[0].content, "content 1");
    assert_text_content(&results[1].content, "content 2");
    assert!(results[0].metadata.error.is_none());
    assert!(results[1].metadata.error.is_none());
}

/// A per-item override on one item of a bytes batch must not disturb
/// extraction of that item or any other item in the same batch.
#[tokio::test]
async fn test_batch_extract_bytes_with_per_item_config_override() {
    let config = ExtractionConfig::default();
    let items = vec![
        BytesInput {
            content: b"hello".to_vec(),
            mime_type: "text/plain".to_string(),
            config: None,
        },
        BytesInput {
            content: b"world".to_vec(),
            mime_type: "text/plain".to_string(),
            config: Some(FileExtractionConfig {
                force_ocr: Some(true),
                ..Default::default()
            }),
        },
    ];

    let results = extract_bytes_documents(items, &config).await;

    assert!(results.is_ok());
    let results = results.unwrap();
    assert_eq!(results.len(), 2);
    assert_text_content(&results[0].content, "hello");
    assert_text_content(&results[1].content, "world");
    assert!(results[0].metadata.error.is_none());
    assert!(results[1].metadata.error.is_none());
}

/// Attaching a per-item config override to a batch item must not suppress or
/// alter that item's error when the item is otherwise invalid, and must not
/// affect the sibling item's success.
#[tokio::test]
async fn test_batch_extract_bytes_per_item_config_override_does_not_suppress_error() {
    let config = ExtractionConfig::default();
    let items = vec![
        BytesInput {
            content: b"valid".to_vec(),
            mime_type: "text/plain".to_string(),
            config: None,
        },
        BytesInput {
            content: b"invalid".to_vec(),
            mime_type: "invalid/mime".to_string(),
            config: Some(FileExtractionConfig {
                force_ocr: Some(true),
                ..Default::default()
            }),
        },
    ];

    let results = extract_bytes_documents(items, &config).await;

    assert!(results.is_ok());
    let results = results.unwrap();
    assert_eq!(results.len(), 2);
    assert_text_content(&results[0].content, "valid");
    assert!(results[0].metadata.error.is_none());
    assert!(results[1].metadata.error.is_some());
}
