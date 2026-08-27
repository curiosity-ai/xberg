//! A `/Mask` stencil sub-image on a regular (non-`/ImageMask`) image XObject is
//! silently dropped — no error, no warning — when its own `/Width` or `/Height`
//! is an indirect reference.
//!
//! ISO 32000-1:2008 §7.3.10 lets any dictionary entry be an indirect reference
//! unless the spec forbids it for that key, and neither `/Width` nor `/Height`
//! is on that exclusion list. `PageRenderer::render_image` first tries to decode
//! a `/Mask` stream through `extract_image_from_xobject`, which requires
//! `/ColorSpace` — but a stencil mask legitimately has none (it carries
//! `/ImageMask true` instead, per §8.9.6.2), so that call always fails for a
//! real stencil `/Mask` and falls into a dedicated `Err(_)` fallback arm that
//! hand-decodes the 1-bit mask. That fallback arm read `/Width` and `/Height`
//! with a bare `dict.get(key).as_integer()` and never resolved a reference, so
//! an indirect dimension read as `0`, the `mw > 0 && mh > 0` guard failed, and
//! the whole masking block was skipped — leaving the base image at full
//! opacity instead of applying the stencil. This is quieter than issue #1444
//! (which at least produced a decode error): here nothing indicates the mask
//! was ignored, and the base image over-paints where it should have been
//! partially transparent.
//!
//! This is an A/B in one test: the same base image + stencil `/Mask` pair
//! rendered twice, once with a direct mask `/Height` (and `/Width`) and once
//! with an indirect one. The stencil is deliberately NOT uniform — its top half
//! paints and its bottom half masks out — so a working mask produces strictly
//! less ink than an ignored one, and the test cannot pass by both renders
//! (mask applied vs. mask skipped) producing the same pixel count by accident.

use xberg_native_pdf::PdfDocument;
use xberg_native_pdf::rendering::{RenderOptions, render_page};

const IMAGE_WIDTH: u32 = 10;
const IMAGE_HEIGHT: u32 = 8;
const MASK_WIDTH: u32 = 10;
const MASK_HEIGHT: u32 = 8;

/// Build a one-page PDF painting a solid-black 10x8 DeviceGray image with a
/// stencil `/Mask` that opaques its top half and masks out its bottom half.
///
/// When `indirect_height` / `indirect_width` are set, the corresponding entry
/// on the *mask* stream is written as a reference to a bare integer object
/// instead of an inline literal; everything else is byte-identical. The base
/// image's own `/Width` and `/Height` are always direct — that path is
/// covered by `indirect_image_dimension_rendering.rs`; this fixture isolates
/// the mask sub-image's dimensions.
fn pdf_painting_a_masked_image(indirect_height: bool, indirect_width: bool) -> Vec<u8> {
    let base_image = vec![0u8; (IMAGE_WIDTH * IMAGE_HEIGHT) as usize];

    // Stencil bits: rows 0..half are all-1 ("paint", opaque per the §8.9.6.2
    // convention `render_image`'s fallback arm documents), rows half..MASK_HEIGHT
    // are all-0 ("don't paint", transparent). Only the low MASK_WIDTH bits of
    // each row matter; padding bits are unused.
    let row_bytes = MASK_WIDTH.div_ceil(8) as usize;
    let half = (MASK_HEIGHT / 2) as usize;
    let mut stencil = vec![0u8; row_bytes * MASK_HEIGHT as usize];
    for byte in stencil.iter_mut().take(row_bytes * half) {
        *byte = 0xFF;
    }

    let mut buf: Vec<u8> = Vec::new();
    let mut off = [0usize; 9];
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

    // Base image XObject: a real, decodable /ColorSpace image (not itself an
    // /ImageMask) carrying a /Mask reference to the stencil stream (obj 6).
    obj(
        &mut buf,
        4,
        format!(
            "<< /Type /XObject /Subtype /Image /Width {IMAGE_WIDTH} /Height {IMAGE_HEIGHT} \
             /ColorSpace /DeviceGray /BitsPerComponent 8 /Mask 6 0 R /Length {} >>",
            base_image.len()
        ),
        Some(&base_image),
    );

    let content = b"q 60 0 0 60 20 20 cm /Im0 Do Q";
    obj(&mut buf, 5, format!("<< /Length {} >>", content.len()), Some(content));

    let height_entry = if indirect_height {
        "7 0 R".to_string()
    } else {
        MASK_HEIGHT.to_string()
    };
    let width_entry = if indirect_width {
        "8 0 R".to_string()
    } else {
        MASK_WIDTH.to_string()
    };
    // Stencil mask stream: /ImageMask true, no /ColorSpace — this is what
    // forces extract_image_from_xobject to Err("Image missing /ColorSpace")
    // and routes render_image into its hand-decoding fallback arm.
    obj(
        &mut buf,
        6,
        format!(
            "<< /Type /XObject /Subtype /Image /Width {width_entry} /Height {height_entry} \
             /ImageMask true /BitsPerComponent 1 /Length {} >>",
            stencil.len()
        ),
        Some(&stencil),
    );
    obj(&mut buf, 7, MASK_HEIGHT.to_string(), None);
    obj(&mut buf, 8, MASK_WIDTH.to_string(), None);

    let xref = buf.len();
    buf.extend_from_slice(b"xref\n0 9\n0000000000 65535 f \n");
    for &offset in &off[1..=8] {
        buf.extend_from_slice(format!("{offset:010} 00000 n \n").as_bytes());
    }
    buf.extend_from_slice(b"trailer\n<< /Size 9 /Root 1 0 R >>\nstartxref\n");
    buf.extend_from_slice(format!("{xref}\n%%EOF\n").as_bytes());
    buf
}

