//! Renderer robustness against malformed, document-controlled geometry.
//!
//! Every construct here is legal PDF syntax carrying out-of-range values.
//! The renderer must skip the construct and return a `Result` — never panic,
//! and never fabricate a plausible-but-wrong result.

use xberg_native_pdf::document::PdfDocument;
use xberg_native_pdf::rendering::{RenderOptions, render_page};

/// Assemble a PDF with a correct xref from raw object bodies.
/// `objects[i]` is the body of object i+1 (no "N 0 obj"/"endobj" wrapper).
fn build_pdf(objects: &[Vec<u8>]) -> Vec<u8> {
    let mut out: Vec<u8> = Vec::new();
    out.extend_from_slice(b"%PDF-1.4\n");
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

/// A Type 0 sampled function declaring a `/Size` grid far larger than its
/// sample stream. The declared grid overflows the flat-index arithmetic, so
/// the function must be rejected rather than indexed with a wrapped offset.
#[test]
fn sampled_function_size_larger_than_stream_renders() {
    let samples = vec![0u8; 64];
    let objects = vec![
        obj("<< /Type /Catalog /Pages 2 0 R >>"),
        obj("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
        obj("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 40 40] /Contents 4 0 R \
             /Resources << /Shading << /Sh0 5 0 R >> >> >>"),
        stream_obj("", b"q /Sh0 sh Q"),
        obj("<< /ShadingType 1 /ColorSpace /DeviceRGB /Domain [0 1 0 1] /Function 6 0 R >>"),
        // Two input dimensions with ~2^32 samples each: the stride reaches 2^64. ~keep
        stream_obj(
            "/FunctionType 0 /Domain [0 1 0 1] /Range [0 1 0 1 0 1] \
             /Size [4294967296 4294967296] /BitsPerSample 8",
            &samples,
        ),
    ];
    let doc = PdfDocument::from_bytes(build_pdf(&objects)).expect("synthetic PDF parses");

    let img = render_page(&doc, 0, &RenderOptions::default()).expect("page with an oversized sampled function renders");
    assert!(!img.data.is_empty(), "renderer produced an empty buffer");
}

// ---------------------------------------------------------------------------
// §11.6.5.2 + §7.10.2: a Type 0 tint transform reached through a /SMask /BC
// DeviceN backdrop interpolates across `2^N` grid corners, where `N` is the
// pair count of the function's document-declared `/Domain`. Setting every
// `/Size` entry to 1 collapses the sample grid to a single value, so a
// one-byte stream satisfies the `/Size`-product and stream-length guards at
// any `N` whatsoever — leaving the corner count as the only thing `N` drives.
// --------------------------------------------------------------------------- ~keep

/// Build a page whose /SMask /BC backdrop routes `dims` tints through a
/// Type 0 sampled tint transform declaring `dims` input dimensions.
///
/// Every `/Size` entry is 1 and `/Range` has a single output pair, so the
/// declared sample count is exactly 1 and the attached one-byte stream is
/// long enough. Nothing else in the fixture scales with `dims`, which
/// isolates the `2^dims` corner loop as the sole cost of raising it.
fn devicen_smask_type0_pdf(dims: usize) -> Vec<u8> {
    let names: String = (1..=dims).map(|i| format!("/Ink{i} ")).collect();
    let domain = "0 1 ".repeat(dims);
    let sizes = "1 ".repeat(dims);
    let backdrop = "0.5 ".repeat(dims);

    let objects = vec![
        obj("<< /Type /Catalog /Pages 2 0 R >>"),
        obj("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
        obj("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 40 40] /Contents 4 0 R \
             /Resources << /ExtGState << /GS0 5 0 R >> >> >>"),
        stream_obj("", b"q /GS0 gs 1 0 0 rg 0 0 40 40 re f Q"),
        obj(&format!(
            "<< /Type /ExtGState /SMask << /Type /Mask /S /Luminosity /G 6 0 R /BC [{backdrop}] >> >>"
        )),
        // The Form's /Group /CS is what tells the /BC pre-fill that the
        // backdrop is DeviceN and which function to run on it. ~keep
        stream_obj(
            &format!(
                "/Type /XObject /Subtype /Form /BBox [0 0 40 40] /Resources << >> \
                 /Group << /Type /Group /S /Transparency \
                 /CS [/DeviceN [{names}] /DeviceGray 7 0 R] >>"
            ),
            b"% empty form\n",
        ),
        stream_obj(
            &format!("/FunctionType 0 /Domain [{domain}] /Range [0 1] /Size [{sizes}] /BitsPerSample 8"),
            &[0u8],
        ),
    ];
    build_pdf(&objects)
}

/// 64 declared input dimensions, exercised end to end so the arity guard is
/// proven reachable from a parsed document and not merely unit-tested.
///
/// `1usize << 64` is a shift past the width of `usize`: the evaluator must
/// reject the arity before computing a corner count at all. Note this test is
/// only a gate under `overflow-checks` (on by default in the dev profile) —
/// with them off the shift is masked to `<< 0` and uncapped code completes.
/// The arity bound itself is gated unconditionally by the unit tests on
/// `evaluate_type0_multi` in `rendering/page_renderer.rs`. ~keep
#[test]
fn smask_bc_devicen_type0_with_64_input_dimensions_renders() {
    let doc = PdfDocument::from_bytes(devicen_smask_type0_pdf(64)).expect("synthetic PDF parses");

    let img = render_page(&doc, 0, &RenderOptions::default())
        .expect("page with a 64-dimension sampled tint transform renders");
    assert!(!img.data.is_empty(), "renderer produced an empty buffer");
}
