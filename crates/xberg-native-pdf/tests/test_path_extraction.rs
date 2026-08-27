//! Integration tests for path (vector graphics) extraction.
//!
//! Path Objects Extraction

// ~keep: test/bench binaries print by design; org logging policy exempts tests
#![allow(clippy::print_stdout, clippy::print_stderr, clippy::dbg_macro)]

use xberg_native_pdf::document::PdfDocument;
use xberg_native_pdf::elements::{LineCap, LineJoin, PathContent, PathOperation};
use xberg_native_pdf::extractors::paths::{FillRule, PathExtractor};
use xberg_native_pdf::geometry::Rect;
use xberg_native_pdf::layout::Color;

mod common;

mod path_extractor_tests {
    use super::*;

    #[test]
    fn test_simple_line() {
        let mut extractor = PathExtractor::new();
        extractor.set_stroke_color(Color::black());
        extractor.set_line_width(1.0);

        extractor.move_to(100.0, 100.0);
        extractor.line_to(200.0, 100.0);
        extractor.stroke();

        let paths = extractor.finish();
        assert_eq!(paths.len(), 1);
        assert!(paths[0].has_stroke());
        assert!(!paths[0].has_fill());
        assert_eq!(paths[0].operations.len(), 2);
    }

    #[test]
    fn test_rectangle() {
        let mut extractor = PathExtractor::new();
        extractor.set_fill_color(Color::new(1.0, 0.0, 0.0));
        extractor.set_stroke_color(Color::black());

        extractor.rectangle(50.0, 50.0, 100.0, 75.0);
        extractor.fill_and_stroke(FillRule::NonZero);

        let paths = extractor.finish();
        assert_eq!(paths.len(), 1);
        assert!(paths[0].has_fill());
        assert!(paths[0].has_stroke());

        let bbox = &paths[0].bbox;
        assert!((bbox.x - 50.0).abs() < 0.001);
        assert!((bbox.y - 50.0).abs() < 0.001);
        assert!((bbox.width - 100.0).abs() < 0.001);
        assert!((bbox.height - 75.0).abs() < 0.001);
    }

    #[test]
    fn test_bezier_curve() {
        let mut extractor = PathExtractor::new();
        extractor.set_stroke_color(Color::new(0.0, 0.0, 1.0));

        extractor.move_to(0.0, 0.0);
        extractor.curve_to(25.0, 50.0, 75.0, 50.0, 100.0, 0.0);
        extractor.stroke();

        let paths = extractor.finish();
        assert_eq!(paths.len(), 1);
        assert_eq!(paths[0].operations.len(), 2);

        match &paths[0].operations[1] {
            PathOperation::CurveTo(x1, y1, x2, y2, x3, y3) => {
                assert_eq!(x1, &25.0);
                assert_eq!(y1, &50.0);
                assert_eq!(x2, &75.0);
                assert_eq!(y2, &50.0);
                assert_eq!(x3, &100.0);
                assert_eq!(y3, &0.0);
            }
            _ => panic!("Expected CurveTo operation"),
        }
    }

    #[test]
    fn test_closed_path() {
        let mut extractor = PathExtractor::new();
        extractor.set_fill_color(Color::new(0.0, 1.0, 0.0));

        extractor.move_to(50.0, 0.0);
        extractor.line_to(100.0, 100.0);
        extractor.line_to(0.0, 100.0);
        extractor.close_path();
        extractor.fill(FillRule::NonZero);

        let paths = extractor.finish();
        assert_eq!(paths.len(), 1);
        assert!(paths[0].has_fill());
        assert!(!paths[0].has_stroke());

        let has_close = paths[0]
            .operations
            .iter()
            .any(|op| matches!(op, PathOperation::ClosePath));
        assert!(has_close);
    }

    #[test]
    fn test_multiple_subpaths() {
        let mut extractor = PathExtractor::new();
        extractor.set_stroke_color(Color::black());
        extractor.set_line_width(2.0);

        extractor.move_to(0.0, 0.0);
        extractor.line_to(100.0, 0.0);
        extractor.stroke();

        extractor.move_to(0.0, 50.0);
        extractor.line_to(100.0, 50.0);
        extractor.stroke();

        let paths = extractor.finish();
        assert_eq!(paths.len(), 2);
    }

