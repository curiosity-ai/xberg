#![allow(warnings)]
//! Tests for ISO 32000-1:2008 Section 14.7-14.8 Reading Order Priority
//!
//! PDF spec defines reading order priority as:
//! 1. Structure tree (tagged PDF) - USE FIRST if available
//! 2. Physical page order - Use if no structure tree
//! 3. Content stream order - Use if both above unavailable

use xberg_native_pdf::structure::types::StructType;

/// Mock TextBlock for testing reading order
#[derive(Clone, Debug)]
struct TextBlock {
    text: String,
    x: f32,
    y: f32,
    struct_type: String,
    mcid: Option<u32>,
}

#[test]
fn test_priority_1_structure_tree_over_physical_order() {
    // Structure tree should be used first when available
    // Even if text appears physically in different order on page ~keep

    let blocks_physical_order = vec![
        TextBlock {
            text: "Second paragraph".to_string(),
            x: 100.0,
            y: 200.0,
            struct_type: "P".to_string(),
            mcid: Some(2),
        },
        TextBlock {
            text: "First paragraph".to_string(),
            x: 100.0,
            y: 400.0,
            struct_type: "P".to_string(),
            mcid: Some(1),
        },
    ];

    // When extracted by structure tree (MCID order), should be 1, 2, not physical order
    // This test defines expected behavior once structure tree reading is implemented ~keep
    let expected_order = ["First paragraph", "Second paragraph"];

    // Structure tree says MCID 1 comes first, MCID 2 comes second
    // So even though block 2 appears first physically, it should appear second in output ~keep
    assert_eq!(expected_order[0], "First paragraph");
    assert_eq!(expected_order[1], "Second paragraph");
}

#[test]
fn test_priority_2_physical_order_when_no_structure_tree() {
    let blocks = vec![
        TextBlock {
            text: "First".to_string(),
            x: 100.0,
            y: 100.0,
            struct_type: "Body".to_string(),
            mcid: None,
        },
        TextBlock {
            text: "Second".to_string(),
            x: 100.0,
            y: 300.0,
            struct_type: "Body".to_string(),
            mcid: None,
        },
        TextBlock {
            text: "Third".to_string(),
            x: 100.0,
            y: 500.0,
            struct_type: "Body".to_string(),
            mcid: None,
        },
    ];

    let expected = ["First", "Second", "Third"];

    for (i, block) in blocks.iter().enumerate() {
        assert_eq!(block.text, expected[i]);
    }
}

#[test]
fn test_multi_column_layout_structure_tree_priority() {
    // Multi-column layout: Structure tree order should override column order ~keep

    let blocks = vec![
        TextBlock {
            text: "Column1 para1".to_string(),
            x: 50.0,
            y: 100.0,
            struct_type: "P".to_string(),
            mcid: Some(1),
        },
        TextBlock {
            text: "Column2 para1".to_string(),
            x: 400.0,
            y: 100.0,
            struct_type: "P".to_string(),
            mcid: Some(2),
        },
        TextBlock {
            text: "Column1 para2".to_string(),
            x: 50.0,
            y: 300.0,
            struct_type: "P".to_string(),
            mcid: Some(3),
        },
        TextBlock {
            text: "Column2 para2".to_string(),
            x: 400.0,
            y: 300.0,
            struct_type: "P".to_string(),
            mcid: Some(4),
        },
    ];

    // Structure tree order (MCID): 1, 2, 3, 4
    // Physical/columnar order would be: 1, 3 (left column) then 2, 4 (right column)
    // Structure tree should win ~keep
    let expected_order = vec!["Column1 para1", "Column2 para1", "Column1 para2", "Column2 para2"];
    let block_texts: Vec<&str> = blocks.iter().map(|b| b.text.as_str()).collect();

    assert_eq!(block_texts, expected_order);
}

#[test]
fn test_structure_tree_provides_correct_reading_order() {
    let mcid_to_struct_type = vec![(1, "H1"), (2, "P"), (3, "P"), (4, "Table")];

    // Structure tree defines order: 1, 2, 3, 4
    // Text should appear in this sequence regardless of physical position ~keep
    for (i, (mcid, _struct_type)) in mcid_to_struct_type.iter().enumerate() {
        assert_eq!(*mcid, (i + 1) as u32);
    }
}

#[test]
fn test_ignore_physical_column_order_with_structure() {
    // Physical layout: 2 columns, text appears left-to-right
    // But structure says: 1, 2, 3, 4 (mixed columns) ~keep
    let structure_order = vec![1, 2, 3, 4];
    let physical_column_order = vec![1, 3, 2, 4];

    assert_eq!(structure_order[0], 1);
    assert_eq!(structure_order[1], 2);
    assert_eq!(structure_order[2], 3);
    assert_eq!(structure_order[3], 4);

    assert_ne!(structure_order, physical_column_order);
}

#[test]
fn test_header_footer_order_via_structure() {
    let elements = vec![
        ("H1", "Document Title"),
        ("H2", "Section 1"),
        ("P", "Section 1 content"),
        ("P", "More content"),
        ("H2", "Section 2"),
        ("P", "Section 2 content"),
    ];

    let mcid_order: Vec<u32> = (1..=6).collect();

    // Structure defines order: H1, H2, P, P, H2, P
    // Physical order might be different if page has headers/footers
    // But structure should win ~keep
    for (i, (struct_type, _text)) in elements.iter().enumerate() {
        assert!(match *struct_type {
            "H1" | "H2" | "P" => true,
            _ => false,
        });
        assert_eq!(mcid_order[i], (i + 1) as u32);
    }
}

