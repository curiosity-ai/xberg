//! Booktabs-style ("three-line") tables must survive table detection.
//!
//! Academic templates (LaTeX `booktabs`, many journal styles) rule a table
//! with only a few full-width HORIZONTAL strokes — a heavy top rule, a thin
//! sub-header rule, a heavy bottom rule — each drawn as a geometrically
//! zero-height straight-line stroke, with NO vertical column dividers at
//! all. Detection therefore rides entirely on the horizontal-rule-bounded
//! fallback (`detect_tables_from_horizontal_rules`), which is gated on the
//! intersection- and cluster-based pipelines finding nothing and on
//! `v_edges` being empty. Any perturbation of those gates silently scatters
//! the table into disconnected paragraphs.

mod common;
use common::build_minimal_pdf_raw;

use xberg_native_pdf::document::PdfDocument;

/// A 6-row logistic-regression-style table in the booktabs idiom:
/// three full-width zero-height horizontal rules (0.8 pt top, 0.3 pt
/// sub-header, 0.8 pt bottom), no vertical rules, 4 text columns.
fn booktabs_fixture_pdf() -> Vec<u8> {
    booktabs_fixture_pdf_with_decorations(false)
}

/// Same table; `with_speck` additionally draws an unrelated decorative
/// stroke far from the table — a ~1 pt segment with a heavy (8 pt) stroke,
/// the shape of a tick mark / list dash / emphasis bar. Real academic pages
/// carry such marks; they must not disable table detection elsewhere on the
/// page.
fn booktabs_fixture_pdf_with_decorations(with_speck: bool) -> Vec<u8> {
    let mut content = Vec::new();
    content.extend_from_slice(b"0 J 0 j\n");
    if with_speck {
        content.extend_from_slice(b"8 w 300 120 m 301 120 l S\n");
    }
    content.extend_from_slice(b"0.8 w 100 710 m 500 710 l S\n");
    content.extend_from_slice(b"0.3 w 100 688 m 500 688 l S\n");
    content.extend_from_slice(b"0.8 w 100 568 m 500 568 l S\n");
    content.extend_from_slice(b"BT /F1 10 Tf\n");
    let rows: [[&str; 4]; 6] = [
        ["Variable", "Beta", "SE", "p-Value"],
        ["Age", "0.042", "0.011", "0.001"],
        ["Sex", "0.318", "0.142", "0.025"],
        ["BMI", "0.077", "0.023", "0.004"],
        ["Smoker", "0.512", "0.201", "0.011"],
        ["Diabetes", "0.694", "0.233", "0.003"],
    ];
    let ys = [695, 672, 652, 632, 612, 592];
    let xs = [105, 260, 340, 430];
    for (row, y) in rows.iter().zip(ys) {
        for (cell, x) in row.iter().zip(xs) {
            content.extend_from_slice(format!("1 0 0 1 {x} {y} Tm ({cell}) Tj\n").as_bytes());
        }
    }
    content.extend_from_slice(b"ET");
    build_minimal_pdf_raw(&content, b"/Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]")
}

#[test]
fn booktabs_three_line_table_is_detected() {
    let doc = PdfDocument::from_bytes(booktabs_fixture_pdf()).expect("parse fixture");
    let tables = doc.extract_tables(0).expect("extract tables");
    assert!(
        !tables.is_empty(),
        "a booktabs (three horizontal rules, no vertical rules) table must be detected"
    );
    let cells: Vec<String> = tables[0]
        .rows
        .iter()
        .flat_map(|r| r.cells.iter())
        .map(|c| c.text.trim().to_string())
        .collect();
    assert!(
        cells.iter().any(|c| c.contains("Age")) && cells.iter().any(|c| c.contains("0.042")),
        "row labels must stay aligned with their values, got {:?}",
        cells
    );
    assert!(
        tables[0].rows.len() >= 5,
        "all data rows must be captured, got {} rows",
        tables[0].rows.len()
    );
}

