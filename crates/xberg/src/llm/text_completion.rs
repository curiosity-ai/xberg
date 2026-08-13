//! Plain text completion helper for LLM-driven post-processors.
//!
//! Wraps a liter-llm chat call that takes a free-form prompt and returns the
//! assistant text. Used by translation and summarisation-abstractive
//! post-processors; not constrained by a JSON schema.

use crate::core::config::LlmConfig;
use crate::types::LlmUsage;

/// Send a single user prompt to the configured LLM and return the response text
/// along with the captured usage metadata.
///
/// The `source` argument labels the [`LlmUsage`] entry that is returned so
/// callers can aggregate per-feature spend (`"translation"`, `"summarisation"`,
/// etc.). The helper performs a single non-streaming chat completion request.
///
/// # Errors
///
/// Returns an error if the LLM client cannot be constructed, the request fails,
/// or the response does not contain assistant content.
#[allow(clippy::field_reassign_with_default)]
#[cfg_attr(alef, alef(skip))]
pub async fn complete_text(
    llm_config: &LlmConfig,
    prompt: &str,
    source: &str,
) -> crate::Result<(String, Option<LlmUsage>)> {
    use liter_llm::LlmClient;

    let client = super::client::create_client(llm_config)?;

    let mut request = liter_llm::ChatCompletionRequest::default();
    request.model = llm_config.model.clone();
    request.messages = vec![liter_llm::Message::User(liter_llm::UserMessage {
        content: liter_llm::UserContent::Text(prompt.to_string()),
        name: None,
    })];
    super::client::apply_request_time_params(&mut request, llm_config)?;

    let response = client
        .chat(request)
        .await
        .map_err(|e| crate::XbergError::parsing(format!("LLM text completion request failed ({source}): {e}")))?;

    let usage = super::usage::extract_usage_from_chat(&response, source);

    let text = response
        .choices
        .first()
        .and_then(|c| c.message.content.as_ref().and_then(|m| m.as_text()))
        .map(|s| s.trim().to_string())
        .ok_or_else(|| {
            crate::XbergError::parsing(format!(
                "LLM text completion ({source}) returned no content (model={}, {} choices)",
                llm_config.model,
                response.choices.len()
            ))
        })?;

    Ok((text, usage))
}

#[cfg(all(test, feature = "api"))]
mod tests {
    use super::*;

    /// End-to-end guard on the *wiring*, not just the helper: the unit tests around
    /// `client::apply_request_time_params` would stay green if a request builder stopped
    /// calling it, which is exactly how `top_p`/`stop`/`seed`/`presence_penalty`/
    /// `frequency_penalty` came to be accepted by every config file and binding while
    /// never reaching a provider. This asserts on the JSON that actually goes on the wire.
    #[tokio::test]
    async fn should_send_every_request_time_param_on_the_wire() {
        use axum::{Router, routing::post};
        use tokio::sync::mpsc;

        let (tx, mut rx) = mpsc::unbounded_channel::<serde_json::Value>();

        let app = Router::new().fallback(post(move |body: axum::extract::Json<serde_json::Value>| async move {
            let _ = tx.send(body.0);
            axum::response::Json(serde_json::json!({
                "id": "test",
                "object": "chat.completion",
                "created": 12345,
                "model": "test",
                "choices": [{
                    "index": 0,
                    "message": { "role": "assistant", "content": "ok" },
                    "finish_reason": "stop"
                }]
            }))
        }));

        let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
        let addr = listener.local_addr().unwrap();
        tokio::spawn(async move {
            axum::serve(listener, app).await.unwrap();
        });

        let config = LlmConfig {
            model: "openai/gpt-4o".to_string(),
            api_key: Some("test-api-key".to_string()),
            base_url: Some(format!("http://{}/v1/", addr)),
            temperature: Some(0.7),
            max_tokens: Some(1024),
            top_p: Some(0.9),
            stop: Some(vec!["[END]".to_string()]),
            seed: Some(42),
            presence_penalty: Some(0.5),
            frequency_penalty: Some(-0.5),
            ..LlmConfig::default()
        };

        complete_text(&config, "hello", "test").await.expect("request failed");

        let sent = tokio::time::timeout(tokio::time::Duration::from_secs(5), rx.recv())
            .await
            .expect("timed out waiting for the request body")
            .expect("no request body received");

        assert_eq!(sent["temperature"], serde_json::json!(0.7));
        assert_eq!(sent["max_tokens"], serde_json::json!(1024));
        assert_eq!(sent["top_p"], serde_json::json!(0.9));
        assert_eq!(sent["stop"], serde_json::json!(["[END]"]));
        assert_eq!(sent["seed"], serde_json::json!(42));
        assert_eq!(sent["presence_penalty"], serde_json::json!(0.5));
        assert_eq!(sent["frequency_penalty"], serde_json::json!(-0.5));
    }
}