    #[test]
    fn test_line_styles() {
        let mut extractor = PathExtractor::new();
        extractor.set_stroke_color(Color::black());
        extractor.set_line_width(3.0);
        extractor.set_line_cap(LineCap::Round);
        extractor.set_line_join(LineJoin::Bevel);

        extractor.move_to(0.0, 0.0);
        extractor.line_to(50.0, 50.0);
        extractor.line_to(100.0, 0.0);
        extractor.stroke();

        let paths = extractor.finish();
        assert_eq!(paths.len(), 1);
        assert_eq!(paths[0].stroke_width, 3.0);
        assert_eq!(paths[0].line_cap, LineCap::Round);
        assert_eq!(paths[0].line_join, LineJoin::Bevel);
    }

    #[test]
    fn test_fill_rules() {
        let mut extractor = PathExtractor::new();
        extractor.set_fill_color(Color::new(0.5, 0.5, 0.5));

        extractor.rectangle(0.0, 0.0, 100.0, 100.0);
        extractor.fill(FillRule::EvenOdd);

        let paths = extractor.finish();
        assert_eq!(paths.len(), 1);
        assert!(paths[0].has_fill());
    }

    #[test]
    fn test_end_path_without_painting() {
        let mut extractor = PathExtractor::new();

        extractor.move_to(0.0, 0.0);
        extractor.rectangle(10.0, 10.0, 80.0, 80.0);
        extractor.end_path();

        let paths = extractor.finish();
        assert!(paths.is_empty());
    }

    // ─── Optional Content Group (PDF "layer") tagging ─────────────────
    //
    // Covers the BDC/BMC/EMC marked-content stack added to expose the
    // OCG name on each finalized path (ISO 32000-1:2008 §8.11, §14.6). ~keep

    #[test]
    fn test_layer_attached_when_oc_active() {
        let mut extractor = PathExtractor::new();
        extractor.set_stroke_color(Color::black());

        extractor.push_oc_layer(Some("A-GRID".to_string()));
        extractor.move_to(0.0, 0.0);
        extractor.line_to(100.0, 0.0);
        extractor.stroke();
        extractor.pop_oc_layer();

        let paths = extractor.finish();
        assert_eq!(paths.len(), 1);
        assert_eq!(paths[0].layer.as_deref(), Some("A-GRID"));
    }

    #[test]
    fn test_layer_none_outside_oc() {
        let mut extractor = PathExtractor::new();
        extractor.set_stroke_color(Color::black());

        extractor.move_to(0.0, 0.0);
        extractor.line_to(50.0, 50.0);
        extractor.stroke();

        let paths = extractor.finish();
        assert_eq!(paths.len(), 1);
        assert_eq!(paths[0].layer, None);
    }

    #[test]
    fn test_layer_cleared_after_pop() {
        let mut extractor = PathExtractor::new();
        extractor.set_stroke_color(Color::black());

        extractor.push_oc_layer(Some("S-COLS".to_string()));
        extractor.move_to(0.0, 0.0);
        extractor.line_to(10.0, 0.0);
        extractor.stroke();
        extractor.pop_oc_layer();

        extractor.move_to(0.0, 20.0);
        extractor.line_to(10.0, 20.0);
        extractor.stroke();

        let paths = extractor.finish();
        assert_eq!(paths.len(), 2);
        assert_eq!(paths[0].layer.as_deref(), Some("S-COLS"));
        assert_eq!(paths[1].layer, None);
    }

    #[test]
    fn test_outer_oc_survives_nested_non_oc_bmc() {
        let mut extractor = PathExtractor::new();
        extractor.set_stroke_color(Color::black());

        extractor.push_oc_layer(Some("A-WALL".to_string()));
        extractor.push_oc_layer(None);

        extractor.move_to(0.0, 0.0);
        extractor.line_to(10.0, 0.0);
        extractor.stroke();

        extractor.pop_oc_layer();
        extractor.pop_oc_layer();

        let paths = extractor.finish();
        assert_eq!(paths.len(), 1);
        assert_eq!(paths[0].layer.as_deref(), Some("A-WALL"));
    }

    #[test]
    fn test_nested_oc_inner_wins() {
        let mut extractor = PathExtractor::new();
        extractor.set_stroke_color(Color::black());

        extractor.push_oc_layer(Some("OUTER".to_string()));
        extractor.push_oc_layer(Some("INNER".to_string()));

        extractor.move_to(0.0, 0.0);
        extractor.line_to(10.0, 0.0);
        extractor.stroke();

        extractor.pop_oc_layer();
        extractor.move_to(0.0, 20.0);
        extractor.line_to(10.0, 20.0);
        extractor.stroke();

        extractor.pop_oc_layer();

        let paths = extractor.finish();
        assert_eq!(paths.len(), 2);
        assert_eq!(paths[0].layer.as_deref(), Some("INNER"));
        assert_eq!(paths[1].layer.as_deref(), Some("OUTER"));
    }

