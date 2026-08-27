//! Regression tests for systemic UTF-8 encoding loss.
//!
//! These tests verify that non-ASCII text (accented Latin characters, CJK,
//! Arabic, etc.) is correctly encoded in every PDF string context:
//!   - Visible page text via Base-14 fonts (WinAnsiEncoding)
//!   - Document metadata (/Title, /Author, /Subject)
//!   - Annotation /Contents and /T fields
//!   - Bookmark /Title entries
//!   - Form field names and values
//!
//! The core invariant: a character like é (U+00E9) must appear in the PDF
//! file as the single byte 0xE9, not as the two-byte UTF-8 sequence 0xC3 0xA9.

use xberg_native_pdf::object::encode_pdf_text_string;

#[test]
fn encode_ascii_is_identity() {
    assert_eq!(encode_pdf_text_string("Hello"), b"Hello");
}

#[test]
fn encode_latin1_extended_char_preserves_bytes() {
    let bytes = encode_pdf_text_string("Lógico");
    assert_eq!(bytes, &[0x4C, 0xF3, 0x67, 0x69, 0x63, 0x6F]);
}

#[test]
fn encode_portuguese_sentence() {
    let bytes = encode_pdf_text_string("Ação é lógica");
    for (i, ch) in "Ação é lógica".chars().enumerate() {
        assert_eq!(
            bytes[i], ch as u8,
            "byte {} should be 0x{:02X} for '{}'",
            i, ch as u8, ch
        );
    }
}

#[test]
fn encode_german_umlauts() {
    let bytes = encode_pdf_text_string("äöüÄÖÜß");
    assert_eq!(bytes, &[0xE4, 0xF6, 0xFC, 0xC4, 0xD6, 0xDC, 0xDF]);
}

#[test]
fn encode_french_accents() {
    let bytes = encode_pdf_text_string("èéêëàâç");
    assert_eq!(bytes, &[0xE8, 0xE9, 0xEA, 0xEB, 0xE0, 0xE2, 0xE7]);
}

#[test]
fn encode_spanish_accents() {
    let bytes = encode_pdf_text_string("áéíóúñ¡¿");
    assert_eq!(bytes, &[0xE1, 0xE9, 0xED, 0xF3, 0xFA, 0xF1, 0xA1, 0xBF]);
}

#[test]
fn encode_cjk_triggers_utf16be_with_bom() {
    let bytes = encode_pdf_text_string("中");
    assert_eq!(&bytes[..2], &[0xFE, 0xFF], "BOM must be present");
    assert_eq!(bytes, &[0xFE, 0xFF, 0x4E, 0x2D]);
}

#[test]
fn encode_arabic_triggers_utf16be_with_bom() {
    let bytes = encode_pdf_text_string("م");
    assert_eq!(&bytes[..2], &[0xFE, 0xFF]);
    assert_eq!(bytes, &[0xFE, 0xFF, 0x06, 0x45]);
}

#[test]
fn encode_mixed_latin_and_cjk_is_all_utf16be() {
    let bytes = encode_pdf_text_string("héllo中");
    assert_eq!(&bytes[..2], &[0xFE, 0xFF], "BOM required for mixed strings");
    let expected: Vec<u8> = [0xFE_u8, 0xFF]
        .iter()
        .chain(
            "héllo中"
                .encode_utf16()
                .flat_map(|u| [(u >> 8) as u8, (u & 0xFF) as u8])
                .collect::<Vec<_>>()
                .iter(),
        )
        .copied()
        .collect();
    assert_eq!(bytes, expected);
}

#[test]
fn encode_empty_string() {
    assert_eq!(encode_pdf_text_string(""), b"");
}

#[test]
fn encode_null_byte_boundary() {
    let bytes = encode_pdf_text_string("\u{0000}");
    assert_eq!(bytes, &[0x00]);
}

// `metadata_title_with_accents_uses_pdfdocencoding_not_utf8`,
// `content_stream_latin1_text_uses_single_byte_not_utf8`,
// `metadata_with_cjk_title_uses_utf16be_bom`, and
// `content_stream_chars_above_ff_replaced_with_question_mark` used to build a
// PDF via `writer::DocumentBuilder`/`DocumentMetadata` and grep the output
// bytes to verify the WRITER correctly applied `encode_pdf_text_string`
// (metadata) and `write_escaped_string` (content-stream `Tj` strings) when
// emitting non-ASCII text. Both were writer-internal encoding paths; with
// the writer removed there is nothing left to build or grep. The primitive
// itself (`encode_pdf_text_string`, above) is unaffected and still fully
// covered.
