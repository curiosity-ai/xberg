//! A hostile `/Width`, `/Height`, or `/VerticesPerRow` must never turn a
//! render into a multi-gigabyte allocation, an overflow panic, or an
//! effectively unbounded pixel-by-pixel loop.
//!
//! Two independent document-controlled sizes feed unchecked allocations
//! before the fixes this file guards:
//!
//! - A JBIG2 `/ImageMask`/image XObject decodes its codestream using its own
//!   internal header, so a tiny valid stream can still declare an
//!   astronomical `/Width` x `/Height` in the PDF dictionary. That pair used
//!   to size a `Vec::with_capacity` (and, for the mask path, a packed-row
//!   buffer and its nested per-pixel loop) directly from the dictionary.
//! - A Type 5 lattice mesh shading's `/VerticesPerRow` had no upper bound,
//!   unlike every other bit-width field `MeshParams::parse` validates, and
//!   fed a `Vec::with_capacity` that is not itself fallible.
//!
//! Every test here renders a full page through the public API and asserts
//! the render completes and returns `Ok`, never that it hangs or aborts the
//! process — a wedged or crashed test process is not something `#[test]`
//! can observe as a failure. The size guards live in
//! `src/extractors/images.rs` (`checked_jbig2_pixel_capacity`,
//! `pack_image_mask_rows`) and `src/rendering/mesh_shading.rs`
//! (`MeshParams::parse`); this file is the end-to-end proof that a hostile
//! document actually reaches them.

use xberg_native_pdf::PdfDocument;
use xberg_native_pdf::rendering::{RenderOptions, render_page};

/// Assemble a PDF with a correct xref from raw object bodies.
/// `objects[i]` is the body of object i+1 (no "N 0 obj"/"endobj" wrapper).
fn build_pdf(objects: &[Vec<u8>]) -> Vec<u8> {
    let mut out: Vec<u8> = Vec::new();
    out.extend_from_slice(b"%PDF-1.7\n");
    let mut offsets = Vec::new();
    for (i, body) in objects.iter().enumerate() {
        offsets.push(out.len());
        out.extend_from_slice(format!("{} 0 obj\n", i + 1).as_bytes());
        out.extend_from_slice(body);
        out.extend_from_slice(b"\nendobj\n");
    }
    let xref_pos = out.len();
    out.extend_from_slice(format!("xref\n0 {}\n", objects.len() + 1).as_bytes());
    out.extend_from_slice(b"0000000000 65535 f \n");
    for off in &offsets {
        out.extend_from_slice(format!("{off:010} 00000 n \n").as_bytes());
    }
    out.extend_from_slice(
        format!(
            "trailer\n<< /Size {} /Root 1 0 R >>\nstartxref\n{}\n%%EOF\n",
            objects.len() + 1,
            xref_pos
        )
        .as_bytes(),
    );
    out
}

fn obj(s: &str) -> Vec<u8> {
    s.as_bytes().to_vec()
}