#[test]
fn booktabs_table_survives_unrelated_decorative_speck() {
    // A heavy-stroked ~1 pt decorative segment elsewhere on the page (a
    // tick mark / list dash) must not knock out the horizontal-rule table
    // path for the whole page. Uses the line-pipelines-only configuration
    // (`text_fallback = false`, as `extract_text` / `to_plain_text` do) so
    // the text-alignment fallback cannot mask a loss in the rule-based
    // path: the fallback rides on ideal synthetic alignment that real
    // academic tables don't have. ~keep
    let config = xberg_native_pdf::structure::spatial_table_detector::TableDetectionConfig {
        text_fallback: false,
        ..Default::default()
    };
    let doc = PdfDocument::from_bytes(booktabs_fixture_pdf_with_decorations(true)).expect("parse");
    let tables = doc.extract_tables_with_config(0, config).expect("extract tables");
    assert!(
        !tables.is_empty(),
        "booktabs table must still be detected with a decorative speck on the page"
    );
    assert!(
        tables[0].rows.len() >= 5,
        "all data rows must be captured, got {} rows",
        tables[0].rows.len()
    );
}

#[test]
fn booktabs_table_detected_by_line_pipelines_alone() {
    let config = xberg_native_pdf::structure::spatial_table_detector::TableDetectionConfig {
        text_fallback: false,
        ..Default::default()
    };
    let doc = PdfDocument::from_bytes(booktabs_fixture_pdf()).expect("parse");
    let tables = doc.extract_tables_with_config(0, config).expect("extract tables");
    assert!(!tables.is_empty(), "three-line table must be detected from its rules");
}

/// The inverse case: a dense-equation math page whose only strokes are
/// fraction bars (vinculums) — zero-height horizontal strokes at unrelated
/// x and y positions spread across the page, no vertical rules anywhere.
/// Geometrically each bar looks like a table rule; collectively they are
/// NOT a table: they don't share an x-range and they span most of the page
/// height. Geometry mirrors a real corpus regression (short bars 11-60pt
/// wide, 0.478pt stroke, ~half the page's y-extent).
fn scattered_fraction_bars_pdf() -> Vec<u8> {
    let mut content = Vec::new();
    content.extend_from_slice(b"0 J 0 j 0.478 w\n");
    let bars: [(i32, i32, i32); 14] = [
        (100, 279, 715),
        (120, 34, 668),
        (310, 22, 668),
        (100, 150, 622),
        (140, 11, 575),
        (260, 47, 575),
        (390, 30, 575),
        (100, 120, 528),
        (150, 58, 481),
        (330, 16, 481),
        (100, 210, 434),
        (180, 25, 388),
        (300, 40, 388),
        (430, 13, 388),
    ];
    for (x, w, y) in bars {
        content.extend_from_slice(format!("{x} {y} m {} {y} l S\n", x + w).as_bytes());
    }
    content.extend_from_slice(b"BT /F1 10 Tf\n");
    let text_lines: [(i32, i32); 12] = [
        (715, 1),
        (668, 2),
        (622, 3),
        (575, 4),
        (528, 5),
        (481, 6),
        (434, 7),
        (388, 8),
        (740, 9),
        (690, 10),
        (645, 11),
        (600, 12),
    ];
    for (y, i) in text_lines {
        if i <= 8 {
            content
                .extend_from_slice(format!("1 0 0 1 100 {} Tm (EqMarker{} alpha plus beta) Tj\n", y + 4, i).as_bytes());
            content.extend_from_slice(
                format!("1 0 0 1 100 {} Tm (gamma minus delta over epsilon) Tj\n", y - 11).as_bytes(),
            );
        } else {
            content.extend_from_slice(
                format!(
                    "1 0 0 1 100 {} Tm (EqMarker{} where the coefficients satisfy the bound) Tj\n",
                    y, i
                )
                .as_bytes(),
            );
        }
    }
    content.extend_from_slice(b"ET");
    build_minimal_pdf_raw(&content, b"/Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]")
}

#[test]
fn scattered_fraction_bars_do_not_form_a_table() {
    // Line-pipelines-only, as extract_text uses: the fallback must not
    // group unrelated fraction bars into a fake table. ~keep
    let config = xberg_native_pdf::structure::spatial_table_detector::TableDetectionConfig {
        text_fallback: false,
        ..Default::default()
    };
    let doc = PdfDocument::from_bytes(scattered_fraction_bars_pdf()).expect("parse");
    let tables = doc.extract_tables_with_config(0, config).expect("extract tables");
    assert!(
        tables.is_empty(),
        "scattered fraction bars must not be grouped into a table, got {} table(s): {:?}",
        tables.len(),
        tables
            .iter()
            .map(|t| t.rows.iter().map(|r| r.cells.len()).collect::<Vec<_>>())
            .collect::<Vec<_>>()
    );
}

