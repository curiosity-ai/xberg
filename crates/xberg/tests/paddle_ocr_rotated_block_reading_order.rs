//! Route-level coverage for #658: PaddleOCR's rotated-block reordering
//! (`PaddleOcrBackend::reorder_blocks_for_page_rotation`, `crates/xberg/src/paddle_ocr/backend.rs`)
//! has thorough unit-level coverage there (`reorders_blocks_into_monotonic_order_on_a_rotated_page`
//! and `leaves_block_order_unchanged_when_page_is_not_rotated`, both driving the private sort
//! function directly with hand-built `DetailedTextBlock`s) but, unlike the other rotation fixes
//! (see `tests/rotated_text_repair.rs` for GH#1358), no test drives it through the public
//! `extract()` API. A regression there would be silent.
//!
//! Unlike GH#1358's repair, which discriminates on tiny synthetic PDFs with no model
//! dependency, PaddleOCR's reordering only fires after real detector inference — there is no
//! public entry point that accepts pre-built text blocks. So this test necessarily needs
//! ONNX Runtime and downloaded PaddleOCR models, exactly like every other model-driven test in
//! `paddle_ocr_integration.rs`, and is `#[ignore]`d for the same reason.
//!
//! # Why this discriminates on ORDER, not presence
//!
//! PaddleOCR's failure mode here is specifically reading order, not recognition (its detector
//! warps each quad upright before recognition, so the words themselves come out right either
//! way — see `ocr-rotation-is-the-dominant-defect` project notes). A synthetic single-page PDF
//! places three words in a single raster-space *column* (same x, ascending y) with a `/Rotate
//! 90` page entry and `force_ocr: true`. Because the text is authored with an upright matrix,
//! the OCR raster (which is always MediaBox-oriented, never display-oriented — see
//! `crate::pdf::render`) shows perfectly legible horizontal words; only their reading *order*
//! depends on the `/Rotate` hint.
//!
//! Working out `reorder_blocks_for_page_rotation`'s sort key for `page_rotation_degrees = 90`
//! (`correction_degrees = 270`, `reading_order_point` case `270 => (raster_width - y, x)`, where
//! `x`/`y` are the block's *raster pixel* coordinates — origin top-left, y increasing
//! **downward** (see `crate::pdf::render`'s own doc comment: "Map a bounding box from OCR-image
//! pixel space (origin top-left, y down) to PDF point space (origin bottom-left, y up)"), not
//! the PDF content-stream `y` used to author the fixture below, which increases **upward**):
//! sorted primarily on the second/`x` component, which all three words tie on (same raster
//! `x`), the fixed order falls back to the first component, `raster_width - y`, ascending —
//! i.e. raster `y` *descending*. PaddleOCR's own natural detection order is top-to-bottom in
//! raster space, i.e. raster `y` *ascending* — the exact reverse of the fixed order.
//!
//! Because rendering flips the y-axis (PDF y=80, near the bottom of the 450pt-tall MediaBox,
//! lands at the *largest* raster pixel y; PDF y=320, near the top, lands at the *smallest*),
//! placing "FIRST" at PDF y=80, "SECOND" at y=200, and "THIRD" at y=320 makes the fixed order
//! sort "FIRST" (largest raster y) first and "THIRD" (smallest raster y) last, while
//! PaddleOCR's natural ascending-raster-y detection order produces the exact reversal,
//! "THIRD SECOND FIRST". A test that only checked all three words were present would pass
//! either way; this one cannot — and a test that (incorrectly) placed "FIRST" at PDF y=320
//! would assert the *unfixed* order and pass on broken code while failing on the fix, which is
//! the trap this file fell into before this doc comment and the fixture below were corrected.

#![allow(clippy::print_stdout, clippy::print_stderr, clippy::dbg_macro)] // ~keep: test/bench binaries print by design; org logging policy exempts tests
#![cfg(all(feature = "pdf", paddle_ocr))]

use xberg::core::config::{ExtractInput, OcrConfig};
use xberg::{ExtractionConfig, extract};

/// Text matrix for an unrotated run: advances toward +x. The words stay upright in the raw
/// MediaBox raster PaddleOCR sees; only the `/Rotate` page entry (read separately from the
/// content stream) signals that the page is display-rotated.
const MATRIX_UPRIGHT: &str = "1 0 0 1";

/// One `Tm` + `Tj` pair: place `word` at (`x`, `y`) under the given text matrix.
fn show_text(matrix: &str, x: u32, y: u32, word: &str) -> String {
    format!("{matrix} {x} {y} Tm\n({word}) Tj\n")
}

