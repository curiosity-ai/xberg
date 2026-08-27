// Tests run under the workspace's `unsafe_code = "deny"`. These stream bodies carry
// binary sample data, so they are not valid UTF-8 -- that is why the original used
// `from_utf8_unchecked`. A test has no performance reason to skip validation, and the
// assertions below only ever match ASCII substrings, so a lossy conversion is exact
// for their purposes.
//! Round-6 probes for real coverage rasterisation of text / Image Do /
//! Shading sh spot-lane writes.
//!
//! Rounds 1-3 built the spot-lane sidecar and per-paint mirror. Round 2
//! used real path-coverage masks for path fills / strokes / combos, but
//! left text, Image Do, ImageMask Do and Shading sh on the snapshot-vs-
//! post-paint diff branch. Round 2 / 3 pinned this as
//! `HONEST_GAP_SPOT_MIRROR_AA_EDGE_COVERAGE`,
//! `HONEST_GAP_SPOT_MIRROR_IDENTICAL_RGB_COLLISION`, and
//! `HONEST_GAP_SEPARATION_TEXT_DO_SH_COVERAGE`.
//!
//! Round 6 wires real coverage masks for those three paint surfaces, so
//! the spot mirror's coverage source is the same kind of geometry-true
//! per-pixel coverage that path fills already use. After round 6 the
//! three HONEST_GAPs close byte-exact.
//!
//! Spec citations:
//!  - ISO 32000-1 §7.3.5 Name objects (hex-escaped spot names)
//!  - ISO 32000-1 §8.7.4 Shading patterns
//!  - ISO 32000-1 §8.9.5 Image XObjects (unit-square bounds)
//!  - ISO 32000-1 §8.9.6.2 Stencil Masking (ImageMask /Decode default)
//!  - ISO 32000-1 §9.4 Text-showing operators
//!  - ISO 32000-1 §9.6 Simple fonts (glyph rasterisation)
//!  - ISO 32000-1 §11.3.3 single shape/opacity per pixel
//!  - ISO 32000-1 §11.7.3 spot colours and transparency (sidecar)
//!  - ISO 32000-1 §11.7.4.2 spot-lane Normal substitution

#![allow(dead_code)]

use xberg_native_pdf::document::PdfDocument;
use xberg_native_pdf::rendering::{PageRenderer, RenderOptions};

fn build_pdf_with_output_intent(
    content: &str,
    resources_inner: &str,
    icc_profile: &[u8],
    // Raw object bytes, not `&str`: these streams carry binary payloads (ICC profiles,
    // image-mask samples) that are not valid UTF-8. A `String` round-trip would either be
    // UB (`from_utf8_unchecked`) or corrupt them (`from_utf8_lossy` rewrites each invalid
    // byte as U+FFFD, changing the samples and desynchronising the `/Length` they declare).
    extra_objs: &[&[u8]],
) -> Vec<u8> {
    let mut buf: Vec<u8> = Vec::new();
    buf.extend_from_slice(b"%PDF-1.4\n");

    let cat_off = buf.len();
    buf.extend_from_slice(
        b"1 0 obj\n<< /Type /Catalog /Pages 2 0 R /OutputIntents [<< /Type /OutputIntent /S /GTS_PDFX /OutputCondition (Synthetic Non-Linear CMYK) /DestOutputProfile 5 0 R >>] >>\nendobj\n",
    );

    let pages_off = buf.len();
    buf.extend_from_slice(b"2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");

    let page_off = buf.len();
    let page = format!(
        "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << {} >> /Contents 4 0 R >>\nendobj\n",
        resources_inner
    );
    buf.extend_from_slice(page.as_bytes());

    let stream_off = buf.len();
    let stream_hdr = format!("4 0 obj\n<< /Length {} >>\nstream\n", content.len());
    buf.extend_from_slice(stream_hdr.as_bytes());
    buf.extend_from_slice(content.as_bytes());
    buf.extend_from_slice(b"\nendstream\nendobj\n");

    let icc_off = buf.len();
    let icc_hdr = format!("5 0 obj\n<< /N 4 /Length {} >>\nstream\n", icc_profile.len());
    buf.extend_from_slice(icc_hdr.as_bytes());
    buf.extend_from_slice(icc_profile);
    buf.extend_from_slice(b"\nendstream\nendobj\n");

    let mut extra_offs: Vec<usize> = Vec::new();
    for obj in extra_objs {
        extra_offs.push(buf.len());
        buf.extend_from_slice(obj);
    }

    let xref_off = buf.len();
    let total_objs = 5 + extra_objs.len();
    buf.extend_from_slice(format!("xref\n0 {}\n0000000000 65535 f \n", total_objs + 1).as_bytes());
    for off in [cat_off, pages_off, page_off, stream_off, icc_off] {
        buf.extend_from_slice(format!("{:010} 00000 n \n", off).as_bytes());
    }
    for off in extra_offs {
        buf.extend_from_slice(format!("{:010} 00000 n \n", off).as_bytes());
    }
    buf.extend_from_slice(
        format!(
            "trailer\n<< /Size {} /Root 1 0 R >>\nstartxref\n{}\n%%EOF\n",
            total_objs + 1,
            xref_off
        )
        .as_bytes(),
    );
    buf
}

