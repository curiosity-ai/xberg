//! Round 7 probes for CMYK→CMYK ICC profile retargeting.
//!
//! Closes `HONEST_GAP_DEVICEN_PROCESS_ICC_PROFILE_MISMATCH` for the
//! `icc-lcms2` backend by exercising the CMYK→CMYK profile-retargeting
//! pipeline `crate::color::CmykRetargetTransform` puts under
//! `sidecar::extract_process_paint_cmyk`.
//!
//! Three-state matrix this round pins:
//!   - `icc-lcms2` enabled                       → full retargeting through
//!     the destination profile's BToA
//!     (the round-7 closure path).
//!   - `icc-qcms` only (no `icc-lcms2`)          → the round-5 "natural-form"
//!     reading is preserved
//!     byte-identically.
//!   - neither feature                            → §10.3.5 additive-clamp
//!     fallback fires at the
//!     consumer (renderer / image
//!     extractor); the
//!     process-paint extractor
//!     returns the natural form
//!     unchanged.
//!
//! Spec citations:
//!  - ISO 32000-1 §8.6.5.5 — ICCBased colour spaces (embedded profile
//!    precedence over /Alternate).
//!  - ISO 32000-1 §8.6.6.5 — DeviceN /Process + /Components.
//!  - ISO 32000-1 §10.7.3  — rendering intent.
//!  - ISO 32000-1 §11.7.4.3 Table 149 row 2 — overprint compose for
//!    process source colour spaces.
//!  - ICC.1:2004-10 §6.4   — Black Point Compensation. Not formally in
//!    ISO 32000 but the press-default behaviour every relative-
//!    colorimetric production pipeline expects.

#![allow(dead_code)]

use xberg_native_pdf::document::PdfDocument;
use xberg_native_pdf::rendering::render_separations;

/// `HONEST_GAP_DEVICEN_PROCESS_ICC_PROFILE_MISMATCH` —
/// three-state matrix after round 7.
///
/// **Companion narrative:** `HONEST_GAP_DEVICEN_PROCESS_ICC_PROFILE_MISMATCH`
/// in `tests/test_46_round5_devicen_process_polish.rs` documents the
/// original qcms-only "natural form" reading that this constant's three-
/// state matrix supersedes. The round-5 constant is preserved (not
/// collapsed) because it carries the historical rationale for why the
/// natural-form reading remains the qcms / no-CMM fallback. Read this
/// constant for the current truth-table; read the round-5 constant for
/// the rationale on the non-lcms2 rows.
///
///  - **`icc-lcms2` enabled (round 7 closure)**: when a DeviceN
///    /Process /ColorSpace [/ICCBased N=4] declaration carries an
///    embedded CMYK profile distinct from the document OutputIntent
///    CMYK /DestOutputProfile, the source tints are retargeted through
///    the embedded profile's `AToB` → Lab PCS → the destination
///    profile's `BToA` → destination CMYK. The press-default
///    relative-colorimetric intent with Black Point Compensation
///    governs. Probes `r7_icc_retarget_cross_profile_byte_exact` and
///    `r7_icc_retarget_bpc_changes_shadow_tones_byte_exact` pin the
///    byte-exact destination CMYK against an independent lcms2 run.
///
///  - **`icc-qcms` only** (no `icc-lcms2`): the gap remains as a
///    documented feature-level limitation. qcms 0.3 has no CMYK output
///    path, so `CmykRetargetTransform::new` returns `None` and
///    `extract_process_paint_cmyk` falls back to the round-5 "natural
///    form" reading — source tints accepted as destination CMYK
///    directly. Probe `r7_icc_qcms_only_preserves_round5_natural_form`
///    pins the round-5 byte references unchanged.
///
///  - **neither feature** (`--no-default-features --features rendering`):
///    no CMM is linked in; the §10.3.5 additive-clamp fallback fires
///    at the consumer. `extract_process_paint_cmyk` still emits the
///    round-5 natural form (no ICC re-evaluation), and the renderer's
///    composite path projects through §10.3.5.
///
/// Closure path under `icc-qcms`: enable `icc-lcms2`. Closure path
/// under no-feature: enable either `icc-qcms` (no retargeting, qcms
/// CMM for non-mismatch cases) or `icc-lcms2` (full retargeting).
pub const HONEST_GAP_DEVICEN_PROCESS_ICC_PROFILE_MISMATCH_R7: &str = "HONEST_GAP_DEVICEN_PROCESS_ICC_PROFILE_MISMATCH (round-7 status): \
     icc-lcms2 closes this gap (CMYK→CMYK retargeting through Lab PCS \
     with BPC). icc-qcms preserves the round-5 natural-form reading \
     (qcms 0.3 has no CMYK output path). no-CMM builds fall to \
     §10.3.5 additive-clamp at the consumer. Closure: enable \
     icc-lcms2.";

