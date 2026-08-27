mod common;
use common::{build_and_extract_page0, text_run_op};

fn build_and_extract(runs: &[(&str, f32, f32, &str, f32)]) -> String {
    let mut content = String::new();
    for &(text, x, y, font, size) in runs {
        content.push_str(&text_run_op(text, x, y, font, size));
    }
    build_and_extract_page0(&content)
}

#[test]
fn superscript_12pt_offset_stays_on_one_line() {
    let out = build_and_extract(&[
        ("8", 140.0, 180.0, "Helvetica", 28.0),
        ("th", 156.0, 192.0, "Helvetica", 20.0),
    ]);

    let out = out.trim_end();
    assert!(!out.contains('\n'), "got {:?}", out);
    assert!(out.contains('8') && out.contains("th"), "got {:?}", out);
}

#[test]
fn superscript_extracts_with_correct_glyph_order() {
    let out = build_and_extract(&[
        ("8", 140.0, 180.0, "Helvetica", 28.0),
        ("th", 156.0, 192.0, "Helvetica", 20.0),
    ]);

    assert_eq!(out.trim_end(), "8th", "got {:?}", out.trim_end());
}

#[test]
fn subscript_between_baseline_letters_stays_in_reading_order() {
    let out = build_and_extract(&[
        ("H", 100.0, 200.0, "Helvetica", 14.0),
        ("2", 112.0, 197.0, "Helvetica", 9.0),
        ("O", 122.0, 200.0, "Helvetica", 14.0),
    ]);

    // The document-level pass substitutes ASCII digits in
    // lowered + smaller-font spans with their Unicode subscript
    // equivalents (U+2080..U+2089). The chemistry-style formula
    // therefore extracts as "H\u{2082}O" — the digit's Unicode
    // codepoint preserves the subscript semantics that downstream
    // search/index passes need. Reading order is still the
    // assertion target: H, then subscript, then O. ~keep
    let collapsed: String = out.split_whitespace().collect();
    assert_eq!(collapsed, "H\u{2082}O", "got {:?}", out.trim_end());
}

#[test]
fn three_glyph_run_in_distinct_bands_is_x_ordered() {
    let out = build_and_extract(&[
        ("a", 100.0, 200.0, "Helvetica", 14.0),
        ("b", 112.0, 203.0, "Helvetica", 12.0),
        ("c", 124.0, 206.0, "Helvetica", 10.0),
    ]);

    let collapsed: String = out.split_whitespace().collect();
    assert_eq!(collapsed, "abc", "got {:?}", out.trim_end());
}

#[test]
fn baseline_same_y_stays_on_one_line() {
    let out = build_and_extract(&[
        ("8", 140.0, 180.0, "Helvetica", 28.0),
        ("th", 156.0, 180.0, "Helvetica", 20.0),
    ]);

    assert_eq!(out.trim_end(), "8th", "got {:?}", out.trim_end());
}

#[test]
fn two_lines_normal_leading_still_split() {
    let out = build_and_extract(&[
        ("First line of body text.", 72.0, 700.0, "Helvetica", 12.0),
        ("Second line of body text.", 72.0, 685.6, "Helvetica", 12.0),
    ]);

    let first = out.find("First line").expect("first line present");
    let second = out.find("Second line").expect("second line present");
    let between = &out[first + "First line".len()..second];
    assert!(between.contains('\n'), "got {:?}", out);
}

#[test]
fn multi_line_body_text_preserves_breaks() {
    let lines = ["LineAAA", "LineBBB", "LineCCC", "LineDDD", "LineEEE", "LineFFF"];

    let mut y = 700.0;
    let runs: Vec<(&str, f32, f32, &str, f32)> = lines
        .iter()
        .map(|line| {
            let run = (*line, 72.0, y, "Helvetica", 12.0);
            y -= 14.4;
            run
        })
        .collect();
    let out = build_and_extract(&runs);

    for pair in lines.windows(2) {
        let a = out
            .find(pair[0])
            .unwrap_or_else(|| panic!("missing {:?}: {:?}", pair[0], out));
        let b = out
            .find(pair[1])
            .unwrap_or_else(|| panic!("missing {:?}: {:?}", pair[1], out));
        let between = &out[a + pair[0].len()..b];
        assert!(
            between.contains('\n'),
            "missing newline between {:?} and {:?}: {:?}",
            pair[0],
            pair[1],
            out
        );
    }
}

#[test]
fn superscript_then_next_line_still_breaks() {
    let out = build_and_extract(&[
        ("8", 140.0, 700.0, "Helvetica", 28.0),
        ("th", 156.0, 712.0, "Helvetica", 20.0),
        ("Next paragraph line.", 72.0, 672.0, "Helvetica", 12.0),
    ]);

    assert!(out.contains('8') && out.contains("th"), "got {:?}", out);

    let super_end = out.find("th").or_else(|| out.find('8')).expect("superscript present");
    let body_start = out.find("Next paragraph line").expect("body line present");
    let between = &out[super_end..body_start];
    assert!(between.contains('\n'), "got {:?}", out);
}
