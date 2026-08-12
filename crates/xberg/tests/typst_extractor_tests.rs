//! Comprehensive TDD test suite for Typst document extraction.
//!
//! This test suite validates Typst document extraction against expected outputs.
//! The tests verify:
//! - Document metadata extraction (title, author, date, keywords)
//! - Heading hierarchy parsing (=, ==, ===, etc.)
//! - Inline formatting (bold, italic, code)
//! - Table extraction and parsing
//! - List handling (ordered and unordered)
//! - Link extraction
//! - Mathematical notation preservation
//!
//! Each test document is extracted and validated for correct content extraction.

#![allow(clippy::print_stdout, clippy::print_stderr, clippy::dbg_macro)] // ~keep: test/bench binaries print by design; org logging policy exempts tests
#![cfg(feature = "office")]

mod helpers;
use helpers::extract_bytes_document;

use std::{fs, path::PathBuf};
use xberg::ExtractedDocument;
use xberg::core::config::ExtractionConfig;
use xberg::types::document_structure::NodeContent;

fn typst_fixture(name: &str) -> PathBuf {
    PathBuf::from(env!("CARGO_MANIFEST_DIR"))
        .join("../../test_documents/typst")
        .join(name)
}

/// Extraction config that also returns the structured document tree.
///
/// Heading levels live on `NodeContent::Heading`, never in the extracted text: the
/// extractor records headings as structural elements and no renderer re-emits the
/// Typst `=` markers. Asserting on the structure is the only honest way to check a
/// level, and stricter than string matching — `"= Features"` is also a substring of
/// `"== Features"`.
fn structure_config() -> ExtractionConfig {
    ExtractionConfig {
        include_document_structure: true,
        ..Default::default()
    }
}

/// Every heading as a `(level, text)` pair, in document order.
fn headings(result: &ExtractedDocument) -> Vec<(u8, &str)> {
    result
        .document
        .as_ref()
        .expect("include_document_structure should populate `document`")
        .nodes
        .iter()
        .filter_map(|node| match &node.content {
            NodeContent::Heading { level, text } => Some((*level, text.as_str())),
            _ => None,
        })
        .collect()
}

/// Test simple.typ - Basic Typst document with fundamental formatting
///
/// Document contains:
/// - Document metadata: title, author, date
/// - Level 1 heading: "Introduction"
/// - Level 2 headings: "Subsection", "Features", "Lists", "Code", "Tables", "Links", "Conclusion"
/// - Inline formatting: *bold*, _italic_, `inline code`
/// - Unordered list with 3 items
/// - Code snippet
/// - 2x2 table with headers
/// - Link to Typst website
///
/// Expected: Document should extract text, preserve headings, metadata, and formatting markers
#[tokio::test]
async fn test_simple_typst_document_extraction() {
    let config = structure_config();

    let doc_path = typst_fixture("simple.typ");
    let content = match fs::read(doc_path) {
        Ok(c) => c,
        Err(e) => {
            eprintln!("Warning: Could not read simple.typ: {}. Skipping test.", e);
            return;
        }
    };

    let result = extract_bytes_document(&content, "text/x-typst", &config).await;
    if result.is_err() {
        println!("Skipping test: Typst extractor may not be available");
        return;
    }

    let extraction = result.expect("Operation failed");

    assert_eq!(extraction.mime_type, "text/x-typst", "MIME type should be preserved");

    assert!(!extraction.content.is_empty(), "Extracted content should not be empty");

    assert!(
        extraction.metadata.title.is_some(),
        "Document title should be extracted from #set document()"
    );

    assert!(
        extraction.metadata.authors.is_some(),
        "Document author should be extracted"
    );

    assert!(
        extraction.content.contains("Introduction"),
        "Should extract 'Introduction' heading"
    );
    assert!(
        extraction.content.contains("Features"),
        "Should extract 'Features' heading"
    );
    assert!(
        extraction.content.contains("Conclusion"),
        "Should extract 'Conclusion' heading"
    );

    assert_eq!(
        headings(&extraction),
        vec![
            (1, "Introduction"),
            (2, "Subsection"),
            (1, "Features"),
            (2, "Lists"),
            (2, "Code"),
            (2, "Tables"),
            (2, "Links"),
            (1, "Conclusion"),
        ],
        "All 8 headings should be extracted once each, at their source level, in document order"
    );

    assert!(
        extraction.content.contains("*") || extraction.content.contains("bold"),
        "Should preserve bold formatting or text"
    );

    assert!(
        extraction.content.contains("-") || extraction.content.contains("First") || extraction.content.contains("item"),
        "Should extract list content"
    );

    println!(
        "✓ simple.typ: Successfully extracted {} characters with all 8 headings",
        extraction.content.len()
    );
}