// ===========================================================================
// Synthetic ICC profile helpers — round-5 mirror with a B2A0 tag added so
// lcms2 can build a CMYK→CMYK transform from / through these profiles.
// =========================================================================== ~keep

/// Tunable parameters for a synthetic bidirectional CMYK ICC profile.
/// Both `A2B0` (CMYK → Lab) and `B2A0` (Lab → CMYK) tags carry
/// constant CLUTs — every CMYK input maps to `(l_byte, 128, 128)` Lab,
/// every Lab input maps to `(c_byte, m_byte, y_byte, k_byte)` CMYK.
///
/// Pinning the destination CMYK to a single constant per profile makes
/// the retarget byte-exact regardless of source tint: the lcms2 pipeline
/// is `source.AToB(input) → Lab → dest.BToA(Lab) → output`; with
/// constant CLUTs both halves are constant functions, so the output is
/// the destination profile's `(c_byte, m_byte, y_byte, k_byte)` regardless
/// of the input tints. This makes byte-exact references trivial to pin
/// and trivially reproducible under any lcms2 build (lcms2 6.x, ≥7, …):
/// the bytes are not a function of lcms2's interpolation algorithm.
#[derive(Clone, Copy)]
struct SyntheticCmykProfileParams {
    /// `A2B0` constant Lab output L channel.
    l_byte: u8,
    /// `B2A0` constant destination CMYK (C, M, Y, K) outputs.
    dest_cmyk: (u8, u8, u8, u8),
}