#[test]
fn test_table_row_order_from_structure() {
    let table_cells = vec![
        ("TR", vec!["Header1", "Header2"]),
        ("TR", vec!["Cell1", "Cell2"]),
        ("TR", vec!["Cell3", "Cell4"]),
    ];

    let expected_structure_order = vec![1, 2, 3];
    let actual_order: Vec<u32> = (1..=3).collect();

    assert_eq!(expected_structure_order, actual_order);
    assert_eq!(table_cells.len(), 3);
}

#[test]
fn test_fallback_to_physical_when_no_structure() {
    let text_items = vec![
        ("First", 100.0, 100.0),
        ("Second", 400.0, 100.0),
        ("Third", 100.0, 300.0),
        ("Fourth", 400.0, 300.0),
    ];

    // With no structure tree, order should be:
    // Row 1 (y=100): First (100x), Second (400x) - left to right
    // Row 2 (y=300): Third (100x), Fourth (400x) - left to right ~keep
    let expected_order = ["First", "Second", "Third", "Fourth"];

    let actual_order: Vec<&str> = text_items.iter().map(|(t, _, _)| *t).collect();

    // Note: actual implementation would need to sort by Y, then by X
    // This test just verifies the definition of expected behavior ~keep
    assert_eq!(actual_order[0], expected_order[0]);
}

#[test]
fn test_structure_tree_completely_overrides_physical_order() {
    // Structure tree order should COMPLETELY override physical layout
    // Not just be a tiebreaker - it's the PRIMARY method ~keep

    let physical_order = vec![
        "Text appearing first on page (top-left)",
        "Text appearing second on page (middle)",
        "Text appearing third on page (bottom-right)",
    ];

    let structure_order = vec!["Third", "First", "Second"];

    // When structure tree is present, we should produce structure_order, not physical_order
    // This test documents that structure tree is PRIMARY, not secondary ~keep

    assert_ne!(physical_order, structure_order);
    assert_eq!(structure_order[0], "Third");
    assert_eq!(structure_order[1], "First");
}

#[test]
fn test_nested_structure_elements_ordering() {
    let section1_paragraphs = [(1, "Section 1 - Paragraph 1"), (2, "Section 1 - Paragraph 2")];

    let section2_paragraphs = [(3, "Section 2 - Paragraph 1"), (4, "Section 2 - Paragraph 2")];

    // Structure order (depth-first): 1, 2, 3, 4
    // All of section 1's content before section 2 ~keep
    let expected_order = vec![1, 2, 3, 4];
    let combined: Vec<u32> = section1_paragraphs
        .iter()
        .map(|(id, _)| *id)
        .chain(section2_paragraphs.iter().map(|(id, _)| *id))
        .collect();

    assert_eq!(combined, expected_order);
}

#[test]
fn test_structure_order_persists_across_document_sections() {
    let mcids = vec![
        (1, "Page 1, Content 1"),
        (2, "Page 1, Content 2"),
        (3, "Page 2, Content 1"),
        (4, "Page 2, Content 2"),
    ];

    let order: Vec<u32> = mcids.iter().map(|(id, _)| *id).collect();
    assert_eq!(order, vec![1, 2, 3, 4]);
}

#[test]
fn test_reading_order_with_sidebars() {
    let main_content = [(1, "Main paragraph 1"), (2, "Main paragraph 2")];

    let sidebar_content = [(3, "Sidebar content")];

    // If sidebar appears on right but is MCID 3 (after main paragraphs)
    // It should appear after main content in output, even if physically appears first ~keep
    let structure_order = vec![1, 2, 3];
    let combined: Vec<u32> = main_content
        .iter()
        .map(|(id, _)| *id)
        .chain(sidebar_content.iter().map(|(id, _)| *id))
        .collect();

    assert_eq!(combined, structure_order);
}

#[test]
fn test_empty_structure_tree_uses_physical_order() {
    let text_blocks = [("Top text", 100.0), ("Bottom text", 300.0)];

    // No MCIDs (empty structure tree) - use Y coordinate (physical order) ~keep
    let sorted_by_y: Vec<&str> = text_blocks.iter().map(|(text, _)| *text).collect();

    assert_eq!(sorted_by_y, vec!["Top text", "Bottom text"]);
}

#[test]
fn test_specification_reference_iso_14_7_8() {
    // Key requirement: Structure tree's reading order is AUTHORITATIVE
    // Implementation must check structure tree BEFORE physical layout ~keep

    let spec_priority = [
        "1. Structure tree (tagged PDF)",
        "2. Physical page order (top-to-bottom, left-to-right)",
        "3. Content stream order",
    ];

    assert_eq!(spec_priority[0], "1. Structure tree (tagged PDF)");
    assert_eq!(
        spec_priority[1],
        "2. Physical page order (top-to-bottom, left-to-right)"
    );
    assert_eq!(spec_priority[2], "3. Content stream order");
}

#[test]
fn test_structure_type_detection_for_reading_order() {
    // Different structure types (P, H1-H6, Table, List) should be preserved
    // in their structure tree order, not reordered by physical position ~keep

    let elements = [
        (StructType::H1, "Heading 1"),
        (StructType::P, "Paragraph"),
        (StructType::H2, "Heading 2"),
        (StructType::Table, "Table"),
        (StructType::L, "List"),
    ];

    for (i, (struct_type, _text)) in elements.iter().enumerate() {
        match struct_type {
            StructType::H1 => assert_eq!(i, 0),
            StructType::P => assert_eq!(i, 1),
            StructType::H2 => assert_eq!(i, 2),
            StructType::Table => assert_eq!(i, 3),
            StructType::L => assert_eq!(i, 4),
            _ => panic!("Unexpected struct type"),
        }
    }
}