fn stream_obj(dict: &str, data: &[u8]) -> Vec<u8> {
    let mut v = format!("<< {} /Length {} >>\nstream\n", dict, data.len()).into_bytes();
    v.extend_from_slice(data);
    v.extend_from_slice(b"\nendstream");
    v
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

/// A JBIG2 `/ImageMask` declaring `/Width 100000 /Height 100000` (the exact
/// shape that asks for a ~1.25 GB allocation) with a one-byte (garbage)
/// stream. This is deliberately below `PageRenderer::image_mask_layout`'s own
/// pre-existing `checked_mul` guard (which only rejects a `usize` overflow,
/// not a merely huge-but-representable product) so this test exercises the
/// guard THIS fix adds, not that one. Before the fix, `decode_jbig2_image`
/// sized a pixel buffer from `width * height` in `u32` arithmetic — an
/// overflow panic in debug, a silent zero-capacity wrap in release — and
/// `pack_image_mask_rows` separately tried to allocate `row_bytes * height`
/// packed bytes with no cap. The guard must reject this before either
/// allocation, from the dictionary values alone, so garbage stream bytes
/// never even reach the JBIG2 decoder.
#[test]
fn oversized_jbig2_image_mask_renders_without_hang_or_panic() {
    const HUGE: u32 = 100_000;
    let objects = vec![
        obj("<< /Type /Catalog /Pages 2 0 R >>"),
        obj("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
        obj("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] \
             /Resources << /XObject << /Im0 4 0 R >> >> /Contents 5 0 R >>"),
        stream_obj(
            &format!(
                "/Type /XObject /Subtype /Image /Width {HUGE} /Height {HUGE} \
                 /ImageMask true /BitsPerComponent 1 /Filter /JBIG2Decode"
            ),
            &[0u8],
        ),
        stream_obj("", b"q 60 0 0 60 20 20 cm /Im0 Do Q"),
    ];
    let doc = PdfDocument::from_bytes(build_pdf(&objects)).expect("synthetic PDF parses");

    let rendered = render_page(&doc, 0, &RenderOptions::with_dpi(72).as_raw())
        .expect("a page with an oversized JBIG2 ImageMask must still render, with the mask skipped");

    assert!(!rendered.data.is_empty(), "renderer produced an empty buffer");
}

/// The same oversized declaration on a plain (non-mask) JBIG2 image XObject,
/// which goes through `decode_jbig2_image` directly rather than via
/// `decode_jbig2_image_mask_bits`. `extract_image_from_xobject` has no
/// pre-existing dimension guard on this path at all, so `width * height`
/// reaches `decode_jbig2_image` exactly as read from the dictionary.
#[test]
fn oversized_jbig2_image_renders_without_hang_or_panic() {
    const HUGE: u32 = 100_000;
    let objects = vec![
        obj("<< /Type /Catalog /Pages 2 0 R >>"),
        obj("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
        obj("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] \
             /Resources << /XObject << /Im0 4 0 R >> >> /Contents 5 0 R >>"),
        stream_obj(
            &format!(
                "/Type /XObject /Subtype /Image /Width {HUGE} /Height {HUGE} \
                 /ColorSpace /DeviceGray /BitsPerComponent 1 /Filter /JBIG2Decode"
            ),
            &[0u8],
        ),
        stream_obj("", b"q 60 0 0 60 20 20 cm /Im0 Do Q"),
    ];
    let doc = PdfDocument::from_bytes(build_pdf(&objects)).expect("synthetic PDF parses");

    let rendered = render_page(&doc, 0, &RenderOptions::with_dpi(72).as_raw())
        .expect("a page with an oversized JBIG2 image must still render, with the image skipped");

    assert!(!rendered.data.is_empty(), "renderer produced an empty buffer");
}

/// A Type 5 lattice shading declaring `/VerticesPerRow 9223372036854775807`
/// (`i64::MAX`) with a one-byte stream. Before the fix,
/// `decode_type5_stream` sized its row buffer with
/// `Vec::with_capacity(vertices_per_row)`, which is not fallible and aborts
/// the process with a "capacity overflow" panic well before any byte of the
/// stream is read.
#[test]
fn oversized_vertices_per_row_renders_without_hang_or_panic() {
    let objects = vec![
        obj("<< /Type /Catalog /Pages 2 0 R >>"),
        obj("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
        obj("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 40 40] /Contents 4 0 R \
             /Resources << /Shading << /Sh0 5 0 R >> >> >>"),
        stream_obj("", b"q /Sh0 sh Q"),
        stream_obj(
            "/ShadingType 5 /ColorSpace /DeviceGray /BitsPerCoordinate 8 /BitsPerComponent 8 \
             /VerticesPerRow 9223372036854775807 /Decode [0 1 0 1 0 1]",
            &[0u8],
        ),
    ];
    let doc = PdfDocument::from_bytes(build_pdf(&objects)).expect("synthetic PDF parses");

    let rendered = render_page(&doc, 0, &RenderOptions::with_dpi(72).as_raw())
        .expect("a page with an oversized VerticesPerRow must still render, with the shading skipped");

    assert!(!rendered.data.is_empty(), "renderer produced an empty buffer");
}

/// Positive control: an ordinary Type 5 lattice (2 rows x 3 columns, well
/// under the `VerticesPerRow` guard cap) must still tessellate and paint
/// exactly as it did before the guard existed. A cap set too tight would
/// silently break every real Type 5 mesh, so this is the more important
/// half of the guard test pair — it must NOT skip.
#[test]
fn ordinary_vertices_per_row_still_renders_the_lattice() {
    // 8 bits per coordinate/component; 1 grayscale component, held at 0
    // (black) for every vertex so the painted region contrasts with the
    // white page background. Two rows of three vertices each tessellate
    // into (3-1) x 2 = 4 triangles covering most of the page. ~keep
    let mut mesh_bytes: Vec<u8> = Vec::new();
    for &x in &[0u8, 128, 255] {
        mesh_bytes.extend_from_slice(&[x, 0, 0]);
    }
    for &x in &[0u8, 128, 255] {
        mesh_bytes.extend_from_slice(&[x, 255, 0]);
    }

    let objects = vec![
        obj("<< /Type /Catalog /Pages 2 0 R >>"),
        obj("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
        obj("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 40 40] /Contents 4 0 R \
             /Resources << /Shading << /Sh0 5 0 R >> >> >>"),
        stream_obj("", b"q 40 0 0 40 0 0 cm /Sh0 sh Q"),
        stream_obj(
            "/ShadingType 5 /ColorSpace /DeviceGray /BitsPerCoordinate 8 /BitsPerComponent 8 \
             /VerticesPerRow 3 /Decode [0 1 0 1 0 1]",
            &mesh_bytes,
        ),
    ];
    let painted = painted_pixels(build_pdf(&objects));

    assert!(
        painted > 100,
        "an ordinary VerticesPerRow=3 lattice painted only {painted} non-background pixels — \
         the guard cap must not be rejecting legitimate meshes"
    );
}