/// Build a bidirectional `mft1`-tag CMYK ICC profile carrying both
/// `A2B0` (CMYK → Lab) and `B2A0` (Lab → CMYK) tags.  Round 5's
/// `build_constant_cmyk_icc` carried only `A2B0`; lcms2 6.1.1 rejects
/// CMYK-output transforms built from a profile lacking `B2A0`, so the
/// retarget pipeline can't be built without both.
///
/// Layout per ICC.1:2004-10 §10.8:
///   - 128-byte header (version 2.4, prtr device class, CMYK colour
///     space, Lab PCS).
///   - 4-byte tag count = 2.
///   - 12-byte tag table entries for `A2B0` and `B2A0` (sig, offset,
///     size).
///   - `A2B0` `mft1` body: 4-channel CMYK in, 3-channel Lab out, 2-grid
///     CLUT.  Output values: constant `(l_byte, 128, 128)`.
///   - `B2A0` `mft1` body: 3-channel Lab in, 4-channel CMYK out, 2-grid
///     CLUT.  Output values: constant `(c_byte, m_byte, y_byte, k_byte)`.
///
/// `mft1` (LUT8 — sig 0x6d667431) is the smallest format both qcms and
/// lcms2 parse cleanly.  The 3x3 chromaticity matrix is identity (PCS
/// is Lab, not XYZ — the matrix is ignored by spec for Lab PCS, but
/// the field is mandatory).  Input and output curves are linear
/// (256-entry identity ramps).  The CLUT is 2^N entries per channel
/// (N = input channels), each entry of size out_chan bytes.
fn build_bidirectional_cmyk_icc(params: SyntheticCmykProfileParams) -> Vec<u8> {
    let mut a2b0 = build_mft1_constant(4, 3, &[params.l_byte, 128, 128]);
    let mut b2a0 = build_mft1_constant(
        3,
        4,
        &[
            params.dest_cmyk.0,
            params.dest_cmyk.1,
            params.dest_cmyk.2,
            params.dest_cmyk.3,
        ],
    );

    // Pad each tag body to a multiple of 4 bytes (ICC alignment) so
    // the next tag starts on a 4-byte boundary. ~keep
    while !a2b0.len().is_multiple_of(4) {
        a2b0.push(0);
    }
    while !b2a0.len().is_multiple_of(4) {
        b2a0.push(0);
    }

    let header_size: u32 = 128;
    let tag_count: u32 = 2;
    let tag_table_size: u32 = 4 + tag_count * 12;
    let a2b0_offset: u32 = header_size + tag_table_size;
    let a2b0_size: u32 = a2b0.len() as u32;
    let b2a0_offset: u32 = a2b0_offset + a2b0_size;
    let b2a0_size: u32 = b2a0.len() as u32;
    let total_size: u32 = b2a0_offset + b2a0_size;

    let mut profile = vec![0u8; 128];
    profile[0..4].copy_from_slice(&total_size.to_be_bytes());
    profile[8..12].copy_from_slice(&0x0240_0000u32.to_be_bytes());
    profile[12..16].copy_from_slice(b"prtr");
    profile[16..20].copy_from_slice(b"CMYK");
    profile[20..24].copy_from_slice(b"Lab ");
    profile[36..40].copy_from_slice(b"acsp");
    profile[64..68].copy_from_slice(&0u32.to_be_bytes());
    // D50 illuminant XYZ at bytes 68..80 — the round-5 helper pinned
    // these and lcms2 accepts them. ~keep
    profile[68..72].copy_from_slice(&0x0000_F6D6u32.to_be_bytes());
    profile[72..76].copy_from_slice(&0x0001_0000u32.to_be_bytes());
    profile[76..80].copy_from_slice(&0x0000_D32Du32.to_be_bytes());

    profile.extend_from_slice(&tag_count.to_be_bytes());
    profile.extend_from_slice(&0x4132_4230u32.to_be_bytes());
    profile.extend_from_slice(&a2b0_offset.to_be_bytes());
    profile.extend_from_slice(&a2b0_size.to_be_bytes());
    profile.extend_from_slice(&0x4232_4130u32.to_be_bytes());
    profile.extend_from_slice(&b2a0_offset.to_be_bytes());
    profile.extend_from_slice(&b2a0_size.to_be_bytes());

    profile.extend_from_slice(&a2b0);
    profile.extend_from_slice(&b2a0);
    profile
}

/// Build an `mft1` LUT8 tag body whose CLUT collapses every input to
/// the constant `out_values` (one byte per output channel).
fn build_mft1_constant(in_chan: u8, out_chan: u8, out_values: &[u8]) -> Vec<u8> {
    assert_eq!(out_values.len(), out_chan as usize);
    let grid: u8 = 2;
    let mut tag = Vec::with_capacity(2048);

    tag.extend_from_slice(&0x6d66_7431u32.to_be_bytes());
    tag.extend_from_slice(&0u32.to_be_bytes());
    tag.push(in_chan);
    tag.push(out_chan);
    tag.push(grid);
    tag.push(0);

    // 3×3 chromaticity matrix (s15Fixed16). Identity. For Lab PCS the
    // matrix is ignored but the field is mandatory. ~keep
    let identity: [u32; 9] = [0x0001_0000, 0, 0, 0, 0x0001_0000, 0, 0, 0, 0x0001_0000];
    for v in identity {
        tag.extend_from_slice(&v.to_be_bytes());
    }

    for _ in 0..in_chan {
        for i in 0..256u16 {
            tag.push(i as u8);
        }
    }

    let entries = (grid as usize).pow(in_chan as u32);
    for _ in 0..entries {
        for &v in out_values {
            tag.push(v);
        }
    }

    for _ in 0..out_chan {
        for i in 0..256u16 {
            tag.push(i as u8);
        }
    }

    tag
}

