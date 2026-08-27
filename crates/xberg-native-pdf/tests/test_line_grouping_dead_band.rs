//! Regression tests for the widened same-line threshold fix:
//! forward-gap guard, `should_insert_space` harmonization, and
//! threshold-boundary behavior.

mod common;
use common::{build_and_extract_page0, text_run_op};

fn build_and_extract(runs: &[(&str, f32, f32, &str, f32)]) -> String {
    let mut content = String::new();
    for &(text, x, y, font, size) in runs {
        content.push_str(&text_run_op(text, x, y, font, size));
    }
    build_and_extract_page0(&content)
}

fn newline_between(out: &str, before: &str, after: &str) -> bool {
    let a = out
        .find(before)
        .unwrap_or_else(|| panic!("missing {:?}: {:?}", before, out));
    let b = out
        .find(after)
        .unwrap_or_else(|| panic!("missing {:?}: {:?}", after, out));
    out[a + before.len()..b].contains('\n')
}

// A. Title (20pt) + small right-aligned marker (10pt), y_diff=4.5pt. ~keep
#[test]
fn title_plus_right_aligned_marker_splits() {
    let out = build_and_extract(&[
        ("Form 1040", 72.0, 700.0, "Helvetica", 20.0),
        ("(Rev. Jan 2024)", 260.0, 695.5, "Helvetica", 10.0),
    ]);

    assert!(newline_between(&out, "Form 1040", "(Rev. Jan 2024)"), "got {:?}", out);
}

// B. Header (16pt) + small instruction (9pt), y_diff=3.5pt. ~keep
#[test]
fn header_plus_small_instruction_splits() {
    let out = build_and_extract(&[
        ("Section 3", 72.0, 700.0, "Helvetica", 16.0),
        ("(see instructions)", 240.0, 696.5, "Helvetica", 9.0),
    ]);

    assert!(
        newline_between(&out, "Section 3", "(see instructions)"),
        "got {:?}",
        out
    );
}

// C. Body (11pt) + small annotation (8pt) in dead-band, y_diff=3.5pt. ~keep
#[test]
fn body_plus_small_annotation_splits() {
    let out = build_and_extract(&[
        ("See reference 12", 72.0, 700.0, "Helvetica", 11.0),
        ("[updated 2024]", 220.0, 696.5, "Helvetica", 8.0),
    ]);

    assert!(
        newline_between(&out, "See reference 12", "[updated 2024]"),
        "got {:?}",
        out
    );
}

// D. Two-row small-gutter dead-band layout. K=1.5 accepts narrow intra-row
// gaps as residual — pin row-boundary integrity only. ~keep
#[test]
fn small_gutter_dead_band_rows_preserved() {
    let out = build_and_extract(&[
        ("AA1", 72.0, 700.0, "Helvetica", 10.0),
        ("BB1", 92.0, 700.0, "Helvetica", 10.0),
        ("AA2", 72.0, 685.6, "Helvetica", 10.0),
        ("BB2", 92.0, 682.1, "Helvetica", 10.0),
    ]);

    assert!(newline_between(&out, "BB1", "AA2"), "got {:?}", out);
}

// E1. 12pt pair at y_diff=5.99 < 14.4 = 1.2*min_fs: stays same-line. ~keep
#[test]
fn threshold_boundary_inside_stays_same_line() {
    let out = build_and_extract(&[
        ("LLL", 72.0, 700.0, "Helvetica", 12.0),
        ("RRR", 95.0, 694.01, "Helvetica", 12.0),
    ]);

    let out = out.trim_end();
    assert!(!newline_between(out, "LLL", "RRR"), "got {:?}", out);
}

// E2. 12pt pair at y_diff=14.51 > 14.4 = 1.2*min_fs: splits into two lines. ~keep
#[test]
fn threshold_boundary_outside_splits() {
    let out = build_and_extract(&[
        ("LLL", 72.0, 700.0, "Helvetica", 12.0),
        ("RRR", 95.0, 685.49, "Helvetica", 12.0),
    ]);

    assert!(newline_between(&out, "LLL", "RRR"), "got {:?}", out);
}

// 12pt pair at y_diff=1.5 (below old 2.0 threshold): the forward-gap
// guard's y_diff gate must not fire even with a wide word-spacing gap. ~keep
#[test]
fn pair_below_old_threshold_space_merges() {
    let out = build_and_extract(&[
        ("Alpha", 100.0, 700.0, "Helvetica", 12.0),
        ("Beta", 180.0, 698.5, "Helvetica", 12.0),
    ]);

    let out = out.trim_end();
    assert!(out.contains("Alpha Beta"), "got {:?}", out);
}

// 12pt pair in dead-band (y_diff=4.0) with a narrow gap ~5pt
// (gap/fs ≈ 0.4): documented residual — space-merges rather than splits. ~keep
#[test]
fn pair_dead_band_narrow_gap_space_merges() {
    let out = build_and_extract(&[
        ("First", 100.0, 700.0, "Helvetica", 12.0),
        ("Second", 128.0, 696.0, "Helvetica", 12.0),
    ]);

    let out = out.trim_end();
    assert!(out.contains("First Second"), "got {:?}", out);
    assert!(!newline_between(out, "First", "Second"), "got {:?}", out);
}

// 12pt pair at y_diff=15.0 (above the 14.4 = 1.2*fs same-line threshold): splits. ~keep
#[test]
fn pair_above_new_threshold_splits() {
    let out = build_and_extract(&[
        ("High", 100.0, 700.0, "Helvetica", 12.0),
        ("Low", 100.0, 685.0, "Helvetica", 12.0),
    ]);

    assert!(newline_between(&out, "High", "Low"), "got {:?}", out);
}

// Wide-gutter two-column, fs=10, intra-row y_diff=4.0 and gap >> 1.5*fs.
// Forward-gap guard fires regardless of the dead-band Y-jitter. ~keep
#[test]
fn wide_gutter_dead_band_column_splits() {
    let out = build_and_extract(&[
        ("Left", 80.0, 700.0, "Helvetica", 10.0),
        ("Right", 400.0, 696.0, "Helvetica", 10.0),
    ]);

    assert!(newline_between(&out, "Left", "Right"), "got {:?}", out);
}

// F. Aligned two-column negative control — fix must not change extraction
// when every row has identical baselines. ~keep
#[test]
fn aligned_two_column_extracts_unchanged() {
    let out = build_and_extract(&[
        ("HdrLeft", 72.0, 700.0, "Helvetica", 12.0),
        ("HdrRight", 300.0, 700.0, "Helvetica", 12.0),
        ("BodyLeft", 72.0, 685.6, "Helvetica", 12.0),
        ("BodyRight", 300.0, 685.6, "Helvetica", 12.0),
        ("FootLeft", 72.0, 671.2, "Helvetica", 12.0),
        ("FootRight", 300.0, 671.2, "Helvetica", 12.0),
    ]);

    for cell in ["HdrLeft", "HdrRight", "BodyLeft", "BodyRight", "FootLeft", "FootRight"] {
        assert!(out.contains(cell), "missing {:?}: {:?}", cell, out);
    }
    assert!(newline_between(&out, "HdrRight", "BodyLeft"), "got {:?}", out);
    assert!(newline_between(&out, "BodyRight", "FootLeft"), "got {:?}", out);
}
