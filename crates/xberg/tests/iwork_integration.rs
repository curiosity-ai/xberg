//! Integration tests for Apple iWork format extractors.
//!
//! These tests verify that .pages, .numbers, and .key files can be
//! opened, parsed, and produce non-empty text output.

#![allow(clippy::print_stdout, clippy::print_stderr, clippy::dbg_macro)] // ~keep: test/bench binaries print by design; org logging policy exempts tests

mod helpers;

#[cfg(feature = "iwork")]
mod iwork_tests {
    use crate::helpers::{extract_bytes_document, extract_uri_document};
    use std::path::PathBuf;
    use xberg::core::config::{ExtractionConfig, OutputFormat};

    fn test_doc_path(name: &str) -> PathBuf {
        let manifest = env!("CARGO_MANIFEST_DIR");
        PathBuf::from(manifest).join("../../test_documents/iwork").join(name)
    }

    fn ground_truth_path(name: &str) -> PathBuf {
        let manifest = env!("CARGO_MANIFEST_DIR");
        PathBuf::from(manifest)
            .join("../../test_documents/ground_truth/iwork")
            .join(name)
    }

    fn canonical_markdown(content: &str) -> String {
        content
            .lines()
            .map(str::trim)
            .filter(|line| !line.is_empty())
            .map(|line| {
                if !line.starts_with('|') || !line.ends_with('|') {
                    return line.to_string();
                }
                let cells = line
                    .trim_matches('|')
                    .split('|')
                    .map(str::trim)
                    .map(|cell| {
                        if !cell.is_empty() && cell.chars().all(|character| character == '-') {
                            "---"
                        } else {
                            cell
                        }
                    })
                    .collect::<Vec<_>>();
                format!("| {} |", cells.join(" | "))
            })
            .collect::<Vec<_>>()
            .join("\n")
    }

    fn canonical_plain(content: &str) -> String {
        content
            .lines()
            .map(|line| line.split_whitespace().collect::<Vec<_>>().join(" "))
            .filter(|line| !line.is_empty())
            .collect::<Vec<_>>()
            .join("\n")
    }

    #[test]
    fn test_mime_detection_numbers_file() {
        let path = test_doc_path("test.numbers");
        if !path.exists() {
            eprintln!("Skipping: test.numbers not found at {:?}", path);
            return;
        }

        let mime = xberg::core::mime::detect_mime_type(&path, true).unwrap();
        assert_eq!(
            mime, "application/x-iwork-numbers-sffnumbers",
            "Should detect .numbers MIME type from extension"
        );
    }

    #[test]
    fn test_mime_detection_pages_file() {
        let path = test_doc_path("test.pages");
        if !path.exists() {
            eprintln!("Skipping: test.pages not found at {:?}", path);
            return;
        }

        let mime = xberg::core::mime::detect_mime_type(&path, true).unwrap();
        assert_eq!(
            mime, "application/x-iwork-pages-sffpages",
            "Should detect .pages MIME type from extension"
        );
    }

    #[tokio::test]
    #[cfg(feature = "tokio-runtime")]
    async fn test_extract_numbers_document() {
        let path = test_doc_path("test.numbers");
        if !path.exists() {
            eprintln!("Skipping: test.numbers not found at {:?}", path);
            return;
        }

        let content = std::fs::read(&path).expect("Failed to read test.numbers");
        let config = ExtractionConfig::default();

        let result = extract_bytes_document(&content, "application/x-iwork-numbers-sffnumbers", &config)
            .await
            .expect("Extraction should not fail on valid file");

        assert!(
            !result.content.is_empty(),
            "Numbers extraction should produce non-empty text. Got: {:?}",
            &result.content[..result.content.len().min(200)]
        );
    }

    #[tokio::test]
    #[cfg(feature = "tokio-runtime")]
    async fn should_extract_numbers_tables_without_protobuf_noise() {
        let path = test_doc_path("test.numbers");
        let expected_markdown = std::fs::read_to_string(ground_truth_path("numbers_tables.md"))
            .expect("Numbers markdown ground truth should be readable");
        let expected_plain = std::fs::read_to_string(ground_truth_path("numbers_tables.txt"))
            .expect("Numbers plain-text ground truth should be readable");
        let content = std::fs::read(&path).expect("Failed to read test.numbers");
        let markdown_config = ExtractionConfig {
            output_format: OutputFormat::Markdown,
            ..Default::default()
        };
        let plain_config = ExtractionConfig {
            output_format: OutputFormat::Plain,
            ..Default::default()
        };

        let markdown = extract_bytes_document(&content, "application/x-iwork-numbers-sffnumbers", &markdown_config)
            .await
            .expect("Extraction should not fail on valid file");
        let plain = extract_bytes_document(&content, "application/x-iwork-numbers-sffnumbers", &plain_config)
            .await
            .expect("Extraction should not fail on valid file");

        assert_eq!(
            canonical_markdown(&markdown.content),
            canonical_markdown(&expected_markdown)
        );
        assert_eq!(canonical_plain(&plain.content), canonical_plain(&expected_plain));
        assert!(!markdown.content.contains("# Sheet Data"));
        assert!(!markdown.content.contains("&#9;"));
    }

    #[tokio::test]
    #[cfg(feature = "tokio-runtime")]
    async fn should_route_minimal_keynote_package_to_keynote_extractor() {
        let path = test_doc_path("test.key");
        let expected = std::fs::read_to_string(ground_truth_path("keynote_simple.md"))
            .expect("Keynote markdown ground truth should be readable");
        let config = ExtractionConfig {
            output_format: OutputFormat::Markdown,
            ..Default::default()
        };

        let result = extract_uri_document(&path, None, &config)
            .await
            .expect("Extraction should not fail on valid Keynote file");

        assert_eq!(result.mime_type.as_ref(), "application/x-iwork-keynote-sffkey");
        assert_eq!(canonical_markdown(&result.content), canonical_markdown(&expected));
    }

    #[tokio::test]
    #[cfg(feature = "tokio-runtime")]
    async fn test_extract_pages_document() {
        let path = test_doc_path("test.pages");
        if !path.exists() {
            eprintln!("Skipping: test.pages not found at {:?}", path);
            return;
        }

        let content = std::fs::read(&path).expect("Failed to read test.pages");
        let config = ExtractionConfig::default();

        let result = extract_bytes_document(&content, "application/x-iwork-pages-sffpages", &config)
            .await
            .expect("Extraction should not fail on valid ZIP file");

        assert!(
            result.mime_type.as_ref() == "application/x-iwork-pages-sffpages",
            "MIME type should be preserved in result"
        );
    }
}
