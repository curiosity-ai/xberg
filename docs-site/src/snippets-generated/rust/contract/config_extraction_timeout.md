---
id: fixture_rust_config_extraction_timeout
language: rust
target: rust
level: typecheck
requires: []
side_effect: server
---

Tests that extraction_timeout_secs config field is accepted and does not affect fast extractions

```rust title="Rust"
use xberg::extract;
use xberg::ExtractInput;

#[tokio::main]
async fn main() {
    let input_json: serde_json::Value = serde_json::from_str(r#"{"kind":"uri","uri":"https://example.com/pdf/fake_memo.pdf"}"#).unwrap();
    let input = serde_json::from_value::<ExtractInput>(input_json).unwrap();
    let config_json: serde_json::Value = serde_json::from_str(r#"{"extraction_timeout_secs":300}"#).unwrap();
    let config = serde_json::from_value(config_json).unwrap();
    let _ = extract(input, &config).await;
}

```