/// Constant-output CMYK→Lab ICC profile (any CMYK input → near-neutral
/// grey at the chosen L*).
fn build_constant_cmyk_icc(l_byte: u8) -> Vec<u8> {
    let in_chan: u8 = 4;
    let out_chan: u8 = 3;
    let grid: u8 = 2;
    let mut lut = Vec::with_capacity(2048);

    lut.extend_from_slice(&0x6d66_7431u32.to_be_bytes());
    lut.extend_from_slice(&0u32.to_be_bytes());
    lut.push(in_chan);
    lut.push(out_chan);
    lut.push(grid);
    lut.push(0);

    let identity: [i32; 9] = [0x0001_0000, 0, 0, 0, 0x0001_0000, 0, 0, 0, 0x0001_0000];
    for v in identity {
        lut.extend_from_slice(&(v as u32).to_be_bytes());
    }
    for _ in 0..in_chan {
        for i in 0..256u16 {
            lut.push(i as u8);
        }
    }
    let grid_size = (grid as usize).pow(in_chan as u32);
    for _ in 0..grid_size {
        lut.push(l_byte);
        lut.push(128);
        lut.push(128);
    }
    for _ in 0..out_chan {
        for i in 0..256u16 {
            lut.push(i as u8);
        }
    }

    let mut profile = vec![0u8; 128];
    let total_size: u32 = 128 + 4 + 12 + lut.len() as u32;
    profile[0..4].copy_from_slice(&total_size.to_be_bytes());
    profile[8..12].copy_from_slice(&0x0240_0000u32.to_be_bytes());
    profile[12..16].copy_from_slice(b"prtr");
    profile[16..20].copy_from_slice(b"CMYK");
    profile[20..24].copy_from_slice(b"Lab ");
    profile[36..40].copy_from_slice(b"acsp");
    profile[64..68].copy_from_slice(&0u32.to_be_bytes());
    profile[68..72].copy_from_slice(&0x0000_F6D6u32.to_be_bytes());
    profile[72..76].copy_from_slice(&0x0001_0000u32.to_be_bytes());
    profile[76..80].copy_from_slice(&0x0000_D32Du32.to_be_bytes());

    profile.extend_from_slice(&1u32.to_be_bytes());
    profile.extend_from_slice(&0x4132_4230u32.to_be_bytes());
    profile.extend_from_slice(&144u32.to_be_bytes());
    profile.extend_from_slice(&(lut.len() as u32).to_be_bytes());
    profile.extend_from_slice(&lut);

    profile
}

fn tint_to_u8(t: f32) -> u8 {
    (t.clamp(0.0, 1.0) * 255.0).round() as u8
}

fn compose_normal(t_b: f32, t_s: f32, alpha: f32) -> f32 {
    (1.0 - alpha) * t_b + alpha * t_s
}

