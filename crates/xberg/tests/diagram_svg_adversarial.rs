//! Adversarial coverage for SVG diagram recovery nesting depth (#1410 follow-up).
//!
//! `usvg` 0.48.1's own tree conversion (`convert_children`/`convert_element` in
//! `parser/converter.rs`) is mutually recursive with no depth limit of its own,
//! so a deeply nested `<g>` chain can overflow the stack before any of our code
//! runs — not a catchable panic, a process-killing SIGSEGV. `svg::recover`
//! rejects nesting past a bound with a cheap byte-level scan before handing
//! anything to `usvg::Tree::from_data`, and `collect_geometry`/the label pass
//! carry their own depth counters as a second line of defence.
//!
//! These tests drive that guard through the public extraction API, the same
//! path `output_format="dot"` takes in production, at depths well past the
//! bound but comfortably under the general XML reader's own depth cap so that
//! it is the SVG-specific guard being exercised here, not that one. Inputs are
//! generated in-test rather than committed as fixtures, and depths are kept
//! small enough that construction and scanning stay fast even though the
//! guard itself is proven at much larger scale in `svg/mod.rs`'s own unit
//! tests.

#![cfg(all(feature = "xml", feature = "svg"))]

mod helpers;
use helpers::extract_bytes_document_blocking;

use xberg::core::config::{ExtractionConfig, OutputFormat};

const SVG_MIME: &str = "image/svg+xml";

/// Past the SVG recovery guard (64 levels) but comfortably under the general
/// XML reader's own `SecurityLimits::max_xml_depth` (1024 by default), so
/// these tests exercise the SVG-specific guard rather than the general one.
const PAST_DEPTH_BOUND: usize = 200;

fn dot_config() -> ExtractionConfig {
    ExtractionConfig {
        output_format: OutputFormat::Custom("dot".to_string()),
        ..Default::default()
    }
}

/// `depth` nested `<g>` elements wrapping `leaf`, closed the same number of
/// times.
fn nested_svg(depth: usize, leaf: &str) -> String {
    let mut source =
        String::from(r##"<svg xmlns="http://www.w3.org/2000/svg" width="400" height="400" viewBox="0 0 400 400">"##);
    for _ in 0..depth {
        source.push_str("<g>");
    }
    source.push_str(leaf);
    for _ in 0..depth {
        source.push_str("</g>");
    }
    source.push_str("</svg>");
    source
}

#[test]
fn should_return_no_diagram_for_svg_nested_past_the_depth_bound() {
    let svg = nested_svg(PAST_DEPTH_BOUND, "");

    let result = extract_bytes_document_blocking(svg.as_bytes(), SVG_MIME, &dot_config())
        .expect("deep nesting alone must fail gracefully (no diagram), not error or panic");

    assert!(
        result.content.trim().is_empty(),
        "expected no recovered diagram for nesting past the bound, got: {}",
        result.content
    );
}

/// A closed shape at every level of a chain nested past the bound, plus a
/// connector: if the depth guard did not trip, this is shaped exactly like a
/// diagram.
#[test]
fn should_return_no_diagram_for_a_shape_flood_nested_past_the_depth_bound() {
    let mut leaf = String::new();
    for i in 0..PAST_DEPTH_BOUND {
        leaf.push_str(&format!(r#"<rect x="{i}" y="{i}" width="10" height="10"/>"#));
    }
    leaf.push_str(r##"<line x1="0" y1="0" x2="50" y2="50" stroke="#333"/>"##);
    let svg = nested_svg(PAST_DEPTH_BOUND, &leaf);

    let result = extract_bytes_document_blocking(svg.as_bytes(), SVG_MIME, &dot_config())
        .expect("a shape flood past the depth bound must fail gracefully, not error or panic");

    assert!(
        result.content.trim().is_empty(),
        "expected no recovered diagram (no partial graph) for a shape flood, got: {}",
        result.content
    );
}

/// A `<text>` label at every level of a chain nested past the bound, framed
/// by real shapes and a connector at shallow depth: proves the label pass's
/// own depth guard does not leak a partial graph either.
#[test]
fn should_return_no_diagram_for_a_label_flood_nested_past_the_depth_bound() {
    let mut leaf = String::from(r#"<rect x="10" y="10" width="10" height="10"/>"#);
    for i in 0..PAST_DEPTH_BOUND {
        leaf.push_str(&format!(r#"<text x="{i}" y="{i}">label {i}</text>"#));
    }
    leaf.push_str(r#"<rect x="200" y="200" width="10" height="10"/>"#);
    leaf.push_str(r##"<line x1="10" y1="10" x2="200" y2="200" stroke="#333"/>"##);
    let svg = nested_svg(PAST_DEPTH_BOUND, &leaf);

    let result = extract_bytes_document_blocking(svg.as_bytes(), SVG_MIME, &dot_config())
        .expect("a label flood past the depth bound must fail gracefully, not error or panic");

    assert!(
        result.content.trim().is_empty(),
        "expected no recovered diagram (no leaked labels) for a label flood, got: {}",
        result.content
    );
}

/// The bound must not be so tight that an everyday nested diagram (a
/// translated `<g>` wrapper, for instance) stops recovering.
#[test]
fn should_still_recover_a_diagram_nested_well_within_the_depth_bound() {
    let leaf = concat!(
        r##"<rect x="10" y="10" width="80" height="40" fill="#2c3e50"/>"##,
        r#"<text x="50" y="35" text-anchor="middle">Start</text>"#,
        r##"<rect x="10" y="150" width="80" height="40" fill="#27ae60"/>"##,
        r#"<text x="50" y="175" text-anchor="middle">End</text>"#,
        r##"<line x1="50" y1="50" x2="50" y2="150" stroke="#333"/>"##,
    );
    let svg = nested_svg(4, leaf);

    let result = extract_bytes_document_blocking(svg.as_bytes(), SVG_MIME, &dot_config()).expect("extraction");

    assert!(
        result.content.contains("n0 -> n1"),
        "expected a recovered graph well within the depth bound, got: {}",
        result.content
    );
}
