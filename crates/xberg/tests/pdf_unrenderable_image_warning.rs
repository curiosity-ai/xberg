//! A PDF whose `/XObject` image stream cannot be decoded (corrupt `/FlateDecode` data)
//! makes `xberg_native_pdf::rendering::page_renderer::render_xobject` catch the error
//! from `render_image` and continue, leaving that region of the page blank — the same
//! silent-degradation shape as issue #1364's dropped glyphs, except a whole picture is
//! missing rather than one glyph's ink.
//!
//! Before this fix that failure only ever reached a `tracing::warn!("Skipping
//! unrenderable image XObject ...")` call; nothing surfaced it as a `ProcessingWarning`,
//! so a caller had no way to learn the render was incomplete. This asserts the same
//! `xberg::pdf::render` capture-and-drain plumbing that already surfaces dropped-glyph
//! warnings (see `issue_291_dropped_glyph_warning.rs`) also surfaces an unrenderable
//! image, and — critically — with a message describing an image failure, not a
//! misleading "glyph ink is missing" label.

#![cfg(feature = "pdf")]

use xberg::pdf::render::{
    install_pdf_render_diagnostics, render_pdf_page_to_png, take_xberg_native_pdf_render_warnings,
};

/// Build a minimal one-page PDF with a single `/XObject /Image` (`/Im1`) whose
/// `/Filter /FlateDecode` stream is not valid zlib/deflate data. `xberg_native_pdf`'s
/// `FlateDecoder` exhausts every recovery strategy on exactly this byte string (see
/// `xberg-native-pdf/src/decoders/flate.rs::test_flate_decode_invalid_data`), so the
/// decode is guaranteed to fail rather than accidentally succeed via one of the
/// decoder's corruption-recovery heuristics.
fn build_pdf_with_undecodable_image() -> Vec<u8> {
    let image_stream: &[u8] = b"This is not zlib compressed data";
    let content_stream: &[u8] = b"q 100 0 0 100 50 50 cm /Im1 Do Q";

    let mut pdf: Vec<u8> = Vec::new();
    macro_rules! push_bytes {
        ($s:expr) => {
            pdf.extend_from_slice($s)
        };
    }
    macro_rules! push_str {
        ($s:expr) => {
            pdf.extend_from_slice($s.as_bytes())
        };
    }

    push_bytes!(b"%PDF-1.5\n");

    let off1 = pdf.len();
    push_bytes!(b"1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

    let off2 = pdf.len();
    push_bytes!(b"2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");

    let off3 = pdf.len();
    push_bytes!(
        b"3 0 obj\n\
         << /Type /Page /Parent 2 0 R /MediaBox [0 0 300 300]\n\
            /Resources << /XObject << /Im1 5 0 R >> >>\n\
            /Contents 4 0 R >>\n\
         endobj\n"
    );

    let off4 = pdf.len();
    push_str!(format!("4 0 obj\n<< /Length {} >>\nstream\n", content_stream.len()));
    push_bytes!(content_stream);
    push_bytes!(b"\nendstream\nendobj\n");

    let off5 = pdf.len();
    push_str!(format!(
        "5 0 obj\n\
         << /Type /XObject /Subtype /Image /Width 10 /Height 10 /ColorSpace /DeviceGray\n\
            /BitsPerComponent 8 /Filter /FlateDecode /Length {} >>\n\
         stream\n",
        image_stream.len()
    ));
    push_bytes!(image_stream);
    push_bytes!(b"\nendstream\nendobj\n");

    let xref_off = pdf.len();
    push_str!(format!(
        "xref\n0 6\n\
         0000000000 65535 f \r\n\
         {off1:010} 00000 n \r\n\
         {off2:010} 00000 n \r\n\
         {off3:010} 00000 n \r\n\
         {off4:010} 00000 n \r\n\
         {off5:010} 00000 n \r\n"
    ));
    push_str!(format!(
        "trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n{xref_off}\n%%EOF\n"
    ));

    pdf
}

/// The undecodable image must not fail the whole page (`render_xobject` catches the
/// error and continues), but it must now surface as exactly one `ProcessingWarning`
/// sourced `"pdf-render"`, naming the page and describing an image failure rather than
/// a dropped glyph.
#[test]
fn test_unrenderable_image_xobject_produces_processing_warning() {
    // Capture is opt-in — see issue_291_dropped_glyph_warning.rs for why this always
    // wins the dispatcher slot in a plain test binary.
    assert!(
        install_pdf_render_diagnostics(),
        "no other component should own the tracing dispatcher in this test binary"
    );

    // Drain any residual state from a previous render on this thread so the
    // assertion below is exact, not "at least".
    let _ = take_xberg_native_pdf_render_warnings();

    let pdf = build_pdf_with_undecodable_image();

    let png = render_pdf_page_to_png(&pdf, 0, Some(150), None)
        .expect("a page with one unrenderable image must still render (blank in that region)");
    assert!(!png.is_empty(), "renderer must still produce page bytes");

    let warnings = take_xberg_native_pdf_render_warnings();

    // Exactly one warning must be about the *image*. The fixture's corrupt stream also
    // trips the glyph-drop path, so the total count is not 1 and asserting that it is
    // would pin an unrelated detail of the fixture rather than the behaviour under test.
    let image_warnings: Vec<_> = warnings.iter().filter(|w| !w.message.contains("glyph")).collect();
    assert_eq!(
        image_warnings.len(),
        1,
        "the unrenderable image must surface as exactly one image ProcessingWarning, got: {warnings:?}"
    );

    let warning = image_warnings[0];
    assert_eq!(
        warning.source, "pdf-render",
        "image-render-failure warnings must be sourced \"pdf-render\", got: {}",
        warning.source
    );
    assert!(
        warning.message.contains("Page 1"),
        "warning must name the affected page, got: {}",
        warning.message
    );
    assert!(
        warning.message.contains("image"),
        "warning must describe an image failure, got: {}",
        warning.message
    );
    assert!(
        !warning.message.contains("glyph"),
        "an unrenderable image must not be mislabeled as a dropped glyph, got: {}",
        warning.message
    );
}