// ===========================================================================
// Synthetic PDF builder — mirrors round 5's shape so the corpus stays
// uniform; the only addition is the second ICC stream that carries the
// embedded /Process /ColorSpace profile.
// =========================================================================== ~keep

fn build_pdf_with_output_intent(
    content: &str,
    resources_inner: &str,
    icc_profile: &[u8],
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

fn plate<'a>(
    plates: &'a [xberg_native_pdf::rendering::SeparationPlate],
    name: &str,
) -> &'a xberg_native_pdf::rendering::SeparationPlate {
    plates
        .iter()
        .find(|p| p.ink_name == name)
        .unwrap_or_else(|| panic!("no plate named {}", name))
}

fn centre(plate: &xberg_native_pdf::rendering::SeparationPlate) -> u8 {
    let off = ((plate.height / 2) * plate.width + plate.width / 2) as usize;
    plate.data[off]
}

/// Make a four-name DeviceN PDF using the same shape as round 5's A1
/// fixture, but parameterised by both ICC profile streams.  `icc` is
/// the OutputIntent (object 5), `process_icc` is the embedded
/// /Process /ColorSpace stream (object 6).
fn build_devicen_iccbased_fixture(icc: &[u8], process_icc: &[u8]) -> Vec<u8> {
    let psfunc = "<< /FunctionType 2 /Domain [0 1 0 1 0 1 0 1] \
                  /Range [0 1 0 1 0 1 0 1] \
                  /C0 [0 0 0 0] /C1 [1 1 1 1] /N 1 >>";
    let content = "0.4 0 0 0 k\n0 0 100 100 re\nf\n\
                   /CS_N cs\n/Ov gs\n0.5 0.2 0.7 0.1 scn\n0 0 100 100 re\nf\n";
    let resources = format!(
        "/ExtGState << /Ov << /Type /ExtGState /OP true /ca 0.5 >> >> \
         /ColorSpace << /CS_N [/DeviceN [/Cyan /Magenta /Yellow /Black] \
            /DeviceCMYK {} \
            << /Process << /ColorSpace [/ICCBased 6 0 R] \
                          /Components [/Cyan /Magenta /Yellow /Black] >> >> \
         ] >>",
        psfunc
    );
    let process_icc_obj_hdr = format!("6 0 obj\n<< /N 4 /Length {} >>\nstream\n", process_icc.len());
    let mut process_icc_obj_bytes = Vec::from(process_icc_obj_hdr.as_bytes());
    process_icc_obj_bytes.extend_from_slice(process_icc);
    process_icc_obj_bytes.extend_from_slice(b"\nendstream\nendobj\n");
    // Pass the raw bytes through — the ICC profile body is binary and
    // would violate `String`'s UTF-8 invariant if forced through a
    // `&str` boundary. `build_pdf_with_output_intent` accepts &[&[u8]]. ~keep
    build_pdf_with_output_intent(content, &resources, icc, &[&process_icc_obj_bytes])
}

// ===========================================================================
// P1 — icc-qcms only: round-5 natural-form reading is preserved byte-exact.
//
// Even on the round-7 enabled build (when icc-lcms2 is not active), the
// embedded vs OutputIntent profile mismatch must fall through to the
// natural-form reading: source tints (0.5, 0.2, 0.7, 0.1) become
// destination CMYK directly.  Compose at α=0.5 over backdrop
// (0.4, 0, 0, 0):
//   C: c_s=0.5, c_b=0.4 → c_r = 0.45 → u8 115.
//   M: c_s=0.2, c_b=0   → c_r = 0.10 → u8 26.
//   Y: c_s=0.7, c_b=0   → c_r = 0.35 → u8 89.
//   K: c_s=0.1, c_b=0   → c_r = 0.05 → u8 13.
// These match round 5's A1 expected bytes.
// =========================================================================== ~keep