/// A single-page PDF with a `/Rotate 90` page entry, holding three upright words stacked in
/// one raster-space column (same x, same raster pixel x, differing raster pixel y): "FIRST" at
/// PDF y=80 (nearest the *bottom* of the page in content-stream space, which rendering flips to
/// the *largest* raster pixel y — see the module doc comment), "SECOND" at y=200, "THIRD" at
/// PDF y=320 (nearest the top, which rendering flips to the *smallest* raster pixel y).
///
/// Deliberately uncompressed with a classic cross-reference table, mirroring
/// `tests/rotated_text_repair.rs::single_page_pdf`, so the bytes stay readable in a failure
/// dump. The layout is fixed: 1 catalog, 2 pages, 3 page, 4 contents, 5 font.
fn rotated_column_pdf() -> Vec<u8> {
    let mut operators = String::new();
    for (word, y) in [("FIRST", 80_u32), ("SECOND", 200), ("THIRD", 320)] {
        operators.push_str(&show_text(MATRIX_UPRIGHT, 100, y, word));
    }
    let content = format!("BT\n/F1 24 Tf\n{operators}ET\n");

    let objects: [String; 5] = [
        "<< /Type /Catalog /Pages 2 0 R >>".to_string(),
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>".to_string(),
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 450] /Rotate 90 \
         /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>"
            .to_string(),
        format!("<< /Length {} >>\nstream\n{content}\nendstream", content.len()),
        "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>".to_string(),
    ];

    let mut bytes: Vec<u8> = b"%PDF-1.4\n".to_vec();
    let mut offsets = Vec::with_capacity(objects.len());
    for (index, object) in objects.iter().enumerate() {
        offsets.push(bytes.len());
        bytes.extend_from_slice(format!("{} 0 obj\n{object}\nendobj\n", index + 1).as_bytes());
    }

    let xref_offset = bytes.len();
    let size = objects.len() + 1;
    bytes.extend_from_slice(format!("xref\n0 {size}\n0000000000 65535 f \n").as_bytes());
    for offset in &offsets {
        bytes.extend_from_slice(format!("{offset:010} 00000 n \n").as_bytes());
    }
    bytes.extend_from_slice(
        format!("trailer\n<< /Size {size} /Root 1 0 R >>\nstartxref\n{xref_offset}\n%%EOF\n").as_bytes(),
    );
    bytes
}

/// Collapse whitespace so the assertion pins word order without pinning line breaking.
fn normalize_whitespace(text: &str) -> String {
    text.split_whitespace().collect::<Vec<_>>().join(" ")
}

/// Force PaddleOCR over the synthetic rotated-column PDF and return the extracted text.
async fn extract_via_forced_paddle_ocr(bytes: Vec<u8>) -> String {
    let config = ExtractionConfig {
        use_cache: false,
        force_ocr: true,
        ocr: Some(OcrConfig {
            backend: "paddle-ocr".to_string(),
            language: vec!["en".to_string()],
            ..Default::default()
        }),
        ..Default::default()
    };
    let input = ExtractInput::from_bytes(bytes, "application/pdf", None);
    let result = extract(input, &config)
        .await
        .expect("forced PaddleOCR extraction of the rotated-column fixture must succeed");
    result
        .results
        .first()
        .expect("one input must yield exactly one result")
        .content
        .clone()
}

/// Assert `words` appear in `text` in exactly this order, each exactly once — mirrors
/// `tests/rotated_text_repair.rs::assert_words_in_order`.
fn assert_words_in_order(text: &str, words: &[&str]) {
    let mut previous_index: Option<usize> = None;
    for word in words {
        let occurrences = text.matches(word).count();
        assert_eq!(
            occurrences, 1,
            "expected {word:?} exactly once, found it {occurrences} times in {text:?}"
        );
        let index = text
            .find(word)
            .unwrap_or_else(|| panic!("{word:?} is missing entirely from the extracted text {text:?}"));
        if let Some(previous) = previous_index {
            assert!(
                previous < index,
                "{word:?} must follow the previous word in true reading order but appeared before it \
                 (byte {index} vs {previous}) in {text:?} — PaddleOCR's rotated-block reorder \
                 (`reorder_blocks_for_page_rotation`) did not recover reading order"
            );
        }
        previous_index = Some(index);
    }
}

#[tokio::test]
#[ignore = "requires ONNX Runtime and downloaded PaddleOCR models"]
async fn should_preserve_reading_order_when_paddle_blocks_are_rotated() {
    let text = normalize_whitespace(&extract_via_forced_paddle_ocr(rotated_column_pdf()).await);

    assert_words_in_order(&text, &["FIRST", "SECOND", "THIRD"]);
}