#[test]
fn scattered_fraction_bars_preserve_reading_order() {
    let doc = PdfDocument::from_bytes(scattered_fraction_bars_pdf()).expect("parse");
    let text = doc.extract_text(0).expect("extract text");
    let expected_order = [9, 1, 10, 2, 11, 3, 12, 4, 5, 6, 7, 8];
    let positions: Vec<usize> = expected_order
        .iter()
        .map(|i| {
            // Match "EqMarkerN " with a delimiter so EqMarker1 doesn't hit
            // EqMarker10/11/12. ~keep
            text.find(&format!("EqMarker{i} "))
                .unwrap_or_else(|| panic!("EqMarker{i} missing from output: {text}"))
        })
        .collect();
    let mut sorted = positions.clone();
    sorted.sort_unstable();
    assert_eq!(
        positions, sorted,
        "equation markers must appear in top-to-bottom order, got positions {positions:?} in: {text}"
    );
}

/// Draw one horizontal dashed border line as alternating dash segments and
/// tiny near-square "joint" strokes, mirroring the real geometry of an
/// author-affiliation box border (dash 198x0.96 / 76.92x0.96, joints
/// 0.96x0.96, all 1.0pt stroke).
fn push_dashed_border_line(content: &mut Vec<u8>, y: f32) {
    content.extend_from_slice(b"1 w\n");
    let segs: [(f32, f32); 4] = [(-24.52, 198.0), (173.48, 0.96), (174.44, 76.92), (251.36, 0.96)];
    for (x, w) in segs {
        content.extend_from_slice(format!("{x} {y} m {} {y1} l S\n", x + w, y1 = y + 0.96).as_bytes());
    }
}

/// Draw the vertical dashed borders that close a dash-bordered box between
/// two horizontal border lines: columns of short vertical dash segments
/// (0.96pt wide, ~4pt tall, 1.0pt stroke) at the box's left and right x —
/// the counterpart of `push_dashed_border_line` (the near-square joints in
/// the real dump come from these overlapping the horizontal runs).
fn push_dashed_border_verticals(content: &mut Vec<u8>, y_bottom: f32, y_top: f32) {
    content.extend_from_slice(b"1 w\n");
    for x in [-24.52_f32, 251.36] {
        let mut y = y_bottom + 1.5;
        while y + 10.0 < y_top {
            content.extend_from_slice(format!("{x} {y} m {x1} {y1} l S\n", x1 = x + 0.96, y1 = y + 10.0).as_bytes());
            y += 14.0;
        }
    }
}

/// One booktabs table at the real PMC coordinates: full-width rules at the
/// given ys (heavy/thin/heavy), 4 columns of row text between them.
fn push_booktabs_table(content: &mut Vec<u8>, y_top: i32, y_sub: i32, y_bot: i32, tag: &str) {
    content.extend_from_slice(format!("0.797 w 167.26 {y_top} m 559.27 {y_top} l S\n").as_bytes());
    content.extend_from_slice(format!("0.299 w 167.26 {y_sub} m 559.27 {y_sub} l S\n").as_bytes());
    content.extend_from_slice(format!("0.797 w 167.26 {y_bot} m 559.27 {y_bot} l S\n").as_bytes());
    content.extend_from_slice(b"BT /F1 9 Tf\n");
    let xs = [172, 320, 400, 480];
    let hdr_y = (y_top + y_sub) / 2 - 3;
    for (h, x) in ["Variable", "Beta", "SE", "pValue"].iter().zip(xs) {
        content.extend_from_slice(format!("1 0 0 1 {x} {hdr_y} Tm ({tag}{h}) Tj\n").as_bytes());
    }
    let n_rows = ((y_sub - y_bot - 6) / 14).max(1);
    for r in 0..n_rows {
        let ry = y_sub - 12 - r * 14;
        for (c, x) in xs.iter().enumerate() {
            content.extend_from_slice(format!("1 0 0 1 {x} {ry} Tm ({tag}R{r}C{c}) Tj\n").as_bytes());
        }
    }
    content.extend_from_slice(b"ET\n");
}

/// PMC8103274-shaped page: one booktabs table (y 576-663) plus dash-bordered
/// boxes (dash segments + near-square joint strokes) in bands well outside
/// the table's y-range — the composition on which the real table was lost.
fn pmc_dash_border_page_one_table() -> Vec<u8> {
    let mut content = Vec::new();
    content.extend_from_slice(b"0 J 0 j\n");
    for y in [795.92, 781.94, 699.98, 253.70, 239.72, 225.68, 211.70, 170.24] {
        push_dashed_border_line(&mut content, y);
    }
    push_dashed_border_verticals(&mut content, 781.94, 795.92);
    push_dashed_border_verticals(&mut content, 699.98, 781.94);
    push_dashed_border_verticals(&mut content, 239.72, 253.70);
    push_dashed_border_verticals(&mut content, 225.68, 239.72);
    push_dashed_border_verticals(&mut content, 211.70, 225.68);
    push_dashed_border_verticals(&mut content, 170.24, 211.70);
    push_booktabs_table(&mut content, 663, 647, 576, "T4");
    build_minimal_pdf_raw(&content, b"/Type /Page /Parent 2 0 R /MediaBox [0 0 612 842]")
}