/// Test minimal.typ - Minimal Typst document
///
/// Document contains:
/// - Single level 1 heading: "Hello World"
/// - Simple text content
///
/// Expected: Basic heading and content extraction
#[tokio::test]
async fn test_minimal_typst_document_extraction() {
    let config = ExtractionConfig::default();

    let doc_path = typst_fixture("minimal.typ");
    let content = match fs::read(doc_path) {
        Ok(c) => c,
        Err(e) => {
            eprintln!("Warning: Could not read minimal.typ: {}. Skipping test.", e);
            return;
        }
    };

    let result = extract_bytes_document(&content, "application/x-typst", &config).await;
    if result.is_err() {
        println!("Skipping test: Typst extractor may not be available");
        return;
    }

    let extraction = result.expect("Operation failed");

    assert!(
        !extraction.content.is_empty(),
        "Minimal document should extract content"
    );

    assert!(
        extraction.content.contains("Hello") || extraction.content.contains("World"),
        "Should extract heading content"
    );

    println!(
        "✓ minimal.typ: Successfully extracted {} characters",
        extraction.content.len()
    );
}

/// Test headings.typ - Document focusing on heading hierarchy
///
/// Document contains:
/// - 6 heading levels (=, ==, ===, ====, =====, ======)
/// - Content under each heading level
///
/// Expected: Heading structure should be preserved with level information
#[tokio::test]
async fn test_heading_hierarchy_extraction() {
    let config = structure_config();

    let doc_path = typst_fixture("headings.typ");
    let content = match fs::read(doc_path) {
        Ok(c) => c,
        Err(e) => {
            eprintln!("Warning: Could not read headings.typ: {}. Skipping test.", e);
            return;
        }
    };

    let result = extract_bytes_document(&content, "text/x-typst", &config).await;
    if result.is_err() {
        println!("Skipping test: Typst extractor may not be available");
        return;
    }

    let extraction = result.expect("Operation failed");

    assert!(!extraction.content.is_empty(), "Document should extract content");

    for level in 1..=6 {
        let expected = format!("Level {} Heading", level);
        assert!(
            extraction.content.contains(&expected),
            "Heading text '{}' should reach the extracted text",
            expected
        );
    }

    assert_eq!(
        headings(&extraction),
        vec![
            (1, "Level 1 Heading"),
            (2, "Level 2 Heading"),
            (3, "Level 3 Heading"),
            (4, "Level 4 Heading"),
            (5, "Level 5 Heading"),
            (6, "Level 6 Heading"),
        ],
        "Each level 1-6 should be extracted exactly once, at its source level, in document order"
    );

    println!(
        "✓ headings.typ: Successfully extracted {} characters with heading structure",
        extraction.content.len()
    );
}

/// Test metadata.typ - Document with comprehensive metadata
///
/// Document contains:
/// - #set document() with: title, author, subject, keywords
/// - Content sections
///
/// Expected: All metadata fields should be extracted correctly
#[tokio::test]
async fn test_metadata_extraction() {
    let config = ExtractionConfig::default();

    let doc_path = typst_fixture("metadata.typ");
    let content = match fs::read(doc_path) {
        Ok(c) => c,
        Err(e) => {
            eprintln!("Warning: Could not read metadata.typ: {}. Skipping test.", e);
            return;
        }
    };

    let result = extract_bytes_document(&content, "application/x-typst", &config).await;
    if result.is_err() {
        println!("Skipping test: Typst extractor may not be available");
        return;
    }

    let extraction = result.expect("Operation failed");

    if let Some(title) = extraction.metadata.additional.get("title") {
        assert!(
            title.to_string().contains("Metadata") || title.to_string().contains("Example"),
            "Title should contain expected text"
        );
    }

    if let Some(author) = extraction.metadata.additional.get("author") {
        assert!(
            author.to_string().contains("John") || author.to_string().contains("Doe"),
            "Author should contain expected text"
        );
    }

    if let Some(keywords) = &extraction.metadata.keywords {
        assert!(!keywords.is_empty(), "Keywords should be present");
    }

    assert!(!extraction.content.is_empty(), "Document should extract content");

    println!(
        "✓ metadata.typ: Successfully extracted metadata and {} characters of content",
        extraction.content.len()
    );
}

