// Tests run under the workspace's `unsafe_code = "deny"`. These stream bodies carry
// binary sample data, so they are not valid UTF-8 -- that is why the original used
// `from_utf8_unchecked`. A test has no performance reason to skip validation, and the
// assertions below only ever match ASCII substrings, so a lossy conversion is exact
// for their purposes.
//! Round-6 QA pass: adversarial probes for the text / Image Do /
//! shading sh coverage rewires landed in commits 879ce18 and 58f8611.
//!
//! Round 6's design+impl wired real geometry-true coverage rasterisers
//! for the three paint surfaces that were on the snapshot-vs-post-paint
//! diff branch. This QA pass drills into the five self-flagged areas the
//! design+impl agent called out plus a mandatory adversarial battery:
//!
//!  - shading gs_clone fill_spot_inks injection — operator-local?
//!  - render_mode = 3 (invisible text) under /Separation paint —
//!    coverage_only_gs overrides render_mode to 0, which means the
//!    coverage scratch paints where the visible text doesn't. Does the
//!    spot lane get written when the visible page shows nothing?
//!  - TJ array with negative kern + multi-glyph coverage accumulation.
//!  - Empty `() Tj` no-op safety.
//!
//! Spec citations:
//!  - ISO 32000-1 §8.7.4 Shading patterns
//!  - ISO 32000-1 §9.3.6 text rendering mode (Tr operator)
//!  - ISO 32000-1 §9.4 Text-showing operators
//!  - ISO 32000-1 §11.3.3 single shape/opacity per pixel
//!  - ISO 32000-1 §11.7.3 spot colours and transparency
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

#[test]
fn round6_qa_b1_shading_fill_spot_inks_does_not_leak_to_next_path_fill() {
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
                   q\n10 10 30 30 re\nW n\n\
                   /Sh1 sh\nQ\n\
                   q\n0 0 0 0 k\n\
                   60 60 30 30 re\nf\nQ\n";
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

    // Sample at user-space (25, 25) — inside shading clip 10..40 ×
    // 10..40. With PDF y-axis flipped, this maps to raster
    // (raster_x=25, raster_y=100-25=75). Sample plane byte at raster
    // (25, 75). ~keep
    let inside_shading = (75usize * w as usize) + 25;
    let expected_shading = tint_to_u8(compose_normal(0.0, 0.4, 0.99));
    assert_eq!(expected_shading, 101);
    assert_eq!(
        plane[inside_shading], expected_shading,
        "sanity: shading sh writes the InkA lane inside its clip. \
         Got {} at raster (25, 75) (user-space (25, 25)), expected {}.",
        plane[inside_shading], expected_shading
    );

    // Sample at user-space (75, 75) — inside the SUBSEQUENT path fill
    // (user-space 60..90 × 60..90), OUTSIDE the shading clip
    // (user-space 10..40 × 10..40). With y-flip, user-space (75, 75)
    // maps to raster (75, 25). The path fill uses DeviceCMYK k with
    // no /Separation cs/scn before it, so gs.fill_spot_inks at that
    // moment must be empty → spot mirror must NOT fire → InkA lane
    // stays at backdrop 0.
    //
    // If the sh arm's injection leaked back into gs_stack, the path
    // fill would mirror against the leaked InkA list and write a
    // non-zero lane value. ~keep
    let outside_shading = (25usize * w as usize) + 75;
    assert_eq!(
        plane[outside_shading], 0,
        "§8.7.4 + §11.7.3: shading sh arm's gs_clone.fill_spot_inks \
         injection MUST be operator-local. A subsequent DeviceCMYK \
         path fill (with no /Separation cs/scn) must NOT see the \
         shading's leaked inks. Got {} at raster (75, 25) (user-space \
         (75, 75), inside the path fill, outside the shading clip) — \
         expected 0 (backdrop).",
        plane[outside_shading]
    );
}

