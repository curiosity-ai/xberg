---
id: fixture_rust_smoke_json_basic
language: rust
target: rust
level: typecheck
requires: []
side_effect: server
---

Smoke test: JSON file extraction

```rust title="Rust"
use xberg::extract;
use xberg::ExtractInput;

#[tokio::main]
async fn main() {
    let input_json: serde_json::Value = serde_json::from_str(r#"{"kind":"uri","mime_type":"application/json","uri":"https://example.com/json/simple.json"}"#).unwrap();
    let input = serde_json::from_value::<ExtractInput>(input_json).unwrap();
    let config_json: serde_json::Value = serde_json::from_str(r#"{}"#).unwrap();
    let config = serde_json::from_value(config_json).unwrap();
    let _ = extract(input, &config).await;
}

```
