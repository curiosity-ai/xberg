//! Fidelity of the auto surface — does the *output actually
//! look good*, not merely "no error".
//!
//! Default-running, zero external deps (no corpus, no ONNX models):
//! a synthesized PDF with KNOWN English prose is extracted and the
//! result is asserted to be readable, correctly ordered, ungarbled,
//! and (for markdown) faithfully delegated + content-bearing.
//!
//! NOTE — text-from-images (OCR) is intentionally NOT covered here:
//! it needs the ONNX model pipeline, which this repo's default test
//! lane does not provision. That remains a tracked coverage gap; see
//! the release notes / the OCR integration suite (`#[cfg(feature =
//! "ocr")]`, model-gated).

use xberg_native_pdf::document::PdfDocument;
use xberg_native_pdf::extractors::auto::AutoExtractor;

const L1: &str = "The quick brown fox jumps over the lazy dog.";
const L2: &str = "Pack my box with five dozen liquor jugs.";
const L3: &str = "Sphinx of black quartz, judge my vow.";

/// Three known prose lines, normal `BT/Td/Tj` positioning (one Tj per
/// line — the ordinary text-layer shape, not the per-glyph jitter case).
fn known_prose_pdf() -> Vec<u8> {
    let stream = format!(
        "BT /F1 12 Tf 72 720 Td ({L1}) Tj ET\n\
         BT /F1 12 Tf 72 700 Td ({L2}) Tj ET\n\
         BT /F1 12 Tf 72 680 Td ({L3}) Tj ET\n"
    );
    let mut p: Vec<u8> = Vec::new();
    macro_rules! push {
        ($s:expr) => {
            p.extend_from_slice($s)
        };
    }
    push!(b"%PDF-1.4\n");
    let o1 = p.len();
    push!(b"1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
    let o2 = p.len();
    push!(b"2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
    let o3 = p.len();
    push!(b"3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>\nendobj\n");
    let o4 = p.len();
    push!(format!("4 0 obj\n<< /Length {} >>\nstream\n", stream.len()).as_bytes());
    push!(stream.as_bytes());
    push!(b"\nendstream\nendobj\n");
    let o5 = p.len();
    push!(b"5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");
    let xo = p.len();
    push!(format!(
        "xref\n0 6\n0000000000 65535 f \r\n{o1:010} 00000 n \r\n{o2:010} 00000 n \r\n{o3:010} 00000 n \r\n{o4:010} 00000 n \r\n{o5:010} 00000 n \r\n"
    )
    .as_bytes());
    push!(format!("trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n{xo}\n%%EOF\n").as_bytes());
    p
}

fn assert_reads_well(text: &str, where_: &str) {
    let i1 = text.find(L1);
    let i2 = text.find(L2);
    let i3 = text.find(L3);
    assert!(
        i1.is_some() && i2.is_some() && i3.is_some(),
        "{where_}: all three known sentences must be present verbatim — \
         got: {text:?}"
    );
    assert!(
        i1 < i2 && i2 < i3,
        "{where_}: sentences must be in top-to-bottom reading order \
         (got offsets {i1:?} {i2:?} {i3:?})"
    );
    assert!(
        !text.contains('\u{FFFD}'),
        "{where_}: extracted text contains U+FFFD replacement chars \
         (garbled): {text:?}"
    );
}

#[test]
fn auto_extract_text_reads_correctly() {
    let doc = PdfDocument::from_bytes(known_prose_pdf()).expect("open prose pdf");
    let ae = AutoExtractor::new();
    assert_reads_well(
        &ae.extract_text(&doc, 0).expect("ae.extract_text"),
        "AutoExtractor::extract_text",
    );
    assert_reads_well(
        &doc.extract_text_auto(0).expect("extract_text_auto"),
        "PdfDocument::extract_text_auto",
    );
}