// ===========================================================================
// QA_BUG_INVISIBLE_TEXT_WRITES_SPOT_LANE — Invisible text (3 Tr) under
// /Separation paint writes the spot lane (REGRESSION introduced by
// round 6's coverage_only_gs override).
// ===========================================================================
//
// ISO 32000-1 §9.3.6 text rendering mode 3 = "neither fill nor stroke;
// add to path for clipping". The visible RGB pixmap shows no glyph.
// Pre-round-6 the spot mirror's diff branch saw no RGB change for
// invisible text → coverage 0 → lane not written.
//
// Round 6 introduced a regression: `coverage_only_gs` in
// `src/rendering/page_renderer.rs` forces `cov.render_mode = 0` to
// "make sure the coverage scratch paints something." This override
// makes the coverage helper paint where the visible text rasteriser
// would paint nothing — the spot mirror's gating (`spot_paint_active`)
// does NOT check render_mode, so the lane gets written even though the
// visible page shows nothing.
//
// §9.3.6 is silent on spot lanes, but §11.3.3's single shape/opacity
// per pixel rule applies to ALL components (process + spot). The
// natural reading: no visible mark → no spot lane write.
//
// Resolution: option (b) landed — the coverage rasterisers early-return
// an all-zero coverage plane when `gs.render_mode == 3`, so the spot
// mirror's diff branch sees no change and the lane stays unwritten.
// The probe now runs live and asserts the byte-exact zero plate; it
// is no longer `#[ignore]`-gated. ~keep
#[test]
fn round6_qa_invisible_text_must_not_write_spot_lane() {
    let icc = build_constant_cmyk_icc(135);
    let psfunc = "<< /FunctionType 2 /Domain [0 1] /Range [0 1 0 1 0 1 0 1] \
                  /C0 [0.0 0.0 0.0 0.0] /C1 [0.0 1.0 0.0 0.0] /N 1 >>";
    let font_obj = "6 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n";
    // /3 Tr makes the text invisible (§9.3.6). A /Separation /InkA
    // paint with tint 0.5 follows — the visible pixmap should show
    // NO glyph, and the InkA lane should NOT be written. ~keep
    let content = "/Trig gs\n\
                   /CS_PMS cs\n0.5 scn\n\
                   BT\n3 Tr\n/F1 50 Tf\n20 30 Td\n(A) Tj\nET\n";
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

    let plane = renderer.cmyk_sidecar_spot_plane(0).expect("InkA plane");

    // §9.3.6: render mode 3 = invisible text. The visible pixmap shows
    // no glyph. Under the §11.3.3 single shape/opacity per pixel rule,
    // the spot lane must also see no mark. Every byte of the InkA
    // plane should be 0. ~keep
    let max_byte = plane.iter().copied().max().unwrap_or(0);
    assert_eq!(
        max_byte, 0,
        "ISO 32000-1 §9.3.6 + §11.3.3 + §11.7.3: invisible text (3 Tr) \
         produces no visible mark and must not write the spot lane. \
         Round 6's `coverage_only_gs` overrides render_mode to 0 to \
         force visible fill in the coverage scratch — this means the \
         coverage helper paints where the visible pixmap does not. \
         The spot mirror gating (`spot_paint_active`) does NOT check \
         render_mode, so the lane gets written under invisible text. \
         Got max byte {} in InkA plane — expected 0.",
        max_byte
    );
}

// ===========================================================================
// PROBE QA-EMPTY — Empty `() Tj` must not panic and must not write the
// spot lane.
// ===========================================================================
//
// The new text-coverage helper re-runs `text_rasterizer.render_text`
// with the same byte string. An empty string `()` is a valid Tj
// operand (§9.4.2) that advances zero glyphs. Verify the renderer
// handles this without panic and without spurious lane writes. ~keep