// ===========================================================================
// PROBE 1 — Image Do real coverage on /Separation /InkA (uniform paint
// stencil, axis-aligned pixel grid).
// ===========================================================================
//
// An /ImageMask Do with /Decode [0 1] over a /Separation /InkA paint.
// Pre-round-6 the diff branch records coverage = 255 on every pixel
// whose RGB changed; round 6 rasterises the unit-square footprint
// directly. To pin both the footprint geometry AND the stencil-bit
// contribution byte-exact without inter-row resampling artefacts we
// use a uniform-PAINT stencil (every bit 0 per ISO 32000-1 §8.9.6.2
// default /Decode [0 1]) and sample at:
//   (a) page CENTRE (50, 50) — well inside the footprint and far from
//       any image-row boundary → exact tint at full coverage.
//   (b) page CORNER (5, 5) — outside the footprint → backdrop 0.
//   (c) just outside the footprint edge — (5, 50) → backdrop 0.
//
// Stencil layout (8x8, MSB-first per row):
//   every byte = 0x00 → bit 0 = paint at every column / every row.
//
// At each image pixel the stencil contributes paint → spot lane
// carries tint 1.0 (the scn 1.0). With CTM `80 0 0 80 10 10` the
// footprint in raster coords is x ∈ [10, 90) × y ∈ [10, 90). Outside
// that, the spot lane stays at backdrop 0. ~keep

#[test]
fn round6_p1_image_mask_do_real_coverage_writes_only_painted_pixels() {
    let icc = build_constant_cmyk_icc(135);
    let psfunc = "<< /FunctionType 2 /Domain [0 1] /Range [0 1 0 1 0 1 0 1] \
                  /C0 [0.0 0.0 0.0 0.0] /C1 [0.0 1.0 0.0 0.0] /N 1 >>";
    // 8x8 ImageMask stream, MSB-first row order. Per ISO 32000-1
    // §8.9.6.2 with default /Decode [0 1]: sample bit 0 → PAINT
    // (opaque), bit 1 → no paint. Uniform-paint stencil = every byte
    // 0x00. ~keep
    let mask_bytes: [u8; 8] = [0x00; 8];
    let stream_body: Vec<u8> = mask_bytes.to_vec();
    // Place image at user-space (10, 10) with width = height = 80. The
    // 80-unit-wide image at 72 dpi maps to 80 pixels on the page. At
    // 1 dpi/pt scale (RenderOptions::with_dpi(72)) the page pixmap is
    // 100x100 px. Image footprint: pixel columns [10..90), pixel rows
    // [10..90) (PDF y is flipped vs raster y).
    //
    // Each image pixel occupies 10×10 page pixels (80 / 8 = 10).
    //
    // ImageMask Do is /Separation /InkA paint with tint 1.0 inside the
    // Form. The Do operator transforms the image-space unit square via
    // CTM = [80 0 0 80 10 10]; the ImageMask sees raster-y (top-down)
    // for its rows. ~keep
    let form_obj = format!(
        "6 0 obj\n\
        << /Type /XObject /Subtype /Image /Width 8 /Height 8 \
           /ImageMask true /BitsPerComponent 1 \
           /Length {} >>\nstream\n",
        stream_body.len()
    );
    let mut form_full: Vec<u8> = Vec::new();
    form_full.extend_from_slice(form_obj.as_bytes());
    form_full.extend_from_slice(&stream_body);
    form_full.extend_from_slice(b"\nendstream\nendobj\n");

    // The content stream sets fill to /Separation /InkA tint 1.0,
    // positions and paints the ImageMask Do. /ca 0.99 fires the
    // transparency detection AND keeps the §11.3.3 compose at α =
    // 0.99 (so painted pixels carry t_s · α = 0.99 — a discriminating
    // value distinct from both backdrop 0 and post-source-clamp 255). ~keep
    let content = "/Trig gs\n\
                   /CS_PMS cs\n1.0 scn\n\
                   q\n80 0 0 80 10 10 cm\n/Img Do\nQ\n";
    let resources = format!(
        "/ExtGState << /Trig << /Type /ExtGState /ca 0.99 >> >> \
         /XObject << /Img 6 0 R >> \
         /ColorSpace << /CS_PMS [/Separation /InkA /DeviceCMYK {} ] >>",
        psfunc
    );
    let pdf = build_pdf_with_output_intent(content, &resources, &icc, &[&form_full]);
    let doc = PdfDocument::from_bytes(pdf).expect("synthetic PDF parses");
    let mut renderer = PageRenderer::new(RenderOptions::with_dpi(72).as_raw());
    let _img = renderer.render_page(&doc, 0).expect("render succeeds");

    let names = renderer.cmyk_sidecar_spot_names().expect("sidecar present");
    assert_eq!(names, &["InkA".to_string()]);
    let (w, h) = renderer.cmyk_sidecar_dims().expect("dims present");
    assert_eq!((w, h), (100, 100));
    let plane = renderer.cmyk_sidecar_spot_plane(0).expect("InkA plane");

    let expected_paint = tint_to_u8(compose_normal(0.0, 1.0, 0.99));
    assert_eq!(expected_paint, 252);
    let expected_outside: u8 = 0;

    let off_a = (50usize * w as usize) + 50;
    assert_eq!(
        plane[off_a], expected_paint,
        "ISO 32000-1 §8.9.5 + §8.9.6.2 + §11.7.3: uniform-paint image \
         mask interior pixel should carry α·t_s = 0.99·1.0 = u8 {}. \
         Got {}.",
        expected_paint, plane[off_a]
    );
    let off_b = (5usize * w as usize) + 5;
    assert_eq!(
        plane[off_b], expected_outside,
        "page corner outside image footprint should stay at backdrop \
         0. Got {}.",
        plane[off_b]
    );
    let off_c = (50usize * w as usize) + 5;
    assert_eq!(
        plane[off_c], expected_outside,
        "pixel at (5, 50) is outside footprint (x < 10) and must stay \
         at backdrop 0. Got {}.",
        plane[off_c]
    );
    let _ = h;
}

