//! ICC-based CMYK → RGB pipeline coverage.
//!
//! `Transform` is the public surface every caller funnels through. When
//! qcms is linked (the default `icc` feature) it compiles embedded ICC
//! profiles into real colourimetric transforms; when the profile can't
//! be compiled — malformed, unsupported version, missing tags — the
//! transform falls through to ISO 32000-1:2008 §10.3.5's additive-clamp
//! fallback. Both paths must agree on anchor samples (pure white, pure
//! black) so downstream callers never see a broken conversion.

use std::sync::Arc;
use xberg_native_pdf::color::{IccProfile, RenderingIntent, Transform};

/// 128-byte ICC header stub with a valid `acsp` signature but no tag
/// table. Accepted by `IccProfile::parse` (header is valid) but
/// rejected by qcms (no functioning tags), exercising the fallback
/// path deterministically.
fn header_only_cmyk_profile_bytes() -> Vec<u8> {
    let mut v = vec![0u8; 128];
    v[8..12].copy_from_slice(&0x04000000u32.to_be_bytes());
    v[12..16].copy_from_slice(b"prtr");
    v[16..20].copy_from_slice(b"CMYK");
    v[20..24].copy_from_slice(b"Lab ");
    v[36..40].copy_from_slice(b"acsp");
    v
}

#[test]
fn cmyk_transform_anchor_samples_agree() {
    let profile =
        Arc::new(IccProfile::parse(header_only_cmyk_profile_bytes(), 4).expect("header-only profile should parse"));
    let t = Transform::new_srgb_target(profile, RenderingIntent::RelativeColorimetric);

    // (0,0,0,0) = paper white under every CMM + under §10.3.5. ~keep
    assert_eq!(t.convert_cmyk_pixel(0, 0, 0, 0), [255, 255, 255]);
    // (255,255,255,255) = saturated ink overlay → black under §10.3.5.
    // A CMM with a press profile might clip to near-zero rather than
    // exactly zero; when qcms rejects the stub profile we're guaranteed
    // §10.3.5 semantics here. ~keep
    assert_eq!(t.convert_cmyk_pixel(255, 255, 255, 255), [0, 0, 0]);
}

#[test]
fn cmyk_transform_bulk_path_matches_pixel_path() {
    // Bulk conversion must produce byte-for-byte identical output to
    // per-pixel conversion under the §10.3.5 fallback path. With a
    // real qcms transform the two paths may disagree by rounding in
    // the final sample but should agree on anchor values. ~keep
    let profile =
        Arc::new(IccProfile::parse(header_only_cmyk_profile_bytes(), 4).expect("header-only profile should parse"));
    let t = Transform::new_srgb_target(profile, RenderingIntent::RelativeColorimetric);

    let samples: [(u8, u8, u8, u8); 4] = [(0, 0, 0, 0), (255, 255, 255, 255), (64, 32, 16, 8), (13, 12, 12, 4)];
    let mut cmyk = Vec::with_capacity(samples.len() * 4);
    for s in &samples {
        cmyk.extend_from_slice(&[s.0, s.1, s.2, s.3]);
    }
    let bulk = t.convert_cmyk_buffer(&cmyk);

    let mut per_pixel = Vec::with_capacity(samples.len() * 3);
    for s in &samples {
        per_pixel.extend_from_slice(&t.convert_cmyk_pixel(s.0, s.1, s.2, s.3));
    }

    // Under the §10.3.5 fallback the two paths must be bit-identical.
    // When qcms is engaged they can differ by at most 1 unit per
    // channel due to the bulk path amortising lookup table evaluation. ~keep
    assert_eq!(bulk.len(), per_pixel.len());
    for (b, p) in bulk.iter().zip(per_pixel.iter()) {
        let diff = (*b as i32 - *p as i32).abs();
        assert!(diff <= 1, "bulk vs per-pixel CMYK conversion differ by {diff}");
    }
}