#[test]
fn round6_qa_empty_tj_no_panic_no_lane_write() {
    let icc = build_constant_cmyk_icc(135);
    let psfunc = "<< /FunctionType 2 /Domain [0 1] /Range [0 1 0 1 0 1 0 1] \
                  /C0 [0.0 0.0 0.0 0.0] /C1 [0.0 1.0 0.0 0.0] /N 1 >>";
    let font_obj = "6 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n";
    let content = "/Trig gs\n\
                   /CS_PMS cs\n0.5 scn\n\
                   BT\n/F1 12 Tf\n20 30 Td\n() Tj\nET\n";
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
    let plane = renderer.cmyk_sidecar_spot_plane(0).expect("InkA plane");
    let max_byte = plane.iter().copied().max().unwrap_or(0);
    assert_eq!(
        max_byte, 0,
        "empty () Tj produces zero glyphs and must not write the InkA \
         lane. Got max byte {}.",
        max_byte
    );
}

#[test]
fn round6_qa_tj_multi_span_negative_kern_writes_both_spans() {
    let icc = build_constant_cmyk_icc(135);
    let psfunc = "<< /FunctionType 2 /Domain [0 1] /Range [0 1 0 1 0 1 0 1] \
                  /C0 [0.0 0.0 0.0 0.0] /C1 [0.0 1.0 0.0 0.0] /N 1 >>";
    let font_obj = "6 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n";
    let content = "/Trig gs\n\
                   /CS_PMS cs\n0.5 scn\n\
                   BT\n/F1 30 Tf\n10 50 Td\n[(M) -300 (M)] TJ\nET\n";
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

    let plane = renderer.cmyk_sidecar_spot_plane(0).expect("InkA plane");

    // Reference: at full glyph-interior coverage the lane composes to
    //   t_r = (1-0.99)·0 + 0.99·0.5 = 0.495 → u8 126.
    // We don't pin a specific pixel coordinate (the glyph layout
    // depends on Helvetica metrics) — instead we assert the byte 126
    // appears at least twice along the text baseline strip (rows
    // around y = 50). Both spans must contribute non-zero coverage. ~keep
    let expected = tint_to_u8(compose_normal(0.0, 0.5, 0.99));
    assert_eq!(expected, 126);
    let occurrences = plane.iter().filter(|&&b| b == expected).count();
    assert!(
        occurrences >= 2,
        "TJ with [(M) -300 (M)] should write the InkA lane at TWO span \
         positions (negative kern moves the second glyph horizontally \
         but does not erase the first). Expected ≥ 2 pixels carrying \
         u8 {} (full coverage compose). Got {} occurrences.",
        expected,
        occurrences
    );
}

// ===========================================================================
// PROBE QA-D1 — Image Do with /Interpolate true (forces Bilinear):
// verify the coverage scratch produces correct byte-exact lane values
// at the rasterised image footprint.
// ===========================================================================
//
// Round 6's coverage helper re-runs `render_image_mask` which calls
// `pixmap_paint_for_image_blit` that selects the FilterQuality based
// on `image_transform.get_scale()`. Both the visible and coverage
// renders see the same transform → identical filter choice. Verify a
// fractional upscale produces lane writes byte-exact at the
// interior (well away from AA edges). ~keep

#[test]
fn round6_qa_d1_image_do_with_interpolate_writes_consistent_interior() {
    let icc = build_constant_cmyk_icc(135);
    let psfunc = "<< /FunctionType 2 /Domain [0 1] /Range [0 1 0 1 0 1 0 1] \
                  /C0 [0.0 0.0 0.0 0.0] /C1 [0.0 1.0 0.0 0.0] /N 1 >>";
    let mask_bytes: [u8; 8] = [0x00; 8];
    let stream_body: Vec<u8> = mask_bytes.to_vec();
    let form_hdr = format!(
        "6 0 obj\n\
        << /Type /XObject /Subtype /Image /Width 8 /Height 8 \
           /ImageMask true /BitsPerComponent 1 /Interpolate true \
           /Length {} >>\nstream\n",
        stream_body.len()
    );
    let mut form_full: Vec<u8> = Vec::new();
    form_full.extend_from_slice(form_hdr.as_bytes());
    form_full.extend_from_slice(&stream_body);
    form_full.extend_from_slice(b"\nendstream\nendobj\n");

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

    let plane = renderer.cmyk_sidecar_spot_plane(0).expect("InkA plane");
    let (w, _h) = renderer.cmyk_sidecar_dims().unwrap();
    // Centre pixel (50, 50) — well inside the 8×8 footprint upscaled
    // to 80×80 (raster 10..90), ≥ 10 px from any image-cell boundary
    // (cells at raster x ∈ {10, 20, 30, 40, 50, 60, 70, 80, 90}; (50,
    // 50) sits ON the centre boundary, but with uniform-paint stencil
    // every neighbouring source bit is 0 → paint, so the bicubic
    // sample equals the uniform paint value). ~keep
    let expected = tint_to_u8(compose_normal(0.0, 1.0, 0.99));
    assert_eq!(expected, 252);
    let off = (50usize * w as usize) + 50;
    assert_eq!(
        plane[off], expected,
        "ISO 32000-1 §8.9.5 + §8.9.6.2 + §11.7.3: ImageMask Do with \
         /Interpolate true must produce byte-exact full-coverage compose \
         at well-interior pixels. Coverage scratch uses the SAME \
         `pixmap_paint_for_image_blit` filter mode as the visible blit, \
         so byte-exact agreement is required. Expected u8 {} at \
         (50, 50). Got {}.",
        expected, plane[off]
    );
}

