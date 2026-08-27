//! A signed unit exponent (`s−1`, `m−2`) must not be rewritten into a Unicode
//! sub/superscript digit.
//!
//! The document-level pass substitutes ASCII digits in lowered/raised
//! smaller-font spans with their Unicode sub/superscript equivalents — correct
//! for chemistry (`H₂O`) and ordinals (`8ᵗʰ`). But a scientific unit exponent
//! such as `km s−1` is, by the plaintext convention every reference extractor
//! follows, kept as ASCII `s−1`. The geometric classifier fires inconsistently
//! on these (some occurrences become `s−₁`, others stay `s−1`), so the result is
//! both wrong and non-deterministic. A digit whose nearest preceding glyph is a
//! minus/hyphen sign is a signed exponent and must be left as ASCII.

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
fn signed_unit_exponent_stays_ascii() {
    let out = build_and_extract(&[
        ("s", 100.0, 200.0, "Helvetica", 14.0),
        ("-1", 110.0, 197.0, "Helvetica", 9.0),
        ("s", 124.0, 200.0, "Helvetica", 14.0),
    ]);

    let collapsed: String = out.split_whitespace().collect();
    assert!(
        !collapsed.contains('\u{2081}') && !collapsed.contains('\u{208B}'),
        "signed unit exponent wrongly rewritten to subscript: {collapsed:?}"
    );
    assert!(
        collapsed.contains("-1"),
        "expected ASCII '-1' to survive, got: {collapsed:?}"
    );
}

#[test]
fn chemistry_subscript_still_substitutes() {
    // Guard must NOT regress the real subscript case: `H2O` → `H₂O`. The digit's
    // preceding glyph is the letter `H`, not a sign. ~keep
    let out = build_and_extract(&[
        ("H", 100.0, 200.0, "Helvetica", 14.0),
        ("2", 112.0, 197.0, "Helvetica", 9.0),
        ("O", 122.0, 200.0, "Helvetica", 14.0),
    ]);

    let collapsed: String = out.split_whitespace().collect();
    assert_eq!(collapsed, "H\u{2082}O", "chemistry subscript regressed: {collapsed:?}");
}