/// Number of pixels on the rendered page that are dark (the painted image, not
/// the white page background).
fn painted_pixels(pdf: Vec<u8>) -> usize {
    let doc = PdfDocument::from_bytes(pdf).expect("fixture parses");
    let rendered = render_page(&doc, 0, &RenderOptions::with_dpi(72).as_raw()).expect("render page 0");
    rendered
        .data
        .chunks(4)
        .filter(|px| px[0] < 128 && px[1] < 128 && px[2] < 128)
        .count()
}

/// The all-direct baseline every variant is compared against. Because the
/// stencil only opaques its top half, a correctly applied mask paints
/// noticeably less ink than the image's full footprint — enough to prove the
/// mask fired at all, not just that *something* painted.
fn control() -> usize {
    let direct = painted_pixels(pdf_painting_a_masked_image(false, false));
    assert!(
        direct > 500,
        "control failed: the all-direct variant painted only {direct} dark pixels, so none of \
         these tests could detect a blank-render or unmasked-full-opacity regression"
    );
    direct
}

#[test]
fn an_indirect_mask_height_paints_the_same_ink_as_a_direct_one() {
    let expected = control();
    let actual = painted_pixels(pdf_painting_a_masked_image(true, false));

    assert_eq!(
        actual, expected,
        "an indirect /Height on a /Mask stencil painted {actual} dark pixels against {expected} \
         for a direct one — the reference was not resolved, the mw > 0 && mh > 0 guard failed, \
         and the mask was silently skipped, leaving the base image fully opaque"
    );
}

#[test]
fn an_indirect_mask_width_paints_the_same_ink_as_a_direct_one() {
    let expected = control();
    let actual = painted_pixels(pdf_painting_a_masked_image(false, true));

    assert_eq!(
        actual, expected,
        "an indirect /Width on a /Mask stencil painted {actual} dark pixels against {expected} \
         for a direct one — the reference was not resolved, the mw > 0 && mh > 0 guard failed, \
         and the mask was silently skipped, leaving the base image fully opaque"
    );
}

#[test]
fn indirect_mask_height_and_width_together_paint_the_same_ink() {
    let expected = control();
    let actual = painted_pixels(pdf_painting_a_masked_image(true, true));

    assert_eq!(
        actual, expected,
        "indirect /Height and /Width together on a /Mask stencil painted {actual} dark pixels \
         against {expected} for direct ones"
    );
}
