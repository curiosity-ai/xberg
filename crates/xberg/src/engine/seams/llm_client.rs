//! The [`LlmClient`] seam: the JSON-schema-constrained LLM completion call.
//!
//! The in-core default is [`LiterLlmClient`], which delegates verbatim to
//! [`llm::structured::complete_with_json_schema`](crate::llm::structured::complete_with_json_schema)
//! — the liter-llm-backed path xberg uses today.
//!
//! Gated behind `liter-llm`: without that feature there is no LLM dependency to
//! wrap, so neither the trait nor its default exist.

use async_trait::async_trait;
use serde_json::Value;

use crate::Result;
use crate::core::config::LlmConfig;
use crate::types::LlmUsage;

/// Abstracts the JSON-schema-constrained LLM completion used by the structured
/// and post-processing paths.
///
/// # Thread safety
///
/// Implementations are `Send + Sync + 'static` and held behind
/// `Arc<dyn LlmClient>`; they may be called concurrently.
#[cfg_attr(not(target_arch = "wasm32"), async_trait)]
#[cfg_attr(target_arch = "wasm32", async_trait(?Send))]
pub trait LlmClient: Send + Sync + 'static {
    /// Send `prompt` to the configured model with a JSON-schema response
    /// constraint and return the parsed JSON value plus captured usage.
    ///
    /// # Errors
    ///
    /// Propagates client construction, request, and JSON-parse failures.
    async fn complete_with_json_schema(
        &self,
        llm_config: &LlmConfig,
        prompt: &str,
        schema_name: &str,
        schema: &Value,
        source: &str,
    ) -> Result<(Value, Option<LlmUsage>)>;
}

/// In-core default: the liter-llm-backed client.
///
/// Delegates straight to
/// [`llm::structured::complete_with_json_schema`](crate::llm::structured::complete_with_json_schema),
/// so its behavior is identical to calling that function directly.
#[derive(Debug, Default, Clone, Copy)]
pub struct LiterLlmClient;

#[cfg_attr(not(target_arch = "wasm32"), async_trait)]
#[cfg_attr(target_arch = "wasm32", async_trait(?Send))]
impl LlmClient for LiterLlmClient {
    async fn complete_with_json_schema(
        &self,
        llm_config: &LlmConfig,
        prompt: &str,
        schema_name: &str,
        schema: &Value,
        source: &str,
    ) -> Result<(Value, Option<LlmUsage>)> {
        crate::llm::structured::complete_with_json_schema(llm_config, prompt, schema_name, schema, source).await
    }
}