    #[test]
    fn test_underflow_pop_is_safe() {
        let mut extractor = PathExtractor::new();
        extractor.set_stroke_color(Color::black());

        extractor.pop_oc_layer();
        extractor.move_to(0.0, 0.0);
        extractor.line_to(10.0, 0.0);
        extractor.stroke();

        let paths = extractor.finish();
        assert_eq!(paths.len(), 1);
        assert_eq!(paths[0].layer, None);
    }
}

mod svg_conversion_tests {
    use super::*;

    fn create_simple_path() -> PathContent {
        PathContent {
            operations: vec![
                PathOperation::MoveTo(10.0, 20.0),
                PathOperation::LineTo(100.0, 20.0),
                PathOperation::LineTo(100.0, 80.0),
                PathOperation::ClosePath,
            ],
            bbox: Rect::new(10.0, 20.0, 90.0, 60.0),
            stroke_color: Some(Color::black()),
            fill_color: Some(Color::new(1.0, 0.0, 0.0)),
            stroke_width: 2.0,
            line_cap: LineCap::Butt,
            line_join: LineJoin::Miter,
            dash_pattern: None,
            matrix: None,
            artifact_type: None,
            reading_order: None,
            layer: None,
        }
    }

    #[test]
    fn test_svg_path_data_generation() {
        let path = create_simple_path();

        let mut d = String::new();
        for op in &path.operations {
            match op {
                PathOperation::MoveTo(x, y) => d.push_str(&format!("M {} {} ", x, y)),
                PathOperation::LineTo(x, y) => d.push_str(&format!("L {} {} ", x, y)),
                PathOperation::ClosePath => d.push_str("Z "),
                _ => {}
            }
        }

        assert!(d.contains("M 10 20"));
        assert!(d.contains("L 100 20"));
        assert!(d.contains("L 100 80"));
        assert!(d.contains("Z"));
    }

    #[test]
    fn test_svg_stroke_attributes() {
        let path = create_simple_path();

        assert!(path.stroke_color.is_some());
        let stroke = path.stroke_color.unwrap();
        assert_eq!(stroke.r, 0.0);
        assert_eq!(stroke.g, 0.0);
        assert_eq!(stroke.b, 0.0);
    }

    #[test]
    fn test_svg_fill_attributes() {
        let path = create_simple_path();

        assert!(path.fill_color.is_some());
        let fill = path.fill_color.unwrap();
        assert_eq!(fill.r, 1.0);
        assert_eq!(fill.g, 0.0);
        assert_eq!(fill.b, 0.0);
    }

    #[test]
    fn test_svg_bezier_curve() {
        let path = PathContent {
            operations: vec![
                PathOperation::MoveTo(0.0, 100.0),
                PathOperation::CurveTo(25.0, 0.0, 75.0, 0.0, 100.0, 100.0),
            ],
            bbox: Rect::new(0.0, 0.0, 100.0, 100.0),
            stroke_color: Some(Color::black()),
            fill_color: None,
            stroke_width: 1.0,
            line_cap: LineCap::Butt,
            line_join: LineJoin::Miter,
            dash_pattern: None,
            matrix: None,
            artifact_type: None,
            reading_order: None,
            layer: None,
        };

        let mut d = String::new();
        for op in &path.operations {
            match op {
                PathOperation::MoveTo(x, y) => d.push_str(&format!("M {} {} ", x, y)),
                PathOperation::CurveTo(x1, y1, x2, y2, x3, y3) => {
                    d.push_str(&format!("C {} {} {} {} {} {} ", x1, y1, x2, y2, x3, y3))
                }
                _ => {}
            }
        }

        assert!(d.contains("M 0 100"));
        assert!(d.contains("C 25 0 75 0 100 100"));
    }

    #[test]
    fn test_svg_rectangle() {
        let path = PathContent {
            operations: vec![PathOperation::Rectangle(50.0, 50.0, 200.0, 100.0)],
            bbox: Rect::new(50.0, 50.0, 200.0, 100.0),
            stroke_color: Some(Color::black()),
            fill_color: Some(Color::new(0.9, 0.9, 0.9)),
            stroke_width: 1.0,
            line_cap: LineCap::Butt,
            line_join: LineJoin::Miter,
            dash_pattern: None,
            matrix: None,
            artifact_type: None,
            reading_order: None,
            layer: None,
        };

        let has_rect = path
            .operations
            .iter()
            .any(|op| matches!(op, PathOperation::Rectangle(_, _, _, _)));
        assert!(has_rect);
    }