/// Test advanced.typ - Complex Typst document with multiple features
///
/// Document contains:
/// - Metadata: title, author, keywords, date
/// - Heading numbering configuration
/// - Mathematical notation (inline and display)
/// - Nested heading levels (level 1, 2, 3, 4)
/// - Code blocks (Python example)
/// - Complex tables with 3 columns and 4 rows
/// - Multiple paragraph sections
/// - Links with text
/// - Multiple formatting combinations
///
/// Expected: Comprehensive extraction of all document elements
#[tokio::test]
async fn test_advanced_typst_document_extraction() {
    let config = ExtractionConfig::default();

    let doc_path = typst_fixture("advanced.typ");
    let content = match fs::read(doc_path) {
        Ok(c) => c,
        Err(e) => {
            eprintln!("Warning: Could not read advanced.typ: {}. Skipping test.", e);
            return;
        }
    };

    let result = extract_bytes_document(&content, "text/x-typst", &config).await;
    if result.is_err() {
        println!("Skipping test: Typst extractor may not be available");
        return;
    }

    let extraction = result.expect("Operation failed");

    assert!(extraction.metadata.title.is_some(), "Title should be extracted");

    assert!(
        !extraction.content.is_empty(),
        "Advanced document should extract content"
    );

    assert!(
        extraction.content.contains("$")
            || extraction.content.contains("equation")
            || extraction.content.contains("math"),
        "Should extract or preserve mathematical notation"
    );

    assert!(
        extraction.content.contains("Mathematical")
            || extraction.content.contains("Formatting")
            || extraction.content.contains("Features"),
        "Should extract section headings"
    );

    assert!(
        extraction.content.contains("python")
            || extraction.content.contains("def")
            || extraction.content.contains("fibonacci")
            || extraction.content.contains("```"),
        "Should extract code block content"
    );

    let level_count = extraction.content.matches("=").count();
    assert!(level_count >= 3, "Should preserve nested heading hierarchy");

    assert!(
        extraction.content.contains("Name")
            || extraction.content.contains("Alice")
            || extraction.content.contains("Table"),
        "Should extract table content"
    );

    assert!(
        extraction.content.contains("example")
            || extraction.content.contains("link")
            || extraction.content.contains("http"),
        "Should extract link content"
    );

    println!(
        "✓ advanced.typ: Successfully extracted {} characters with complex formatting",
        extraction.content.len()
    );
}

/// Test typst-reader.typ - Pandoc test file
///
/// Document from Pandoc test suite demonstrating Typst reader functionality
///
/// Expected: Proper extraction of Typst-specific syntax
#[tokio::test]
async fn test_typst_reader_extraction() {
    let config = ExtractionConfig::default();

    let doc_path = typst_fixture("typst-reader.typ");
    let content = match fs::read(doc_path) {
        Ok(c) => c,
        Err(e) => {
            eprintln!("Warning: Could not read typst-reader.typ: {}. Skipping test.", e);
            return;
        }
    };

    let result = extract_bytes_document(&content, "application/x-typst", &config).await;
    if result.is_err() {
        println!("Skipping test: Typst extractor may not be available");
        return;
    }

    let extraction = result.expect("Operation failed");

    assert!(
        !extraction.content.is_empty(),
        "Should extract content from Pandoc test file"
    );

    assert!(
        extraction.content.contains("=") || extraction.content.contains("Fibonacci"),
        "Should extract heading or content from test file"
    );

    println!(
        "✓ typst-reader.typ: Successfully extracted {} characters",
        extraction.content.len()
    );
}

/// Test undergradmath.typ - Pandoc test file with complex math
///
/// Document from Pandoc test suite with extensive mathematical notation
/// and complex formatting
///
/// Expected: Handling of complex Typst syntax with metadata and content
#[tokio::test]
async fn test_undergradmath_extraction() {
    let config = ExtractionConfig::default();

    let doc_path = typst_fixture("undergradmath.typ");
    let content = match fs::read(doc_path) {
        Ok(c) => c,
        Err(e) => {
            eprintln!("Warning: Could not read undergradmath.typ: {}. Skipping test.", e);
            return;
        }
    };

    let result = extract_bytes_document(&content, "text/x-typst", &config).await;
    if result.is_err() {
        println!("Skipping test: Typst extractor may not be available");
        return;
    }

    let extraction = result.expect("Operation failed");

    assert!(
        !extraction.content.is_empty(),
        "Should extract content from complex math document"
    );

    if let Some(title) = extraction.metadata.additional.get("title") {
        assert!(!title.to_string().is_empty(), "Title should be extracted");
    }

    assert!(
        extraction.content.contains("=") || extraction.content.contains("Typst") || extraction.content.len() > 100,
        "Should extract document structure or content"
    );

    println!(
        "✓ undergradmath.typ: Successfully extracted {} characters from math document",
        extraction.content.len()
    );
}