// ===========================================================================
// PROBE 2 — Identical-RGB text paint surfaces InkA lane (HONEST_GAP
// IDENTICAL_RGB_COLLISION close).
// ===========================================================================
//
// A /Separation /InkA paint whose alternate-CS tint transform produces
// the backdrop's RGB at the painted pixels. Pre-round-6: the diff
// branch saw no RGB change → coverage 0 → spot lane NOT written. Round
// 6 rasterises the text outline → coverage > 0 at glyph-interior
// pixels → spot lane IS written.
//
// Construction:
//  - Backdrop: /Trig gs (ca=0.99) DeviceCMYK paint at (0, 0, 0, 0) =
//    additive white (RGB 1, 1, 1).
//  - /Separation /InkA tint-transform function produces CMYK = (0, 0,
//    0, 0) regardless of input tint → alternate-CS RGB = (1, 1, 1) for
//    every painted pixel — IDENTICAL to the backdrop's RGB.
//  - Text-show "A" on the /Separation /InkA at tint 0.5 → spot mirror
//    should write tint 0.5 to the InkA lane at glyph-interior pixels.
//
// We sample the centre pixel of the page (which the "A" glyph
// drawn at user-space (50, 50) with a large font size covers if Tf
// places its body across the centre). To keep the geometry tractable,
// we use a large font size (50 pt) at position (35, 35) so the
// glyph's body straddles the page centre (50, 50).
//
// The probe asserts: spot lane at (50, 50) carries a NON-ZERO value
// matching the §11.3.3 compose at α = 0.99 with t_s = 0.5 over t_b = 0
// → t_r = 0.99·0.5 = 0.495 → u8 round(126.225) = 126.
//
// Pre-round-6 the diff would have produced 0 at this pixel (no RGB
// change, identical-RGB collision). Round-6 rasterised coverage gives
// 255 at glyph-interior pixels → write the lane → u8 126. ~keep

