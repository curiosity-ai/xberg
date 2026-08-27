//! Tests for the text search functionality.

mod common;

use xberg_native_pdf::PdfDocument;
use xberg_native_pdf::search::{SearchOptions, TextSearcher};

/// Helper function to create a test PDF with searchable text, shown as a
/// single line on page 0.
fn create_test_pdf_with_text(text: &str) -> Vec<u8> {
    let content = common::text_run_op(text, 72.0, 700.0, "Helvetica", 12.0);
    common::build_pdf_with_standard_fonts(content.as_bytes(), b"/Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]")
}

mod search_options {
    use super::*;

    #[test]
    fn test_search_options_default() {
        let opts = SearchOptions::default();
        assert!(!opts.case_insensitive);
        assert!(!opts.literal);
        assert!(!opts.whole_word);
        assert_eq!(opts.max_results, 0);
        assert!(opts.page_range.is_none());
    }

    #[test]
    fn test_search_options_builder() {
        let opts = SearchOptions::new()
            .with_case_insensitive(true)
            .with_literal(true)
            .with_whole_word(true)
            .with_max_results(10)
            .with_page_range(0, 5);

        assert!(opts.case_insensitive);
        assert!(opts.literal);
        assert!(opts.whole_word);
        assert_eq!(opts.max_results, 10);
        assert_eq!(opts.page_range, Some((0, 5)));
    }

    #[test]
    fn test_search_options_case_insensitive() {
        let opts = SearchOptions::case_insensitive();
        assert!(opts.case_insensitive);
        assert!(!opts.literal);
        assert!(!opts.whole_word);
    }
}

mod text_search {
    use super::*;

    #[test]
    fn test_simple_text_search() {
        let bytes = create_test_pdf_with_text("Hello World! Welcome to PDF search testing.");

        let temp_dir = tempfile::tempdir().expect("Failed to create temp dir");
        let temp_path = temp_dir.path().join("test_search_simple.pdf");
        std::fs::write(&temp_path, &bytes).expect("Failed to write temp PDF");

        let doc = PdfDocument::open(&temp_path).expect("Failed to open PDF");
        let options = SearchOptions::default();
        let results = TextSearcher::search(&doc, "Hello", &options).expect("Search failed");

        assert!(!results.is_empty(), "Should find at least one match for 'Hello'");
        assert!(results[0].text.contains("Hello"));

        let _ = std::fs::remove_file(&temp_path);
    }

    #[test]
    fn test_case_insensitive_search() {
        let bytes = create_test_pdf_with_text("Hello World! hello again. HELLO once more.");

        let temp_dir = tempfile::tempdir().expect("Failed to create temp dir");
        let temp_path = temp_dir.path().join("test_search_case.pdf");
        std::fs::write(&temp_path, &bytes).expect("Failed to write temp PDF");

        let doc = PdfDocument::open(&temp_path).expect("Failed to open PDF");

        let options = SearchOptions::default();
        let results = TextSearcher::search(&doc, "hello", &options).expect("Search failed");

        let options_insensitive = SearchOptions::case_insensitive();
        let results_insensitive = TextSearcher::search(&doc, "hello", &options_insensitive).expect("Search failed");

        assert!(
            results_insensitive.len() >= results.len(),
            "Case insensitive should find at least as many matches"
        );

        let _ = std::fs::remove_file(&temp_path);
    }

    #[test]
    fn test_regex_search() {
        let bytes = create_test_pdf_with_text("Item 1, Item 2, Item 3, and some text.");

        let temp_dir = tempfile::tempdir().expect("Failed to create temp dir");
        let temp_path = temp_dir.path().join("test_search_regex.pdf");
        std::fs::write(&temp_path, &bytes).expect("Failed to write temp PDF");

        let doc = PdfDocument::open(&temp_path).expect("Failed to open PDF");

        let options = SearchOptions::default();
        let results = TextSearcher::search(&doc, r"Item \d", &options).expect("Search failed");

        assert!(!results.is_empty(), "Should find at least one 'Item N' pattern");

        let _ = std::fs::remove_file(&temp_path);
    }

