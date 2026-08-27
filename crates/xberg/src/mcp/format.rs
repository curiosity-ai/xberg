//! MCP response formatting and configuration helpers.
//!
//! This module provides utilities for formatting extraction results and building configurations.

use crate::ExtractionConfig;
use crate::core::config::merge::build_config_from_json;

/// Build extraction config from MCP parameters.
///
/// Merges the provided config JSON (if any) with the default config using JSON-level
/// merge semantics. Unspecified fields in the JSON preserve their values from the default config.
pub(super) fn build_config(
    default_config: &ExtractionConfig,
    config_json: Option<serde_json::Value>,
) -> Result<ExtractionConfig, String> {
    if let Some(config) = config_json.as_ref() {
        crate::core::config::request_security::validate_caller_extraction_config(config)?;
    }
    let json_string = config_json
        .map(|v| serde_json::to_string(&v))
        .transpose()
        .map_err(|e| format!("Failed to serialize config JSON: {e}"))?;
    build_config_from_json(default_config, json_string.as_deref())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_build_config_with_no_config() {
        let default_config = ExtractionConfig::default();

        let config = build_config(&default_config, None).unwrap();
        assert_eq!(config.use_cache, default_config.use_cache);
    }

    #[test]
    fn test_build_config_with_config_json() {
        let default_config = ExtractionConfig::default();
        let config_json = serde_json::json!({
            "use_cache": false
        });

        let config = build_config(&default_config, Some(config_json)).unwrap();
        assert!(!config.use_cache);
    }

    #[test]
    fn test_build_config_with_invalid_config_json() {
        let default_config = ExtractionConfig::default();
        let config_json = serde_json::json!({
            "use_cache": "not_a_boolean"
        });

        let result = build_config(&default_config, Some(config_json));
        assert!(result.is_err());
        assert!(result.unwrap_err().contains("Failed to deserialize"));
    }

    #[test]
    fn test_build_config_preserves_default_config_settings() {
        let default_config = ExtractionConfig {
            use_cache: false,
            ..Default::default()
        };

        let config = build_config(&default_config, None).unwrap();

        assert!(!config.use_cache);
    }

    #[test]
    fn test_build_config_overrides_default_settings() {
        let default_config = ExtractionConfig {
            use_cache: true,
            ..Default::default()
        };

        let config_json = serde_json::json!({
            "use_cache": false
        });

        let config = build_config(&default_config, Some(config_json)).unwrap();
        assert!(!config.use_cache);
    }

    #[test]
    fn test_build_config_merges_partial_config() {
        let default_config = ExtractionConfig {
            use_cache: false,
            enable_quality_processing: true,
            force_ocr: false,
            ..Default::default()
        };

        let config_json = serde_json::json!({
            "force_ocr": true
        });

        let config = build_config(&default_config, Some(config_json)).unwrap();

        assert!(!config.use_cache, "use_cache should be preserved from default config");
        assert!(
            config.enable_quality_processing,
            "enable_quality_processing should be preserved"
        );
        assert!(config.force_ocr, "force_ocr should be overridden to true");
    }

    #[test]
    fn test_build_config_merges_nested_config() {
        let default_config = ExtractionConfig {
            use_cache: true,
            ..Default::default()
        };

        let config_json = serde_json::json!({
            "output_format": "markdown"
        });

        let config = build_config(&default_config, Some(config_json)).unwrap();

        assert!(config.use_cache, "use_cache should be preserved from default config");
        assert_eq!(
            config.output_format,
            crate::core::config::formats::OutputFormat::Markdown,
            "output_format should be overridden to markdown"
        );
    }

    #[test]
    fn test_build_config_merges_with_custom_defaults() {
        let default_config = ExtractionConfig {
            use_cache: false,
            enable_quality_processing: true,
            force_ocr: false,
            ..Default::default()
        };

        let config_json = serde_json::json!({
            "force_ocr": true,
        });

        let config = build_config(&default_config, Some(config_json)).unwrap();

        assert!(config.force_ocr, "force_ocr should be overridden to true");
        assert!(
            !config.use_cache,
            "use_cache should be preserved from default config (false)"
        );
        assert!(
            config.enable_quality_processing,
            "enable_quality_processing should be preserved (true)"
        );
    }

    #[test]
    fn test_build_config_merges_multiple_fields() {
        let default_config = ExtractionConfig {
            use_cache: true,
            enable_quality_processing: false,
            force_ocr: true,
            ..Default::default()
        };

        let config_json = serde_json::json!({
            "use_cache": false,
            "output_format": "markdown",
        });

        let config = build_config(&default_config, Some(config_json)).unwrap();

        assert!(!config.use_cache, "use_cache should be overridden to false");
        assert_eq!(
            config.output_format,
            crate::core::config::formats::OutputFormat::Markdown,
            "output_format should be overridden to markdown"
        );
        assert!(
            config.force_ocr,
            "force_ocr should be preserved from default config (true)"
        );
        assert!(
            !config.enable_quality_processing,
            "enable_quality_processing should be preserved (false)"
        );
    }

    #[test]
    fn test_build_config_boolean_override_to_default_value() {
        let base = ExtractionConfig {
            use_cache: false,
            ..Default::default()
        };

        let override_json = serde_json::json!({"use_cache": true});

        let merged = build_config(&base, Some(override_json)).unwrap();

        assert!(
            merged.use_cache,
            "Should use explicit override even if it matches default"
        );
    }

    #[test]
    fn should_reject_caller_llm_transport_config_before_merging_trusted_defaults() {
        let default_config = ExtractionConfig {
            ocr: Some(crate::OcrConfig {
                vlm_config: Some(crate::LlmConfig {
                    model: "trusted/model".to_string(),
                    base_url: Some("https://trusted.example".to_string()),
                    ..Default::default()
                }),
                ..Default::default()
            }),
            ..Default::default()
        };

        let safe_override = serde_json::json!({"force_ocr": true});
        assert!(
            build_config(&default_config, Some(safe_override))
                .expect("trusted default transport config must remain valid")
                .force_ocr
        );

        let caller_override = serde_json::json!({
            "ocr": {"vlm_config": {"model": "caller/model", "headers": {"Authorization": "secret"}}}
        });
        assert_eq!(
            build_config(&default_config, Some(caller_override)).expect_err("caller transport config must be rejected"),
            "Caller extraction config may not set ocr.vlm_config.headers"
        );
    }
}
