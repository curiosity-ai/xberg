//! `extract_words` must not split a word drawn as a single
//! string literal into fragments (`module` → `m|odu|le`).
//!
//! Root cause: TJ-offset space spans were created with `text: " "` but
//! `char_widths: []`, and the span merge kept `char_widths` in lockstep by
//! tail-append + tail-resize. A width-less span merging FIRST shifted every
//! subsequent width one slot left, so `TextSpan::to_chars` paired accurate
//! per-glyph x-origins with the previous glyph's nominal width — opening
//! phantom inter-glyph gaps (~0.3 em) that the word-gap clusterer then split
//! on (ISO 32000-1:2008 §9.4.4 word boundaries must come from TJ offsets and
//! real geometry, not fabricated gaps).

mod common;
use common::build_minimal_pdf_raw;

use xberg_native_pdf::document::PdfDocument;

/// Words separated purely by TJ kerning offsets (no space glyphs), the
/// FrameMaker/print-era idiom: font size 1 scaled through `Tm`, `.0001 Tc`,
/// `TD`-relative rows, and an intra-word kern (`(\(EA)73.9(TC\))`) — the
/// exact row structure from the owner's-manual repro, where the pre-fix
/// merge produced the `m|odu|le` split.
fn fixture_pdf() -> Vec<u8> {
    let content: &[u8] = b"BT\n/F1 1 Tf\n9.978 0 0 9.978 149.6494 607.0337 Tm\n.0001 Tc\n0 Tw\n[(8)-6726.1(10A)-3366.4(Electronic)-332.9(automatic)-332.9(temperature)]TJ\n12.3168 -1.2 TD\n[(control)-332.9(\\(EA)73.9(TC\\))-332.9(module)-332.9(\\(vehicles)]TJ\nET";
    build_minimal_pdf_raw(content, b"/Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]")
}

#[test]
fn tj_offset_separated_words_stay_whole() {
    let doc = PdfDocument::from_bytes(fixture_pdf()).expect("parse fixture");
    let words = doc.extract_words(0).expect("extract words");
    let texts: Vec<&str> = words.iter().map(|w| w.text.as_str()).collect();

    assert_eq!(
        texts,
        vec![
            "8",
            "10A",
            "Electronic",
            "automatic",
            "temperature",
            "control",
            "(EATC)",
            "module",
            "(vehicles"
        ],
        "words drawn as single string literals must not fragment"
    );
}

/// Per-glyph decomposition must not open phantom gaps inside one literal:
/// each glyph's bbox right edge must reach (within tolerance) the next
/// glyph's left edge for glyphs of the same source literal.
#[test]
fn no_phantom_gaps_inside_a_single_literal() {
    let doc = PdfDocument::from_bytes(fixture_pdf()).expect("parse fixture");
    let spans = doc.extract_spans(0).expect("extract spans");
    let span = spans
        .iter()
        .find(|s| s.text.contains("module"))
        .expect("span containing 'module'");

    let chars = span.to_chars();
    let module_start = span.text.find("module").unwrap();
    let start_idx = span.text[..module_start].chars().count();
    for i in start_idx..start_idx + "module".len() - 1 {
        let gap = chars[i + 1].bbox.x - (chars[i].bbox.x + chars[i].bbox.width);
        assert!(
            gap < 1.0,
            "phantom gap of {:.2}pt after {:?} (index {}) inside literal 'module'",
            gap,
            chars[i].char,
            i
        );
    }
}