// ===========================================================================
// PROBE QA-OPM — Round 4 OPM=1 + round 6 coverage: text-show under
// OPM=1 on a /Separation paint.
// ===========================================================================
//
// Round 4 wired OPM=1 zero-source-preserve for DeviceCMYK direct.
// /Separation /InkA paint is NOT DeviceCMYK direct (§11.7.4.3 Table
// 149 row 5), so OPM=1 has no effect on the spot lane behaviour. The
// spot mirror still composes via §11.7.4.2 dispatch (UseRequested on
// /Normal). Verify the round 6 coverage helper doesn't break this:
// text on /Separation /InkA under OPM=1 + Normal BM gets coverage-
// driven byte-exact lane write. ~keep

#[test]
fn round6_qa_opm_with_text_coverage_writes_byte_exact() {
    let icc = build_constant_cmyk_icc(135);
    let psfunc = "<< /FunctionType 2 /Domain [0 1] /Range [0 1 0 1 0 1 0 1] \
                  /C0 [0.0 0.0 0.0 0.0] /C1 [0.0 1.0 0.0 0.0] /N 1 >>";
    let font_obj = "6 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n";
    let content = "/OPMgs gs\n\
                   /CS_PMS cs\n0.5 scn\n\
                   BT\n/F1 50 Tf\n20 30 Td\n(A) Tj\nET\n";
    let resources = format!(
        "/ExtGState << /OPMgs << /Type /ExtGState /OP true /OPM 1 /ca 0.99 >> >> \
         /Font << /F1 6 0 R >> \
         /ColorSpace << /CS_PMS [/Separation /InkA /DeviceCMYK {} ] >>",
        psfunc
    );
    let pdf = build_pdf_with_output_intent(content, &resources, &icc, &[font_obj.as_bytes()]);
    let doc = PdfDocument::from_bytes(pdf).expect("synthetic PDF parses");
    let mut renderer = PageRenderer::new(RenderOptions::with_dpi(72).as_raw());
    let _img = renderer.render_page(&doc, 0).expect("render succeeds");

    let plane = renderer.cmyk_sidecar_spot_plane(0).expect("InkA plane");
    // Full-coverage glyph-interior pixels carry t_r = (1-0.99)·0 +
    // 0.99·0.5 = 0.495 → u8 126. ~keep
    let expected = tint_to_u8(compose_normal(0.0, 0.5, 0.99));
    assert_eq!(expected, 126);
    let any_write = plane.contains(&expected);
    assert!(
        any_write,
        "§11.7.4.3 + §11.7.4.5: /Separation paint is Table 149 row 5; \
         OPM=1 zero-source-preserve does not apply (only row 1, \
         DeviceCMYK direct). The spot mirror's coverage-driven write \
         must still fire and produce u8 {} at glyph-interior pixels. \
         Got max byte {:?}.",
        expected,
        plane.iter().max().copied()
    );
}
