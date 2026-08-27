//! Phase 2 TDD tests for config adapter
//!
//! Tests that verify TextPipelineConfig can be created from legacy ConversionOptions
//! for backwards compatibility.

#[test]
fn test_conversion_options_to_pipeline_config_basic() {
    use xberg_native_pdf::converters::{ConversionOptions, ReadingOrderMode};
    use xberg_native_pdf::pipeline::config::{ReadingOrderStrategyType, TextPipelineConfig};

    let options = ConversionOptions {
        reading_order_mode: ReadingOrderMode::ColumnAware,
        detect_headings: true,
        include_images: true,
        ..Default::default()
    };

    let config = TextPipelineConfig::from_conversion_options(&options);

    assert_eq!(config.reading_order.strategy, ReadingOrderStrategyType::XYCut);

    assert!(config.output.detect_headings);
    assert!(config.output.include_images);
}

#[test]
fn test_reading_order_mode_mapping_simple() {
    use xberg_native_pdf::converters::{ConversionOptions, ReadingOrderMode};
    use xberg_native_pdf::pipeline::config::{ReadingOrderStrategyType, TextPipelineConfig};

    let options = ConversionOptions {
        reading_order_mode: ReadingOrderMode::TopToBottomLeftToRight,
        ..Default::default()
    };

    let config = TextPipelineConfig::from_conversion_options(&options);

    assert_eq!(config.reading_order.strategy, ReadingOrderStrategyType::Simple);
}

#[test]
fn test_reading_order_mode_mapping_xycut() {
    use xberg_native_pdf::converters::{ConversionOptions, ReadingOrderMode};
    use xberg_native_pdf::pipeline::config::{ReadingOrderStrategyType, TextPipelineConfig};

    let options = ConversionOptions {
        reading_order_mode: ReadingOrderMode::ColumnAware,
        ..Default::default()
    };

    let config = TextPipelineConfig::from_conversion_options(&options);

    assert_eq!(config.reading_order.strategy, ReadingOrderStrategyType::XYCut);
}

#[test]
fn test_reading_order_mode_mapping_structure_tree() {
    use xberg_native_pdf::converters::{ConversionOptions, ReadingOrderMode};
    use xberg_native_pdf::pipeline::config::{ReadingOrderStrategyType, TextPipelineConfig};

    let options = ConversionOptions {
        reading_order_mode: ReadingOrderMode::StructureTreeFirst {
            mcid_order: vec![0, 1, 2],
        },
        ..Default::default()
    };

    let config = TextPipelineConfig::from_conversion_options(&options);

    assert_eq!(
        config.reading_order.strategy,
        ReadingOrderStrategyType::StructureTreeFirst
    );
}

#[test]
fn test_config_adapter_all_fields() {
    use xberg_native_pdf::converters::{BoldMarkerBehavior as OldBMB, ConversionOptions, ReadingOrderMode};
    use xberg_native_pdf::pipeline::config::{BoldMarkerBehavior, TextPipelineConfig};

    let options = ConversionOptions {
        reading_order_mode: ReadingOrderMode::ColumnAware,
        detect_headings: true,
        include_images: false,
        bold_marker_behavior: OldBMB::Aggressive,
        ..Default::default()
    };

    let config = TextPipelineConfig::from_conversion_options(&options);

    assert!(config.output.detect_headings);
    assert!(!config.output.include_images);
    assert_eq!(config.output.bold_marker_behavior, BoldMarkerBehavior::Aggressive);
}

#[test]
fn test_config_adapter_preserves_layout() {
    use xberg_native_pdf::converters::ConversionOptions;
    use xberg_native_pdf::pipeline::config::TextPipelineConfig;

    let options = ConversionOptions {
        preserve_layout: true,
        ..Default::default()
    };

    let config = TextPipelineConfig::from_conversion_options(&options);

    assert!(config.output.preserve_layout);
}

#[test]
fn test_config_adapter_extract_tables() {
    use xberg_native_pdf::converters::ConversionOptions;
    use xberg_native_pdf::pipeline::config::TextPipelineConfig;

    let options = ConversionOptions {
        extract_tables: true,
        ..Default::default()
    };

    let config = TextPipelineConfig::from_conversion_options(&options);

    assert!(config.output.extract_tables);
}

#[test]
fn test_config_adapter_image_output_dir() {
    use xberg_native_pdf::converters::ConversionOptions;
    use xberg_native_pdf::pipeline::config::TextPipelineConfig;

    let options = ConversionOptions {
        image_output_dir: Some("/tmp/images".to_string()),
        ..Default::default()
    };

    let config = TextPipelineConfig::from_conversion_options(&options);

    assert_eq!(config.output.image_output_dir, Some("/tmp/images".to_string()));
}

#[test]
fn test_config_adapter_defaults() {
    use xberg_native_pdf::converters::ConversionOptions;
    use xberg_native_pdf::pipeline::config::TextPipelineConfig;

    let options = ConversionOptions::default();

    let config = TextPipelineConfig::from_conversion_options(&options);

    assert!(config.output.detect_headings);
    assert!(!config.output.include_images);
    assert!(!config.output.preserve_layout);
    assert!(config.output.extract_tables);
}

#[test]
fn test_config_adapter_all_options_combined() {
    use xberg_native_pdf::converters::{BoldMarkerBehavior as OldBMB, ConversionOptions, ReadingOrderMode};
    use xberg_native_pdf::pipeline::config::{BoldMarkerBehavior, ReadingOrderStrategyType, TextPipelineConfig};

    let options = ConversionOptions {
        reading_order_mode: ReadingOrderMode::StructureTreeFirst {
            mcid_order: vec![0, 1, 2],
        },
        detect_headings: true,
        include_images: false,
        bold_marker_behavior: OldBMB::Conservative,
        preserve_layout: true,
        extract_tables: true,
        image_output_dir: Some("/output/images".to_string()),
        table_detection_config: None,
        ..Default::default()
    };

    let config = TextPipelineConfig::from_conversion_options(&options);

    assert_eq!(
        config.reading_order.strategy,
        ReadingOrderStrategyType::StructureTreeFirst
    );
    assert!(config.output.detect_headings);
    assert!(!config.output.include_images);
    assert_eq!(config.output.bold_marker_behavior, BoldMarkerBehavior::Conservative);
    assert!(config.output.preserve_layout);
    assert!(config.output.extract_tables);
    assert_eq!(config.output.image_output_dir, Some("/output/images".to_string()));
}