#[test]
fn round6_p2_text_identical_rgb_collision_writes_spot_lane() {
    let icc = build_constant_cmyk_icc(135);
    let psfunc = "<< /FunctionType 2 /Domain [0 1] /Range [0 1 0 1 0 1 0 1] \
                  /C0 [0.0 0.0 0.0 0.0] /C1 [0.0 0.0 0.0 0.0] /N 1 >>";
    let font_obj = "6 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n";
    let content = "/Trig gs\n\
                   0 0 0 0 k\n0 0 100 100 re\nf\n\
                   /CS_PMS cs\n0.5 scn\n\
                   BT\n/F1 50 Tf\n20 30 Td\n(A) Tj\nET\n";
    let resources = format!(
        "/ExtGState << /Trig << /Type /ExtGState /ca 0.99 >> >> \
         /Font << /F1 6 0 R >> \
         /ColorSpace << /CS_PMS [/Separation /InkA /DeviceCMYK {} ] >>",
        psfunc
    );
    let pdf = build_pdf_with_output_intent(content, &resources, &icc, &[font_obj.as_bytes()]);
    let doc = PdfDocument::from_bytes(pdf).expect("synthetic PDF parses");
    let mut renderer = PageRenderer::new(RenderOptions::with_dpi(72).as_raw());
    let _img = renderer.render_page(&doc, 0).expect("render succeeds");

    let names = renderer.cmyk_sidecar_spot_names().expect("sidecar present");
    assert_eq!(names, &["InkA".to_string()]);
    let plane = renderer.cmyk_sidecar_spot_plane(0).expect("InkA plane");
    let (w, h) = renderer.cmyk_sidecar_dims().unwrap();

    // After round 6, real coverage rasterisation lights up glyph-
    // interior pixels with coverage = 255. The §11.3.3 compose at
    // α = 1·0.99 = 0.99 with t_b = 0 (lane was blank before this
    // paint), t_s = 0.5:
    //   t_r = (1-0.99)·0 + 0.99·0.5 = 0.495 → u8 round = 126.
    //
    // We probe a band of pixels near the glyph body and assert at
    // least one carries u8 126 (the byte-exact full-coverage value).
    // Without the fix, every pixel stays at u8 0 because the diff
    // branch saw no RGB change. ~keep
    let expected_full_cov = tint_to_u8(compose_normal(0.0, 0.5, 0.99));
    assert_eq!(expected_full_cov, 126);

    let any_write = plane.contains(&expected_full_cov);
    assert!(
        any_write,
        "HONEST_GAP_SPOT_MIRROR_IDENTICAL_RGB_COLLISION close: a \
         /Separation paint whose alternate-CS RGB matches the backdrop \
         RGB must still write the InkA lane at glyph-interior pixels. \
         Expected u8 {} somewhere in the plane (geometry-driven \
         coverage > 0 → lane composes to α·t_s). Plane max byte: {:?}; \
         first non-zero offset: {:?} (dims {}×{}).",
        expected_full_cov,
        plane.iter().max().copied(),
        plane.iter().position(|&b| b != 0),
        w,
        h
    );

    let over_cap = plane.iter().any(|&b| b > expected_full_cov);
    assert!(
        !over_cap,
        "real-coverage rasterisation must not exceed the α·t_s = 0.495 \
         (u8 {}) ceiling at any pixel — coverage ∈ [0, 1] and α·t_s is \
         the maximum the §11.3.3 compose can produce at backdrop 0. \
         Got max byte {:?}.",
        expected_full_cov,
        plane.iter().max().copied()
    );
}