    #[test]
    fn test_literal_search() {
        let bytes = create_test_pdf_with_text("The regex a.b matches axb but literal a.b only matches a.b");

        let temp_dir = tempfile::tempdir().expect("Failed to create temp dir");
        let temp_path = temp_dir.path().join("test_search_literal.pdf");
        std::fs::write(&temp_path, &bytes).expect("Failed to write temp PDF");

        let doc = PdfDocument::open(&temp_path).expect("Failed to open PDF");

        let options = SearchOptions::new().with_literal(true);
        let results = TextSearcher::search(&doc, "a.b", &options).expect("Search failed");

        for result in &results {
            assert!(result.text.contains("a.b"), "Literal match should contain 'a.b'");
        }

        let _ = std::fs::remove_file(&temp_path);
    }

    #[test]
    fn test_whole_word_search() {
        let bytes = create_test_pdf_with_text("The cat sat on the mat. A category is not a cat.");

        let temp_dir = tempfile::tempdir().expect("Failed to create temp dir");
        let temp_path = temp_dir.path().join("test_search_whole_word.pdf");
        std::fs::write(&temp_path, &bytes).expect("Failed to write temp PDF");

        let doc = PdfDocument::open(&temp_path).expect("Failed to open PDF");

        let options = SearchOptions::new().with_whole_word(true);
        let results = TextSearcher::search(&doc, "cat", &options).expect("Search failed");

        for result in &results {
            assert!(
                !result.text.contains("category"),
                "Whole word search should not match 'category'"
            );
        }

        let _ = std::fs::remove_file(&temp_path);
    }

    #[test]
    fn test_max_results_limit() {
        let bytes = create_test_pdf_with_text("test test test test test test test test test test");

        let temp_dir = tempfile::tempdir().expect("Failed to create temp dir");
        let temp_path = temp_dir.path().join("test_search_max.pdf");
        std::fs::write(&temp_path, &bytes).expect("Failed to write temp PDF");

        let doc = PdfDocument::open(&temp_path).expect("Failed to open PDF");

        let options = SearchOptions::new().with_max_results(3);
        let results = TextSearcher::search(&doc, "test", &options).expect("Search failed");

        assert!(results.len() <= 3, "Should respect max_results limit of 3");

        let _ = std::fs::remove_file(&temp_path);
    }

    #[test]
    fn test_no_matches() {
        let bytes = create_test_pdf_with_text("Hello World!");

        let temp_dir = tempfile::tempdir().expect("Failed to create temp dir");
        let temp_path = temp_dir.path().join("test_search_no_match.pdf");
        std::fs::write(&temp_path, &bytes).expect("Failed to write temp PDF");

        let doc = PdfDocument::open(&temp_path).expect("Failed to open PDF");

        let options = SearchOptions::default();
        let results = TextSearcher::search(&doc, "xyz123notfound", &options).expect("Search failed");

        assert!(results.is_empty(), "Should return empty results for non-existent text");

        let _ = std::fs::remove_file(&temp_path);
    }

    #[test]
    fn test_search_result_has_position_info() {
        let bytes = create_test_pdf_with_text("Find me in this document.");

        let temp_dir = tempfile::tempdir().expect("Failed to create temp dir");
        let temp_path = temp_dir.path().join("test_search_position.pdf");
        std::fs::write(&temp_path, &bytes).expect("Failed to write temp PDF");

        let doc = PdfDocument::open(&temp_path).expect("Failed to open PDF");

        let options = SearchOptions::default();
        let results = TextSearcher::search(&doc, "Find", &options).expect("Search failed");

        if !results.is_empty() {
            let result = &results[0];
            assert_eq!(result.page, 0, "Match should be on page 0");
            assert!(result.bbox.width > 0.0, "Bounding box should have positive width");
            assert!(result.bbox.height > 0.0, "Bounding box should have positive height");
        }

        let _ = std::fs::remove_file(&temp_path);
    }
}

// `api_integration` and `highlight_integration` used to exercise the
// `xberg_native_pdf::api::Pdf` convenience wrapper's `search`/
// `search_with_options`/`search_page`/`highlight_matches`/`save` methods.
// The search-only wrappers were thin pass-throughs over
// `TextSearcher::search`, already covered directly by `mod text_search`
// above. `highlight_matches` + `save` required minting a new annotated PDF
// -- a write-path capability that went away with the PDF writer/editor;
// there is no replacement here.