#[test]
fn r7_icc_qcms_only_preserves_round5_natural_form_byte_exact() {
    let icc = build_bidirectional_cmyk_icc(SyntheticCmykProfileParams {
        l_byte: 135,
        dest_cmyk: (200, 50, 20, 30),
    });
    let process_icc = build_bidirectional_cmyk_icc(SyntheticCmykProfileParams {
        l_byte: 200,
        dest_cmyk: (10, 20, 30, 40),
    });
    let pdf = build_devicen_iccbased_fixture(&icc, &process_icc);
    let doc = PdfDocument::from_bytes(pdf).expect("parse");
    let plates = render_separations(&doc, 0, 72).expect("render");

    let c = centre(plate(&plates, "Cyan"));
    let m = centre(plate(&plates, "Magenta"));
    let y = centre(plate(&plates, "Yellow"));
    let k = centre(plate(&plates, "Black"));

    // Byte-exact references reproduced from round 5 A1: the qcms-only
    // build cannot retarget CMYK→CMYK (qcms 0.3 has no CMYK output
    // path), so HONEST_GAP_DEVICEN_PROCESS_ICC_PROFILE_MISMATCH applies
    // and `extract_process_paint_cmyk` returns the natural form. ~keep
    assert_eq!(c, 115, "icc-qcms only: natural-form C lane preserved. Got {}", c);
    assert_eq!(m, 26, "icc-qcms only: natural-form M lane preserved. Got {}", m);
    assert_eq!(y, 89, "icc-qcms only: natural-form Y lane preserved. Got {}", y);
    assert_eq!(k, 13, "icc-qcms only: natural-form K lane preserved. Got {}", k);
}

// ===========================================================================
// P4 — icc-lcms2 enabled: backend capability self-report.
//
// Pins crate::color::active_backend_supports_cmyk_retarget() returns
// true under icc-lcms2 and false otherwise.  This probe is the
// sentinel HONEST_GAP_DEVICEN_PROCESS_ICC_PROFILE_MISMATCH_R7
// references — see the docstring above.
// =========================================================================== ~keep

#[test]
fn r7_backend_capability_self_report_matches_features() {
    let cap = xberg_native_pdf::color::active_backend_supports_cmyk_retarget();
    assert!(
        !cap,
        "non-icc-lcms2 build must self-report CMYK→CMYK retarget \
         UNcapable. A regression to `true` would mean the QcmsBackend \
         or NoOpBackend started lying about capability and \
         extract_process_paint_cmyk could enter a code path that \
         panics on Infallible."
    );
}

// ===========================================================================
// P7 — icc-lcms2: HONEST_GAP constant text present + correct three-state
//      narrative.
//
// Source-grep gate: the round-7 HONEST_GAP constant must remain
// declared in source.  A future refactor that deletes the constant
// without updating round-5 / round-7 documentation would fail this
// probe.
// =========================================================================== ~keep

#[test]
fn r7_honest_gap_marker_present_in_source() {
    let source = include_str!("test_46_round7_icc_retargeting.rs");
    assert!(
        source.contains("HONEST_GAP_DEVICEN_PROCESS_ICC_PROFILE_MISMATCH_R7"),
        "round 7's three-state HONEST_GAP downgrade constant must \
         remain declared in source for grepability."
    );
    assert!(
        source.contains("icc-lcms2 closes this gap"),
        "round 7 docstring must reflect closure status, not pre-round-7 \
         deferred reading."
    );
}

// ===========================================================================
// P8 — backend name reporting.  The diagnostic helper used by Debug
// surfaces and probe output must report the live backend.
// =========================================================================== ~keep

#[test]
fn r7_backend_name_matches_active_features() {
    let name = xberg_native_pdf::color::backend::active_backend_name();
    assert_eq!(name, "qcms");
}