/// PMC8025823-shaped page: TWO stacked booktabs tables (y 270-346 and
/// y 122-187) plus dash-bordered box lines, one band overlapping the first
/// table's y-range as measured on the real page.
fn pmc_dash_border_page_two_tables() -> Vec<u8> {
    let mut content = Vec::new();
    content.extend_from_slice(b"0 J 0 j\n");
    for y in [336.71, 322.73, 254.19] {
        push_dashed_border_line(&mut content, y);
    }
    push_dashed_border_verticals(&mut content, 322.73, 336.71);
    push_dashed_border_verticals(&mut content, 254.19, 322.73);
    push_booktabs_table(&mut content, 346, 330, 270, "T3");
    push_booktabs_table(&mut content, 187, 171, 122, "X");
    build_minimal_pdf_raw(&content, b"/Type /Page /Parent 2 0 R /MediaBox [0 0 612 842]")
}

#[test]
fn booktabs_table_survives_dash_bordered_box_on_page() {
    let config = xberg_native_pdf::structure::spatial_table_detector::TableDetectionConfig {
        text_fallback: false,
        ..Default::default()
    };
    let doc = PdfDocument::from_bytes(pmc_dash_border_page_one_table()).expect("parse");
    let tables = doc.extract_tables_with_config(0, config).expect("extract tables");
    assert!(
        !tables.is_empty(),
        "the booktabs table must be detected despite dash-bordered boxes elsewhere on the page"
    );
    let cells: Vec<String> = tables
        .iter()
        .flat_map(|t| t.rows.iter())
        .flat_map(|r| r.cells.iter())
        .map(|c| c.text.trim().to_string())
        .collect();
    assert!(
        cells.iter().any(|c| c.contains("T4R0C0")),
        "table body cells must be captured, got {:?}",
        cells
    );
}

#[test]
fn stacked_booktabs_tables_survive_dash_bordered_box_on_page() {
    let config = xberg_native_pdf::structure::spatial_table_detector::TableDetectionConfig {
        text_fallback: false,
        ..Default::default()
    };
    let doc = PdfDocument::from_bytes(pmc_dash_border_page_two_tables()).expect("parse");
    let tables = doc.extract_tables_with_config(0, config).expect("extract tables");
    let all_cells: Vec<String> = tables
        .iter()
        .flat_map(|t| t.rows.iter())
        .flat_map(|r| r.cells.iter())
        .map(|c| c.text.trim().to_string())
        .collect();
    assert!(
        all_cells.iter().any(|c| c.contains("T3R0C0")),
        "the first stacked table must be detected, got tables={} cells={:?}",
        tables.len(),
        all_cells
    );
}

/// The adversarial variant of the scattered-bars page: an aligned
/// multi-step derivation. Fraction bars in consecutive `align`-style
/// equation steps line up at the relation sign, so the vinculums share
/// their x-start AND have similar widths — they pass any x-range
/// coherence test — while spanning most of the page height. They still
/// are not table rules, and the derivation text between them must not be
/// reflowed into fake rows.
fn aligned_derivation_bars_pdf() -> Vec<u8> {
    let mut content = Vec::new();
    content.extend_from_slice(b"0 J 0 j 0.478 w\n");
    let bars: [(i32, i32, i32); 6] = [
        (250, 118, 720),
        (250, 112, 624),
        (250, 120, 528),
        (250, 115, 432),
        (250, 110, 336),
        (250, 118, 240),
    ];
    for (x, w, y) in bars {
        content.extend_from_slice(format!("{x} {y} m {} {y} l S\n", x + w).as_bytes());
    }
    // Aligned-derivation text: lhs at a fixed x, numerator above and
    // denominator below each bar at the bar's x — the repetitive column
    // structure a real derivation has. ~keep
    content.extend_from_slice(b"BT /F1 10 Tf\n");
    for (i, (x, _w, y)) in bars.iter().enumerate() {
        content.extend_from_slice(format!("1 0 0 1 150 {} Tm (DerivStep{} equals) Tj\n", y - 2, i + 1).as_bytes());
        content.extend_from_slice(format!("1 0 0 1 {} {} Tm (alpha sub k plus one) Tj\n", x, y + 5).as_bytes());
        content.extend_from_slice(format!("1 0 0 1 {} {} Tm (beta sub k minus one) Tj\n", x, y - 12).as_bytes());
        content.extend_from_slice(format!("1 0 0 1 500 {} Tm (open {} close) Tj\n", y - 2, i + 1).as_bytes());
    }
    content.extend_from_slice(b"ET");
    build_minimal_pdf_raw(&content, b"/Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]")
}