#[test]
fn round6_p3_image_mask_identical_rgb_collision_writes_spot_lane() {
    let icc = build_constant_cmyk_icc(135);
    let psfunc = "<< /FunctionType 2 /Domain [0 1] /Range [0 1 0 1 0 1 0 1] \
                  /C0 [0.0 0.0 0.0 0.0] /C1 [0.0 0.0 0.0 0.0] /N 1 >>";

    // 8x8 ImageMask, all-paint. Per §8.9.6.2 default /Decode [0 1]:
    // bit 0 = paint, bit 1 = no paint. Uniform paint → every byte
    // 0x00. ~keep
    let mask_bytes: [u8; 8] = [0x00; 8];
    let stream_body: Vec<u8> = mask_bytes.to_vec();
    let form_hdr = format!(
        "6 0 obj\n\
        << /Type /XObject /Subtype /Image /Width 8 /Height 8 \
           /ImageMask true /BitsPerComponent 1 \
           /Length {} >>\nstream\n",
        stream_body.len()
    );
    let mut form_full: Vec<u8> = Vec::new();
    form_full.extend_from_slice(form_hdr.as_bytes());
    form_full.extend_from_slice(&stream_body);
    form_full.extend_from_slice(b"\nendstream\nendobj\n");

    let content = "/Trig gs\n\
                   0 0 0 0 k\n0 0 100 100 re\nf\n\
                   /CS_PMS cs\n0.5 scn\n\
                   q\n80 0 0 80 10 10 cm\n/Img Do\nQ\n";
    let resources = format!(
        "/ExtGState << /Trig << /Type /ExtGState /ca 0.99 >> >> \
         /XObject << /Img 6 0 R >> \
         /ColorSpace << /CS_PMS [/Separation /InkA /DeviceCMYK {} ] >>",
        psfunc
    );
    let pdf = build_pdf_with_output_intent(content, &resources, &icc, &[&form_full]);
    let doc = PdfDocument::from_bytes(pdf).expect("synthetic PDF parses");
    let mut renderer = PageRenderer::new(RenderOptions::with_dpi(72).as_raw());
    let _img = renderer.render_page(&doc, 0).expect("render succeeds");

    let names = renderer.cmyk_sidecar_spot_names().expect("sidecar present");
    assert_eq!(names, &["InkA".to_string()]);
    let plane = renderer.cmyk_sidecar_spot_plane(0).expect("InkA plane");
    let (w, _h) = renderer.cmyk_sidecar_dims().unwrap();

    // Reference: image-interior pixel at raster (50, 50) lies within
    // the image footprint (raster y 10..90, raster x 10..90). The
    // round-6 image-coverage helper folds the stencil bit (1 = paint)
    // and footprint geometry to coverage 255 at every image-interior
    // pixel. §11.3.3 at α = 0.99, t_b = 0, t_s = 0.5:
    //   t_r = 0.99·0.5 = 0.495 → u8 round = 126. ~keep
    let expected = tint_to_u8(compose_normal(0.0, 0.5, 0.99));
    assert_eq!(expected, 126);
    let centre_off = (50usize * w as usize) + 50;
    assert_eq!(
        plane[centre_off], expected,
        "HONEST_GAP_SPOT_MIRROR_IDENTICAL_RGB_COLLISION close (image \
         surface): /Separation /InkA + ImageMask Do whose alternate-CS \
         RGB matches backdrop RGB must still write the InkA lane via \
         geometry+stencil coverage. Expected u8 {} at centre (50, 50). \
         Got {}.",
        expected, plane[centre_off]
    );
}

#[test]
fn round6_p4_shading_sh_real_coverage_writes_clipped_footprint() {
    let icc = build_constant_cmyk_icc(135);
    let psfunc = "<< /FunctionType 2 /Domain [0 1] /Range [0 1 0 1 0 1 0 1] \
                  /C0 [0.0 0.0 0.0 0.0] /C1 [0.0 1.0 0.0 0.0] /N 1 >>";
    let sh_func = "<< /FunctionType 2 /Domain [0 1] /Range [0 1] \
                  /C0 [0.4] /C1 [0.4] /N 1 >>";
    let shading_obj = format!(
        "6 0 obj\n\
        << /ShadingType 2 /ColorSpace /CS_PMS /Coords [0 0 100 0] /Domain [0 1] \
           /Function {} /Extend [false false] >>\nendobj\n",
        sh_func
    );
    let content = "/Trig gs\n\
                   q\n10 10 80 80 re\nW n\n\
                   /Sh1 sh\nQ\n";
    let resources = format!(
        "/ExtGState << /Trig << /Type /ExtGState /ca 0.99 >> >> \
         /Shading << /Sh1 6 0 R >> \
         /ColorSpace << /CS_PMS [/Separation /InkA /DeviceCMYK {} ] >>",
        psfunc
    );
    let pdf = build_pdf_with_output_intent(content, &resources, &icc, &[shading_obj.as_bytes()]);
    let doc = PdfDocument::from_bytes(pdf).expect("synthetic PDF parses");
    let mut renderer = PageRenderer::new(RenderOptions::with_dpi(72).as_raw());
    let _img = renderer.render_page(&doc, 0).expect("render succeeds");

    let names = renderer.cmyk_sidecar_spot_names().expect("sidecar present");
    assert_eq!(names, &["InkA".to_string()]);
    let plane = renderer.cmyk_sidecar_spot_plane(0).expect("InkA plane");
    let (w, _h) = renderer.cmyk_sidecar_dims().unwrap();

    // Inside clip: the shading paints the InkA lane. The shading
    // /Function evaluates to a constant tint 0.4 over the whole
    // gradient. At every clipped-interior pixel, coverage = 1.0 → lane
    // composes /Normal(0, 0.4) at α = 0.99 = 0.396 → u8 round = 101.
    //
    // Spec basis: §8.7.4 axial shading + §11.3.3 compose + §11.7.4.2
    // /Normal on the spot lane (spot mirror passes /Normal through for
    // /Normal BM unchanged). ~keep
    let expected_inside = tint_to_u8(compose_normal(0.0, 0.4, 0.99));
    assert_eq!(expected_inside, 101);
    let inside_off = (50usize * w as usize) + 50;
    assert_eq!(
        plane[inside_off], expected_inside,
        "ISO 32000-1 §8.7.4 + §11.7.3: shading sh on a /Separation \
         /InkA underlying writes the InkA lane at every clipped-interior \
         pixel. Expected u8 {} at (50, 50). Got {}.",
        expected_inside, plane[inside_off]
    );

    // Outside clip: shading must not write. The (5, 5) pixel is well
    // outside the [10, 90) clip rectangle and remains at backdrop 0. ~keep
    let outside_off = (5usize * w as usize) + 5;
    assert_eq!(
        plane[outside_off], 0,
        "outside-clip pixel must remain at backdrop 0 (clip excluded \
         the shading geometry there). Got {}.",
        plane[outside_off]
    );
}

