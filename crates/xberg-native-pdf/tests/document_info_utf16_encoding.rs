//! `/Info` dictionary text strings (`/Title`, `/Author`, ...) are PDF text
//! strings per ISO 32000-1:2008 §7.9.2.2 — UTF-16BE with a `FE FF` byte-order
//! mark, or PDFDocEncoding when unprefixed. They are never raw UTF-8.
//! `DocumentInfo::from_object` used `String::from_utf8_lossy` directly on
//! the string bytes, which mangles every non-ASCII UTF-16BE-encoded value
//! into replacement characters (each source character is 2 bytes, almost
//! none of which form valid UTF-8 sequences).
//!
//! The actual decode primitive is `crate::optional_content::decode_pdf_text_string`
//! -- `DocumentInfo::from_object` (removed along with the editor) was a
//! 30-line wrapper around it. `crates/xberg` reads `/Info` through its own
//! `get_info_string` (`src/pdf/native/metadata.rs`), not through
//! `DocumentInfo`, so nothing downstream regresses by testing the primitive
//! directly here.
//!
//! The value under test is the Cyrillic word "Привет" (Russian for
//! "hello"), UTF-16BE-encoded with a BOM — a value that has no valid
//! interpretation as UTF-8 at all, so any UTF-8-based decode reliably
//! corrupts it.

use xberg_native_pdf::optional_content::decode_pdf_text_string;

const TITLE: &str = "Привет";

fn utf16be_bom(s: &str) -> Vec<u8> {
    let mut bytes = vec![0xFE, 0xFF];
    for unit in s.encode_utf16() {
        bytes.extend_from_slice(&unit.to_be_bytes());
    }
    bytes
}

#[test]
fn info_title_decodes_utf16be_bom_correctly() {
    let decoded = decode_pdf_text_string(&utf16be_bom(TITLE));

    assert_eq!(
        decoded, TITLE,
        "UTF-16BE /Title with BOM must decode to the original Unicode string, got {:?}",
        decoded
    );
}

#[test]
fn ascii_title_without_bom_round_trips() {
    // Negative control: unprefixed PDFDocEncoding/ASCII bytes must not be
    // mistaken for UTF-16BE and must decode unchanged. ~keep
    let decoded = decode_pdf_text_string(b"Plain Title");
    assert_eq!(decoded, "Plain Title");
}