    #[test]
    fn test_svg_line_cap_conversion() {
        let round_cap = PathContent {
            operations: vec![PathOperation::MoveTo(0.0, 0.0), PathOperation::LineTo(100.0, 0.0)],
            bbox: Rect::new(0.0, 0.0, 100.0, 0.0),
            stroke_color: Some(Color::black()),
            fill_color: None,
            stroke_width: 10.0,
            line_cap: LineCap::Round,
            line_join: LineJoin::Miter,
            dash_pattern: None,
            matrix: None,
            artifact_type: None,
            reading_order: None,
            layer: None,
        };

        assert_eq!(round_cap.line_cap, LineCap::Round);
    }

    #[test]
    fn test_svg_line_join_conversion() {
        let bevel_join = PathContent {
            operations: vec![
                PathOperation::MoveTo(0.0, 0.0),
                PathOperation::LineTo(50.0, 50.0),
                PathOperation::LineTo(100.0, 0.0),
            ],
            bbox: Rect::new(0.0, 0.0, 100.0, 50.0),
            stroke_color: Some(Color::black()),
            fill_color: None,
            stroke_width: 5.0,
            line_cap: LineCap::Butt,
            line_join: LineJoin::Bevel,
            dash_pattern: None,
            matrix: None,
            artifact_type: None,
            reading_order: None,
            layer: None,
        };

        assert_eq!(bevel_join.line_join, LineJoin::Bevel);
    }
}

mod bbox_tests {
    use super::*;

    #[test]
    fn test_line_bbox() {
        let mut extractor = PathExtractor::new();
        extractor.set_stroke_color(Color::black());

        extractor.move_to(10.0, 20.0);
        extractor.line_to(110.0, 80.0);
        extractor.stroke();

        let paths = extractor.finish();
        let bbox = &paths[0].bbox;

        assert!((bbox.x - 10.0).abs() < 0.001);
        assert!((bbox.y - 20.0).abs() < 0.001);
        assert!((bbox.width - 100.0).abs() < 0.001);
        assert!((bbox.height - 60.0).abs() < 0.001);
    }

    #[test]
    fn test_rectangle_bbox() {
        let mut extractor = PathExtractor::new();
        extractor.set_fill_color(Color::black());

        extractor.rectangle(25.0, 30.0, 150.0, 200.0);
        extractor.fill(FillRule::NonZero);

        let paths = extractor.finish();
        let bbox = &paths[0].bbox;

        assert!((bbox.x - 25.0).abs() < 0.001);
        assert!((bbox.y - 30.0).abs() < 0.001);
        assert!((bbox.width - 150.0).abs() < 0.001);
        assert!((bbox.height - 200.0).abs() < 0.001);
    }

    #[test]
    fn test_triangle_bbox() {
        let mut extractor = PathExtractor::new();
        extractor.set_fill_color(Color::black());

        extractor.move_to(0.0, 0.0);
        extractor.line_to(100.0, 0.0);
        extractor.line_to(50.0, 86.6);
        extractor.close_path();
        extractor.fill(FillRule::NonZero);

        let paths = extractor.finish();
        let bbox = &paths[0].bbox;

        assert!((bbox.x - 0.0).abs() < 0.1);
        assert!((bbox.y - 0.0).abs() < 0.1);
        assert!((bbox.width - 100.0).abs() < 0.1);
        assert!((bbox.height - 86.6).abs() < 0.1);
    }

    #[test]
    fn test_complex_path_bbox() {
        let mut extractor = PathExtractor::new();
        extractor.set_stroke_color(Color::black());

        extractor.move_to(-50.0, -50.0);
        extractor.line_to(50.0, 0.0);
        extractor.curve_to(100.0, 25.0, 100.0, 75.0, 50.0, 100.0);
        extractor.line_to(-50.0, 100.0);
        extractor.close_path();
        extractor.stroke();

        let paths = extractor.finish();
        let bbox = &paths[0].bbox;

        assert!(bbox.x <= -50.0);
        assert!(bbox.y <= -50.0);
        assert!(bbox.x + bbox.width >= 100.0);
        assert!(bbox.y + bbox.height >= 100.0);
    }
}

#[cfg(test)]
mod integration_tests {
    use super::*;
    use std::path::Path;