#[test]
fn round6_p5_shading_identical_rgb_collision_writes_spot_lane() {
    let icc = build_constant_cmyk_icc(135);
    let psfunc = "<< /FunctionType 2 /Domain [0 1] /Range [0 1 0 1 0 1 0 1] \
                  /C0 [0.0 0.0 0.0 0.0] /C1 [0.0 0.0 0.0 0.0] /N 1 >>";
    let sh_func = "<< /FunctionType 2 /Domain [0 1] /Range [0 1] \
                  /C0 [0.4] /C1 [0.4] /N 1 >>";
    let shading_obj = format!(
        "6 0 obj\n\
        << /ShadingType 2 /ColorSpace /CS_PMS /Coords [0 0 100 0] /Domain [0 1] \
           /Function {} /Extend [false false] >>\nendobj\n",
        sh_func
    );
    let content = "/Trig gs\n\
                   0 0 0 0 k\n0 0 100 100 re\nf\n\
                   q\n10 10 80 80 re\nW n\n\
                   /Sh1 sh\nQ\n";
    let resources = format!(
        "/ExtGState << /Trig << /Type /ExtGState /ca 0.99 >> >> \
         /Shading << /Sh1 6 0 R >> \
         /ColorSpace << /CS_PMS [/Separation /InkA /DeviceCMYK {} ] >>",
        psfunc
    );
    let pdf = build_pdf_with_output_intent(content, &resources, &icc, &[shading_obj.as_bytes()]);
    let doc = PdfDocument::from_bytes(pdf).expect("synthetic PDF parses");
    let mut renderer = PageRenderer::new(RenderOptions::with_dpi(72).as_raw());
    let _img = renderer.render_page(&doc, 0).expect("render succeeds");

    let names = renderer.cmyk_sidecar_spot_names().expect("sidecar present");
    assert_eq!(names, &["InkA".to_string()]);
    let plane = renderer.cmyk_sidecar_spot_plane(0).expect("InkA plane");
    let (w, _h) = renderer.cmyk_sidecar_dims().unwrap();

    let expected = tint_to_u8(compose_normal(0.0, 0.4, 0.99));
    assert_eq!(expected, 101);
    let centre_off = (50usize * w as usize) + 50;
    assert_eq!(
        plane[centre_off], expected,
        "HONEST_GAP_SPOT_MIRROR_IDENTICAL_RGB_COLLISION close (shading \
         surface): shading on /Separation /InkA whose alternate-CS RGB \
         matches backdrop RGB must still write the InkA lane via \
         geometry coverage. Expected u8 {} at (50, 50). Got {}.",
        expected, plane[centre_off]
    );
}