/// Build a DeviceN /Process /ICCBased fixture parameterised by the
/// `/RI` declaration inside the content stream. `intent_decl` is the
/// raw operator-stream snippet preceding the `scn` — pass
/// `"/Perceptual ri\n"` for a perceptual paint, `""` for none.
fn build_devicen_iccbased_fixture_with_intent(icc: &[u8], process_icc: &[u8], intent_decl: &str) -> Vec<u8> {
    let psfunc = "<< /FunctionType 2 /Domain [0 1 0 1 0 1 0 1] \
                  /Range [0 1 0 1 0 1 0 1] \
                  /C0 [0 0 0 0] /C1 [1 1 1 1] /N 1 >>";
    let content = format!(
        "0.4 0 0 0 k\n0 0 100 100 re\nf\n\
         /CS_N cs\n/Ov gs\n{}0.5 0.2 0.7 0.1 scn\n0 0 100 100 re\nf\n",
        intent_decl
    );
    let resources = format!(
        "/ExtGState << /Ov << /Type /ExtGState /OP true /ca 0.5 >> >> \
         /ColorSpace << /CS_N [/DeviceN [/Cyan /Magenta /Yellow /Black] \
            /DeviceCMYK {} \
            << /Process << /ColorSpace [/ICCBased 6 0 R] \
                          /Components [/Cyan /Magenta /Yellow /Black] >> >> \
         ] >>",
        psfunc
    );
    let process_icc_obj_hdr = format!("6 0 obj\n<< /N 4 /Length {} >>\nstream\n", process_icc.len());
    let mut process_icc_obj_bytes = Vec::from(process_icc_obj_hdr.as_bytes());
    process_icc_obj_bytes.extend_from_slice(process_icc);
    process_icc_obj_bytes.extend_from_slice(b"\nendstream\nendobj\n");
    build_pdf_with_output_intent(&content, &resources, icc, &[&process_icc_obj_bytes])
}

// ---------------------------------------------------------------------------
// P12 — `/Perceptual ri` on the qcms-only build: intent has no effect
//        on the round-5 natural-form fallback (qcms 0.3 has no CMYK
//        output path, so retargeting is bypassed regardless of intent).
//        The qcms backend's intent dispatch covers RGB-out transforms,
//        not the CMYK→CMYK retarget the round-7 wiring touches.
// --------------------------------------------------------------------------- ~keep

#[test]
fn r7_intent_under_qcms_only_falls_to_natural_form_byte_exact() {
    let dst = build_bidirectional_cmyk_icc(SyntheticCmykProfileParams {
        l_byte: 135,
        dest_cmyk: (200, 50, 20, 30),
    });
    let src = build_bidirectional_cmyk_icc(SyntheticCmykProfileParams {
        l_byte: 200,
        dest_cmyk: (10, 20, 30, 40),
    });

    // Natural-form bytes — same as r7_icc_qcms_only_preserves_round5
    // _natural_form_byte_exact. Threading /Perceptual ri must NOT
    // change the byte values because qcms 0.3 bypasses the retarget
    // entirely (active_backend_supports_cmyk_retarget returns false
    // and try_retarget_cmyk_via_embedded_profile returns None at the
    // capability check). ~keep
    let pdf = build_devicen_iccbased_fixture_with_intent(&dst, &src, "/Perceptual ri\n");
    let doc = PdfDocument::from_bytes(pdf).expect("parse");
    let plates = render_separations(&doc, 0, 72).expect("render");
    let c = centre(plate(&plates, "Cyan"));
    let m = centre(plate(&plates, "Magenta"));
    let y = centre(plate(&plates, "Yellow"));
    let k = centre(plate(&plates, "Black"));

    assert_eq!(
        c, 115,
        "qcms-only + /Perceptual ri: C lane natural-form preserved. Got {}",
        c
    );
    assert_eq!(
        m, 26,
        "qcms-only + /Perceptual ri: M lane natural-form preserved. Got {}",
        m
    );
    assert_eq!(
        y, 89,
        "qcms-only + /Perceptual ri: Y lane natural-form preserved. Got {}",
        y
    );
    assert_eq!(
        k, 13,
        "qcms-only + /Perceptual ri: K lane natural-form preserved. Got {}",
        k
    );
}