#[test]
fn aligned_derivation_bars_do_not_form_a_table() {
    let config = xberg_native_pdf::structure::spatial_table_detector::TableDetectionConfig {
        text_fallback: false,
        ..Default::default()
    };
    let doc = PdfDocument::from_bytes(aligned_derivation_bars_pdf()).expect("parse");
    let tables = doc.extract_tables_with_config(0, config).expect("extract tables");
    assert!(
        tables.is_empty(),
        "x-aligned derivation vinculums must not be grouped into a table, got {} table(s): {:?}",
        tables.len(),
        tables
            .iter()
            .map(|t| t.rows.iter().map(|r| r.cells.len()).collect::<Vec<_>>())
            .collect::<Vec<_>>()
    );
}

#[test]
fn aligned_derivation_bars_preserve_reading_order() {
    let doc = PdfDocument::from_bytes(aligned_derivation_bars_pdf()).expect("parse");
    let text = doc.extract_text(0).expect("extract text");
    let positions: Vec<usize> = (1..=6)
        .map(|i| {
            text.find(&format!("DerivStep{i} "))
                .unwrap_or_else(|| panic!("DerivStep{i} missing from output: {text}"))
        })
        .collect();
    let mut sorted = positions.clone();
    sorted.sort_unstable();
    assert_eq!(
        positions, sorted,
        "derivation steps must read top-to-bottom, got {positions:?} in: {text}"
    );
}

/// A framed code listing in the zine idiom: two full-width horizontal
/// rules with letter-spaced monospace source between them (glyphs of each
/// identifier drawn far enough apart that every letter is its own word).
/// The per-letter x-gaps look like column boundaries, but a region whose
/// words are mostly single letters is spread-out TEXT, not a table.
fn framed_code_listing_pdf() -> Vec<u8> {
    let mut content = Vec::new();
    content.extend_from_slice(b"0 J 0 j 0.4 w\n");
    content.extend_from_slice(b"100 700 m 500 700 l S\n");
    content.extend_from_slice(b"100 560 m 500 560 l S\n");
    content.extend_from_slice(b"BT /F1 9 Tf\n");
    let lines: [(&str, i32); 5] = [
        ("1 void segfault_handler(int sig)", 680),
        ("2 {  ucontext_t *ctx =", 655),
        ("3    (ucontext_t *) ptr;", 630),
        ("4    restore(ctx, sig);", 605),
        ("5 }", 580),
    ];
    for (line, y) in lines {
        for (i, ch) in line.chars().enumerate() {
            if ch == ' ' {
                continue;
            }
            let x = 105.0 + 11.0 * i as f32;
            let esc = match ch {
                '(' => "\\(".to_string(),
                ')' => "\\)".to_string(),
                '\\' => "\\\\".to_string(),
                c => c.to_string(),
            };
            content.extend_from_slice(format!("1 0 0 1 {x:.1} {y} Tm ({esc}) Tj\n").as_bytes());
        }
    }
    content.extend_from_slice(b"ET");
    build_minimal_pdf_raw(&content, b"/Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]")
}

#[test]
fn framed_code_listing_is_not_a_table() {
    let config = xberg_native_pdf::structure::spatial_table_detector::TableDetectionConfig {
        text_fallback: false,
        ..Default::default()
    };
    let doc = PdfDocument::from_bytes(framed_code_listing_pdf()).expect("parse");
    let tables = doc.extract_tables_with_config(0, config).expect("extract tables");
    assert!(
        tables.is_empty(),
        "letter-spaced code between rules must not become a table, got {} table(s): {:?}",
        tables.len(),
        tables
            .iter()
            .map(|t| t.rows.iter().map(|r| r.cells.len()).collect::<Vec<_>>())
            .collect::<Vec<_>>()
    );
}
