//! A page renders blank when an image XObject's `/Height` is an indirect reference.
//!
//! ISO 32000-1:2008 §7.3.10 lets any dictionary entry be an indirect reference unless
//! the spec forbids it for that key, and neither `/Width` nor `/Height` is excluded.
//! Brother scanner firmware emits `/Height 8 0 R`. Extraction resolves this (see
//! `indirect_image_dimensions.rs`), but the reporter saw the *rendered* page come back
//! as an all-white bitmap at the right aspect ratio — decode succeeded with a blank
//! canvas, so the "OCR the raw embedded image" fallback never fired either and VLM OCR
//! reported "no content".
//!
//! This is an A/B in one test: the same image painted twice, once with a direct
//! `/Height` and once with an indirect one. Anything that resolves for one and not the
//! other shows up as a difference in painted ink, so the test cannot pass by both
//! renders being blank.

use xberg_native_pdf::PdfDocument;
use xberg_native_pdf::rendering::{RenderOptions, render_page};

const IMAGE_WIDTH: u32 = 10;
const IMAGE_HEIGHT: u32 = 8;

/// Build a one-page PDF painting a solid-black 10x8 DeviceGray image.
///
/// When `indirect_height` is set, `/Height` is written as a reference to a bare integer
/// object instead of an inline literal; everything else is byte-identical.
fn pdf_painting_an_image(indirect_height: bool, indirect_length: bool) -> Vec<u8> {
    pdf_painting_an_image_with_filter(indirect_height, indirect_length, false)
}

/// Encode a solid-black image as a baseline JPEG, matching the reporter's `/DCTDecode`.
fn black_jpeg() -> Vec<u8> {
    let gray = vec![0u8; (IMAGE_WIDTH * IMAGE_HEIGHT) as usize];
    let mut out = Vec::new();
    jpeg_encoder::Encoder::new(&mut out, 90)
        .encode(
            &gray,
            IMAGE_WIDTH as u16,
            IMAGE_HEIGHT as u16,
            jpeg_encoder::ColorType::Luma,
        )
        .expect("encode the fixture JPEG");
    out
}

fn pdf_painting_an_image_with_filter(indirect_height: bool, indirect_length: bool, dct_decode: bool) -> Vec<u8> {
    let img = if dct_decode {
        black_jpeg()
    } else {
        vec![0u8; (IMAGE_WIDTH * IMAGE_HEIGHT) as usize]
    };
    let filter_entry = if dct_decode { " /Filter /DCTDecode" } else { "" };

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
        IMAGE_HEIGHT.to_string()
    };
    let length_entry = if indirect_length {
        "7 0 R".to_string()
    } else {
        img.len().to_string()
    };
    obj(
        &mut buf,
        4,
        format!(
            "<< /Type /XObject /Subtype /Image /Width {IMAGE_WIDTH} /Height {height_entry} \
             /ColorSpace /DeviceGray /BitsPerComponent 8{filter_entry} /Length {length_entry} >>"
        ),
        Some(&img),
    );

    let content = b"q 60 0 0 60 20 20 cm /Im0 Do Q";
    obj(&mut buf, 5, format!("<< /Length {} >>", content.len()), Some(content));
    obj(&mut buf, 6, IMAGE_HEIGHT.to_string(), None);
    obj(&mut buf, 7, img.len().to_string(), None);

    let xref = buf.len();
    buf.extend_from_slice(b"xref\n0 8\n0000000000 65535 f \n");
    for offset in &off[1..=7] {
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
    let direct = painted_pixels(pdf_painting_an_image(false, false));
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
    let actual = painted_pixels(pdf_painting_an_image(true, false));

    assert_eq!(
        actual, expected,
        "an indirect /Height painted {actual} dark pixels against {expected} for a direct \
         one — the reference was not resolved and the page rendered blank"
    );
}

#[test]
fn an_indirect_length_paints_the_same_ink_as_a_direct_one() {
    let expected = control();
    let actual = painted_pixels(pdf_painting_an_image(false, true));

    assert_eq!(
        actual, expected,
        "an indirect /Length painted {actual} dark pixels against {expected} for a direct \
         one — the stream extent was not resolved, so the image data read short"
    );
}

#[test]
fn an_indirect_height_and_length_together_paint_the_same_ink() {
    let expected = control();
    let actual = painted_pixels(pdf_painting_an_image(true, true));

    assert_eq!(
        actual, expected,
        "indirect /Height and /Length together painted {actual} dark pixels against \
         {expected} for direct ones"
    );
}

/// The reporter's exact shape: a `/DCTDecode` JPEG whose `/Height` and `/Length` are both
/// indirect references, as emitted by Brother scanner firmware.
#[test]
fn a_dct_decode_image_with_indirect_height_and_length_paints_the_same_ink() {
    let expected = painted_pixels(pdf_painting_an_image_with_filter(false, false, true));
    assert!(
        expected > 1_000,
        "control failed: a /DCTDecode image with direct entries painted only {expected} \
         dark pixels, so this test could not detect a blank render"
    );

    let actual = painted_pixels(pdf_painting_an_image_with_filter(true, true, true));

    assert_eq!(
        actual, expected,
        "a /DCTDecode image with indirect /Height and /Length painted {actual} dark pixels \
         against {expected} for direct ones — the page rendered blank"
    );
}
