//! A page renders blank when an `/ImageMask` stencil's `/Height` (or `/Width`) is an
//! indirect reference.
//!
//! ISO 32000-1:2008 §7.3.10 lets any dictionary entry be an indirect reference unless
//! the spec forbids it for that key, and neither `/Width` nor `/Height` is excluded.
//! `indirect_image_dimension_rendering.rs` pinned this for the regular (non-mask) image
//! path via `extract_image_from_xobject`, but `PageRenderer::image_mask_layout` — the
//! dimension/allocation-size gate feeding `render_image_mask` — read `/Width` and
//! `/Height` with a plain `dict.get(key).as_integer()` and never resolved a reference,
//! so an indirect dimension read as "missing" and `render_image_mask` errored out
//! instead of painting. This matters because `/ImageMask` stencils are the JBIG2 / MRC
//! scanned-PDF path: a scanned page whose mask uses indirect dimensions renders blank,
//! and blank input then starves OCR.
//!
//! This is an A/B in one test: the same stencil painted twice, once with a direct
//! `/Height` (and `/Width`) and once with indirect ones. Anything that resolves for one
//! and not the other shows up as a difference in painted ink, so the test cannot pass by
//! both renders being blank.

use xberg_native_pdf::PdfDocument;
use xberg_native_pdf::rendering::{RenderOptions, render_page};

const MASK_WIDTH: u32 = 10;
const MASK_HEIGHT: u32 = 8;

/// Build a one-page PDF painting a solid `/ImageMask` stencil covering the whole image.
///
/// The stencil is all-zero sample bytes; under the default `/Decode [0 1]` a sample of
/// `0` paints the current fill colour (black, the graphics-state default), so a
/// correctly rendered stencil paints every pixel of its footprint.
///
/// When `indirect_height` / `indirect_width` are set, the corresponding entry is written
/// as a reference to a bare integer object instead of an inline literal; everything else
/// is byte-identical.
fn pdf_painting_an_image_mask(indirect_height: bool, indirect_width: bool) -> Vec<u8> {
    let row_bytes = MASK_WIDTH.div_ceil(8) as usize;
    let stencil = vec![0u8; row_bytes * MASK_HEIGHT as usize];

    let mut buf: Vec<u8> = Vec::new();
    let mut off = [0usize; 8];
    buf.extend_from_slice(b"%PDF-1.7\n");
    let mut obj = |buf: &mut Vec<u8>, id: usize, head: String, stream: Option<&[u8]>| {
        off[id] = buf.len();
        buf.extend_from_slice(format!("{id} 0 obj\n{head}").as_bytes());
        if let Some(s) = stream {
            buf.extend_from_slice(b"\nstream\n");
            buf.extend_from_slice(s);
            buf.extend_from_slice(b"\nendstream");
        }
        buf.extend_from_slice(b"\nendobj\n");
    };

    obj(&mut buf, 1, "<< /Type /Catalog /Pages 2 0 R >>".into(), None);
    obj(&mut buf, 2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>".into(), None);
    obj(
        &mut buf,
        3,
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] \
         /Resources << /XObject << /Im0 4 0 R >> >> /Contents 5 0 R >>"
            .into(),
        None,
    );

    let height_entry = if indirect_height {
        "6 0 R".to_string()
    } else {
        MASK_HEIGHT.to_string()
    };
    let width_entry = if indirect_width {
        "7 0 R".to_string()
    } else {
        MASK_WIDTH.to_string()
    };
    obj(
        &mut buf,
        4,
        format!(
            "<< /Type /XObject /Subtype /Image /Width {width_entry} /Height {height_entry} \
             /ImageMask true /BitsPerComponent 1 /Length {} >>",
            stencil.len()
        ),
        Some(&stencil),
    );

    let content = b"q 60 0 0 60 20 20 cm /Im0 Do Q";
    obj(&mut buf, 5, format!("<< /Length {} >>", content.len()), Some(content));
    obj(&mut buf, 6, MASK_HEIGHT.to_string(), None);
    obj(&mut buf, 7, MASK_WIDTH.to_string(), None);

    let xref = buf.len();
    buf.extend_from_slice(b"xref\n0 8\n0000000000 65535 f \n");
    for &offset in &off[1..=7] {
        buf.extend_from_slice(format!("{offset:010} 00000 n \n").as_bytes());
    }
    buf.extend_from_slice(b"trailer\n<< /Size 8 /Root 1 0 R >>\nstartxref\n");
    buf.extend_from_slice(format!("{xref}\n%%EOF\n").as_bytes());
    buf
}

/// Number of pixels on the rendered page that are not the white background.
fn painted_pixels(pdf: Vec<u8>) -> usize {
    let doc = PdfDocument::from_bytes(pdf).expect("fixture parses");
    let rendered = render_page(&doc, 0, &RenderOptions::with_dpi(72).as_raw()).expect("render page 0");
    rendered
        .data
        .chunks(4)
        .filter(|px| px[0] < 128 && px[1] < 128 && px[2] < 128)
        .count()
}

/// The all-direct baseline every variant is compared against.
fn control() -> usize {
    let direct = painted_pixels(pdf_painting_an_image_mask(false, false));
    assert!(
        direct > 1_000,
        "control failed: the all-direct variant painted only {direct} dark pixels, so none \
         of these tests could detect a blank-render regression"
    );
    direct
}

#[test]
fn an_indirect_height_paints_the_same_ink_as_a_direct_one() {
    let expected = control();
    let actual = painted_pixels(pdf_painting_an_image_mask(true, false));

    assert_eq!(
        actual, expected,
        "an indirect /Height on an /ImageMask painted {actual} dark pixels against {expected} \
         for a direct one — the reference was not resolved and the page rendered blank"
    );
}

#[test]
fn an_indirect_width_paints_the_same_ink_as_a_direct_one() {
    let expected = control();
    let actual = painted_pixels(pdf_painting_an_image_mask(false, true));

    assert_eq!(
        actual, expected,
        "an indirect /Width on an /ImageMask painted {actual} dark pixels against {expected} \
         for a direct one — the reference was not resolved and the page rendered blank"
    );
}

#[test]
fn indirect_height_and_width_together_paint_the_same_ink() {
    let expected = control();
    let actual = painted_pixels(pdf_painting_an_image_mask(true, true));

    assert_eq!(
        actual, expected,
        "indirect /Height and /Width together on an /ImageMask painted {actual} dark pixels \
         against {expected} for direct ones"
    );
}
