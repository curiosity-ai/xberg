//! A path's rendered extent must account for `stroke_width`.
//!
//! Print-era producers draw a table's vertical rule as a ~1 pt horizontal
//! segment stroked with a line width equal to the table height
//! (`430 w … 0 0 m .998 0 l S` renders a 1×430 pt vertical bar). The
//! geometric bbox of that path is 1×0 pt, so every bbox-classifying
//! consumer — `is_table_primitive()`, the line-based table detector — saw a
//! speck and the whole grid went undetected. `rendered_bbox()` exposes the
//! stroke-inflated extents (ISO 32000-1:2008 §8.4.3.2 line width straddles
//! the path; butt caps add nothing along the axis) and the classifiers use
//! it.

mod common;
use common::build_minimal_pdf_raw;

use xberg_native_pdf::document::PdfDocument;

/// A fully ruled 3-column × 3-row grid in the print-era idiom: horizontal
/// rules are ordinary thin strokes; the four vertical rules are ~1 pt
/// horizontal segments stroked 90 pt wide (the table height), positioned at
/// the vertical midline so the stroke spans the full grid.
fn fixture_pdf() -> Vec<u8> {
    let mut content = Vec::new();
    content.extend_from_slice(b"0 J 0 j 1 w\n");
    for y in [700, 670, 640, 610] {
        content.extend_from_slice(format!("q 1 0 0 1 100 {y} cm 0 0 m 300 0 l S Q\n").as_bytes());
    }
    content.extend_from_slice(b"90 w\n");
    for x in [100, 200, 300, 400] {
        content.extend_from_slice(format!("q 1 0 0 1 {x} 655 cm 0 0 m 1 0 l S Q\n").as_bytes());
    }
    content.extend_from_slice(b"BT /F1 10 Tf\n");
    for (row, y) in [(0, 685), (1, 655), (2, 625)] {
        for (col, x) in [(0, 110), (1, 210), (2, 310)] {
            content.extend_from_slice(format!("1 0 0 1 {x} {y} Tm (R{row}C{col}) Tj\n").as_bytes());
        }
    }
    content.extend_from_slice(b"ET");
    build_minimal_pdf_raw(&content, b"/Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]")
}

#[test]
fn wide_stroke_rule_reports_rendered_extents() {
    let doc = PdfDocument::from_bytes(fixture_pdf()).expect("parse fixture");
    let rules: Vec<_> = doc
        .extract_paths(0)
        .expect("extract paths")
        .into_iter()
        .filter(|p| p.stroke_width > 50.0)
        .collect();
    assert_eq!(rules.len(), 4, "four wide-stroke column rules");

    for p in &rules {
        let r = p.rendered_bbox();
        assert!(p.bbox.width <= 1.5 && p.bbox.height <= 0.5);
        assert!(
            (r.height - 90.0).abs() < 1.0,
            "rendered height must equal the stroke width, got {}",
            r.height
        );
        assert!(
            r.width <= 1.5 + 1.0,
            "butt caps must not inflate the segment axis, got width {}",
            r.width
        );
        assert!(
            (r.y - 610.0).abs() < 1.0,
            "rendered bar must span downward from the midline, got y {}",
            r.y
        );
    }
}

#[test]
fn wide_stroke_rule_classifies_as_table_primitive_and_vertical_line() {
    let doc = PdfDocument::from_bytes(fixture_pdf()).expect("parse fixture");
    for p in doc
        .extract_paths(0)
        .expect("extract paths")
        .iter()
        .filter(|p| p.stroke_width > 50.0)
    {
        assert!(p.is_table_primitive(), "a rendered 1x90 bar is a table primitive");
        assert!(p.is_vertical_line(2.0), "a rendered 1x90 bar is a vertical line");
        assert!(!p.is_horizontal_line(2.0), "not a horizontal line");
    }
}

#[test]
fn stroke_width_is_ctm_scaled() {
    // `2 w` under a `3 0 0 3` CTM strokes 6pt wide (§8.4.3.2: line width is
    // in user space, transformed by the CTM like all geometry). ~keep
    let content: &[u8] = b"2 w q 3 0 0 3 30 30 cm 0 0 m 50 0 l S Q";
    let doc = PdfDocument::from_bytes(build_minimal_pdf_raw(
        content,
        b"/Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]",
    ))
    .expect("parse");
    let paths = doc.extract_paths(0).expect("paths");
    assert_eq!(paths.len(), 1);
    assert!(
        (paths[0].stroke_width - 6.0).abs() < 0.01,
        "stroke_width must be in the same (CTM-transformed) space as bbox, got {}",
        paths[0].stroke_width
    );
    let r = paths[0].rendered_bbox();
    assert!((r.height - 6.0).abs() < 0.01, "rendered height {}", r.height);
    assert!((r.y - 27.0).abs() < 0.01, "rendered y {}", r.y);
}

#[test]
fn full_grid_with_stroke_width_rules_is_detected_as_table() {
    let doc = PdfDocument::from_bytes(fixture_pdf()).expect("parse fixture");
    let tables = doc.extract_tables(0).expect("extract tables");
    assert!(!tables.is_empty(), "the fully ruled grid must be detected as a table");
    let cells: Vec<String> = tables[0]
        .rows
        .iter()
        .flat_map(|r| r.cells.iter())
        .map(|c| c.text.trim().to_string())
        .collect();
    assert!(
        cells.iter().any(|c| c.contains("R0C0")) && cells.iter().any(|c| c.contains("R2C2")),
        "grid cells must be captured row-major, got {:?}",
        cells
    );
}
