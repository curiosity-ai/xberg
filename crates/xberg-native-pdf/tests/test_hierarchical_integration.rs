//! Unit tests for the hierarchical `StructureElement`/`ContentElement`
//! data model (`crate::elements`) used to represent tagged-PDF structure.
//!
//! Three tests that exercised the (now-removed) writer/editor stack --
//! content-stream generation from a structure element and MCID allocation
//! via `writer::ContentStreamBuilder`, and resource registration via
//! `editor::ResourceManager` -- were removed; everything else here only
//! constructs and inspects the surviving read-side data types.

#[cfg(test)]
mod hierarchical_integration_tests {
    use xberg_native_pdf::elements::{ContentElement, StructureElement};
    use xberg_native_pdf::geometry::Rect;

    /// Test extracting hierarchical structure from a simple document.
    #[test]
    fn test_extract_hierarchical_content() {
        let structure = StructureElement {
            structure_type: "Document".to_string(),
            bbox: Rect::new(0.0, 0.0, 612.0, 792.0),
            children: vec![],
            reading_order: Some(0),
            alt_text: None,
            language: None,
        };

        assert_eq!(structure.structure_type, "Document");
        assert_eq!(structure.children.len(), 0);
        assert_eq!(structure.reading_order, Some(0));
    }

    /// Test creating a hierarchical structure with nested elements.
    #[test]
    fn test_nested_structure_creation() {
        let inner = StructureElement {
            structure_type: "Span".to_string(),
            bbox: Rect::new(100.0, 700.0, 50.0, 12.0),
            children: vec![],
            reading_order: None,
            alt_text: None,
            language: None,
        };

        let outer = StructureElement {
            structure_type: "P".to_string(),
            bbox: Rect::new(100.0, 700.0, 200.0, 50.0),
            children: vec![ContentElement::Structure(inner)],
            reading_order: Some(1),
            alt_text: None,
            language: None,
        };

        assert_eq!(outer.structure_type, "P");
        assert_eq!(outer.children.len(), 1);

        if let ContentElement::Structure(nested) = &outer.children[0] {
            assert_eq!(nested.structure_type, "Span");
        } else {
            panic!("Expected nested Structure element");
        }
    }

    /// Test structure with accessibility attributes.
    #[test]
    fn test_structure_with_accessibility() {
        let structure = StructureElement {
            structure_type: "H1".to_string(),
            bbox: Rect::new(72.0, 720.0, 300.0, 24.0),
            children: vec![],
            reading_order: Some(0),
            alt_text: Some("Main Heading".to_string()),
            language: Some("en".to_string()),
        };

        assert_eq!(structure.alt_text, Some("Main Heading".to_string()));
        assert_eq!(structure.language, Some("en".to_string()));
    }

    /// `StructureElement`'s own bbox fields round-trip through construction
    /// unchanged (despite the historical name, this never touched
    /// `DocumentEditor` -- see the module doc comment). ~keep
    #[test]
    fn test_editor_content_modification() {
        let structure = StructureElement {
            structure_type: "Document".to_string(),
            bbox: Rect::new(0.0, 0.0, 612.0, 792.0),
            children: vec![],
            reading_order: Some(0),
            alt_text: None,
            language: None,
        };

        assert_eq!(structure.structure_type, "Document");
        assert_eq!(structure.bbox.width, 612.0);
        assert_eq!(structure.bbox.height, 792.0);
    }

    /// Test multiple pages with different structures.
    #[test]
    fn test_multiple_pages_structures() {
        let page1_structure = StructureElement {
            structure_type: "Document".to_string(),
            bbox: Rect::new(0.0, 0.0, 612.0, 792.0),
            children: vec![],
            reading_order: Some(0),
            alt_text: None,
            language: None,
        };

        let page2_structure = StructureElement {
            structure_type: "Document".to_string(),
            bbox: Rect::new(0.0, 0.0, 612.0, 792.0),
            children: vec![],
            reading_order: Some(1),
            alt_text: None,
            language: None,
        };

        assert_eq!(page1_structure.reading_order, Some(0));
        assert_eq!(page2_structure.reading_order, Some(1));
    }