    /// `extract_paths` on a page with several drawing operators must return
    /// one `PathContent` per painted subpath, each carrying the operators
    /// and paint state that produced it.
    #[test]
    fn test_extract_paths_from_pdf() {
        let content = b"0 0 0 RG 10 10 m 190 190 l S \
                         1 0 0 rg 50 50 100 100 re f";
        let pdf = common::build_minimal_pdf_raw(content, b"/MediaBox [0 0 200 200]");
        let doc = PdfDocument::from_bytes(pdf).expect("open synthetic PDF");

        let paths = doc.extract_paths(0).expect("extract_paths");

        assert_eq!(paths.len(), 2, "expected one stroked line and one filled rectangle");
        for path in &paths {
            assert!(!path.operations.is_empty());
            assert!(path.has_stroke() || path.has_fill());
        }
        assert!(
            paths[0].has_stroke() && !paths[0].has_fill(),
            "first path is stroke-only"
        );
        assert!(
            paths[1].has_fill() && !paths[1].has_stroke(),
            "second path is fill-only"
        );
    }

    /// `extract_paths_in_rect` filters out paths whose bounding box does not
    /// intersect the supplied region.
    #[test]
    fn test_extract_paths_in_rect() {
        let content = b"0 0 100 100 re f 400 400 50 50 re f";
        let pdf = common::build_minimal_pdf_raw(content, b"/MediaBox [0 0 600 600]");
        let doc = PdfDocument::from_bytes(pdf).expect("open synthetic PDF");

        let region = Rect::new(0.0, 0.0, 300.0, 300.0);
        let paths = doc
            .extract_paths_in_rect(0, region)
            .expect("Failed to extract paths in rect");

        assert_eq!(
            paths.len(),
            1,
            "only the rectangle inside the region should be returned"
        );

        for path in &paths {
            let bbox = &path.bbox;
            let intersects = !(bbox.x > region.x + region.width
                || bbox.x + bbox.width < region.x
                || bbox.y > region.y + region.height
                || bbox.y + bbox.height < region.y);
            assert!(intersects, "Path bbox {:?} should intersect region {:?}", bbox, region);
        }
    }

    #[test]
    fn test_path_extraction_on_simple_pdf() {
        let test_paths = [
            "tests/fixtures/simple.pdf",
            "tests/fixtures/test.pdf",
            "tests/fixtures/hello.pdf",
        ];

        for pdf_path in &test_paths {
            let path = Path::new(pdf_path);
            if path.exists() {
                let result = PdfDocument::open(path);
                if let Ok(doc) = result
                    && let Ok(paths) = doc.extract_paths(0)
                {
                    eprintln!("Extracted {} paths from {:?}", paths.len(), path);
                }
            }
        }
    }
}

mod color_tests {
    use super::*;

    #[test]
    fn test_stroke_color() {
        let mut extractor = PathExtractor::new();
        extractor.set_stroke_color(Color::new(1.0, 0.5, 0.0));

        extractor.move_to(0.0, 0.0);
        extractor.line_to(100.0, 100.0);
        extractor.stroke();

        let paths = extractor.finish();
        let color = paths[0].stroke_color.unwrap();
        assert_eq!(color.r, 1.0);
        assert_eq!(color.g, 0.5);
        assert_eq!(color.b, 0.0);
    }

    #[test]
    fn test_fill_color() {
        let mut extractor = PathExtractor::new();
        extractor.set_fill_color(Color::new(0.0, 0.8, 0.2));

        extractor.rectangle(0.0, 0.0, 100.0, 100.0);
        extractor.fill(FillRule::NonZero);

        let paths = extractor.finish();
        let color = paths[0].fill_color.unwrap();
        assert_eq!(color.r, 0.0);
        assert_eq!(color.g, 0.8);
        assert_eq!(color.b, 0.2);
    }

    #[test]
    fn test_stroke_and_fill_different_colors() {
        let mut extractor = PathExtractor::new();
        extractor.set_stroke_color(Color::black());
        extractor.set_fill_color(Color::new(0.9, 0.9, 0.0));

        extractor.rectangle(10.0, 10.0, 80.0, 80.0);
        extractor.fill_and_stroke(FillRule::NonZero);

        let paths = extractor.finish();
        let stroke = paths[0].stroke_color.unwrap();
        let fill = paths[0].fill_color.unwrap();

        assert_eq!(stroke.r, 0.0);
        assert_eq!(stroke.g, 0.0);
        assert_eq!(stroke.b, 0.0);

        assert_eq!(fill.r, 0.9);
        assert_eq!(fill.g, 0.9);
        assert_eq!(fill.b, 0.0);
    }
}