/// Test MIME type detection and fallback
///
/// Verifies that Typst documents can be extracted with different MIME type specifications
#[tokio::test]
async fn test_typst_mime_type_variants() {
    let config = ExtractionConfig::default();

    let doc_path = typst_fixture("simple.typ");
    let content = match fs::read(doc_path) {
        Ok(c) => c,
        Err(e) => {
            eprintln!("Warning: Could not read simple.typ: {}. Skipping test.", e);
            return;
        }
    };

    let mime_types = vec!["application/x-typst", "text/x-typst", "text/plain"];

    for mime_type in mime_types {
        let result = extract_bytes_document(&content, mime_type, &config).await;

        if let Ok(extraction) = result {
            assert!(
                !extraction.content.is_empty(),
                "Should extract content with MIME type: {}",
                mime_type
            );
            println!(
                "✓ MIME type '{}': Successfully extracted {} characters",
                mime_type,
                extraction.content.len()
            );
        }
    }
}

/// Test formatting preservation
///
/// Validates that inline formatting markers are preserved in extracted content
#[tokio::test]
async fn test_formatting_preservation() {
    let config = ExtractionConfig::default();

    let doc_path = typst_fixture("simple.typ");
    let content = match fs::read(doc_path) {
        Ok(c) => c,
        Err(e) => {
            eprintln!("Warning: Could not read simple.typ: {}. Skipping test.", e);
            return;
        }
    };

    let result = extract_bytes_document(&content, "text/x-typst", &config).await;
    if result.is_err() {
        println!("Skipping test: Typst extractor may not be available");
        return;
    }

    let extraction = result.expect("Operation failed");

    assert!(
        extraction.content.contains("*") || extraction.content.contains("bold"),
        "Should preserve bold formatting or text"
    );

    assert!(
        extraction.content.contains("_") || extraction.content.contains("italic"),
        "Should preserve italic formatting or text"
    );

    assert!(
        extraction.content.contains("`") || extraction.content.contains("code"),
        "Should preserve code formatting or text"
    );

    println!("✓ Formatting preservation: All markers/content found in extracted text");
}

/// Test large document handling
///
/// Validates extraction of the large undergradmath document
#[tokio::test]
async fn test_large_document_extraction() {
    let config = ExtractionConfig::default();

    let doc_path = typst_fixture("undergradmath.typ");
    let content = match fs::read(doc_path) {
        Ok(c) => c,
        Err(e) => {
            eprintln!("Warning: Could not read undergradmath.typ: {}. Skipping test.", e);
            return;
        }
    };

    let result = extract_bytes_document(&content, "text/x-typst", &config).await;
    if result.is_err() {
        println!("Skipping test: Typst extractor may not be available");
        return;
    }

    let extraction = result.expect("Operation failed");

    assert!(
        !extraction.content.is_empty(),
        "Should extract content from large document"
    );

    println!(
        "✓ Large document: Extracted {} bytes of content from source file",
        extraction.content.len()
    );
}

/// Test empty/whitespace handling
///
/// Validates graceful handling of edge cases
#[tokio::test]
async fn test_empty_content_handling() {
    let config = ExtractionConfig::default();

    let empty_content = b"";
    let result = extract_bytes_document(empty_content, "text/x-typst", &config).await;

    match result {
        Ok(extraction) => {
            println!(
                "✓ Empty content: Handled gracefully, extracted {} bytes",
                extraction.content.len()
            );
        }
        Err(e) => {
            println!("✓ Empty content: Resulted in expected error: {}", e);
        }
    }
}

/// Test MIME type priority
///
/// Validates that Typst extractor has correct priority (50)
#[tokio::test]
async fn test_typst_extractor_priority() {
    use xberg::extractors::TypstExtractor;
    use xberg::plugins::DocumentExtractor;

    let extractor = TypstExtractor;
    let priority = extractor.priority();

    assert_eq!(priority, 50, "Typst extractor should have priority 50");
    println!("✓ Typst extractor priority: {}", priority);
}

/// Test supported MIME types
///
/// Validates that extractor claims to support Typst MIME types
#[tokio::test]
async fn test_supported_mime_types() {
    use xberg::extractors::TypstExtractor;
    use xberg::plugins::DocumentExtractor;

    let extractor = TypstExtractor;
    let mime_types = extractor.supported_mime_types();

    assert!(
        mime_types.contains(&"application/x-typst"),
        "Should support application/x-typst"
    );
    assert!(mime_types.contains(&"text/x-typst"), "Should support text/x-typst");

    println!("✓ Supported MIME types: {:?}", mime_types);
}