    /// Test structure type variants (PDF standard types).
    #[test]
    fn test_standard_structure_types() {
        let standard_types = vec![
            "Document", "Part", "Art", "Sect", "Div", "P", "H1", "H2", "H3", "H4", "H5", "H6", "List", "ListItem",
            "Label", "ListBody", "Table", "THead", "TBody", "TFoot", "TR", "TD", "TH", "Span", "Quote", "Code", "Link",
        ];

        for type_name in standard_types {
            let structure = StructureElement {
                structure_type: type_name.to_string(),
                bbox: Rect::new(0.0, 0.0, 100.0, 50.0),
                children: vec![],
                reading_order: None,
                alt_text: None,
                language: None,
            };

            assert_eq!(structure.structure_type, type_name);
        }
    }

    /// Test empty structure element.
    #[test]
    fn test_empty_structure() {
        let empty_structure = StructureElement {
            structure_type: "Div".to_string(),
            bbox: Rect::new(0.0, 0.0, 0.0, 0.0),
            children: vec![],
            reading_order: None,
            alt_text: None,
            language: None,
        };

        assert!(empty_structure.children.is_empty());
        assert_eq!(empty_structure.bbox.width, 0.0);
        assert_eq!(empty_structure.bbox.height, 0.0);
    }

    /// Test deep nesting of structure elements.
    #[test]
    fn test_deep_nesting() {
        let mut current = StructureElement {
            structure_type: "Span".to_string(),
            bbox: Rect::new(0.0, 0.0, 10.0, 10.0),
            children: vec![],
            reading_order: None,
            alt_text: None,
            language: None,
        };

        for level in 0..5 {
            let parent = StructureElement {
                structure_type: format!("Level{}", level),
                bbox: Rect::new(0.0, 0.0, 100.0 + (level as f32 * 10.0), 50.0),
                children: vec![ContentElement::Structure(current)],
                reading_order: Some(level),
                alt_text: None,
                language: None,
            };
            current = parent;
        }

        let mut depth = 0;
        let mut current_ref = &current;
        loop {
            depth += 1;
            if current_ref.children.is_empty() {
                break;
            }
            if let ContentElement::Structure(nested) = &current_ref.children[0] {
                current_ref = nested;
            } else {
                break;
            }
        }

        assert_eq!(depth, 6);
    }

    /// Test synthetic structure generation configuration.
    #[test]
    fn test_synthetic_structure_config() {
        use xberg_native_pdf::extractors::SyntheticStructureConfig;

        let config = SyntheticStructureConfig::default();

        assert_eq!(config.paragraph_gap_threshold, 4.0);
        assert_eq!(config.heading_size_multiplier, 1.3);
        assert_eq!(config.section_break_threshold, 50.0);

        let custom_config = SyntheticStructureConfig {
            paragraph_gap_threshold: 6.0,
            heading_size_multiplier: 1.5,
            section_break_threshold: 75.0,
        };

        assert_eq!(custom_config.paragraph_gap_threshold, 6.0);
        assert_eq!(custom_config.heading_size_multiplier, 1.5);
        assert_eq!(custom_config.section_break_threshold, 75.0);
    }

    /// Test structure type formatting.
    #[test]
    fn test_structure_type_formatting() {
        let types = vec![("Document", "Document"), ("H1", "H1"), ("P", "P"), ("Sect", "Sect")];

        for (input, expected) in types {
            let structure = StructureElement {
                structure_type: input.to_string(),
                bbox: Rect::new(0.0, 0.0, 100.0, 50.0),
                children: vec![],
                reading_order: None,
                alt_text: None,
                language: None,
            };

            assert_eq!(structure.structure_type, expected);
        }
    }
}
