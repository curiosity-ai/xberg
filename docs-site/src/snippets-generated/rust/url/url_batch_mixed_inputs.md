---
id: fixture_rust_url_batch_mixed_inputs
language: rust
target: rust
level: typecheck
requires: []
side_effect: server
---

extract_batch: mixed bytes and URL inputs share one output envelope

```rust title="Rust"
use xberg::extract_batch;
use xberg::ExtractInput;

#[tokio::main]
async fn main() {
    let inputs_json: serde_json::Value = serde_json::from_str(r#"[{"kind":"uri","uri":"https://example.com"},{"bytes":[66,97,116,99,104,32,98,121,116,101,115,32,99,111,110,116,101,110,116],"filename":"inline.txt","kind":"bytes","mime_type":"text/plain"}]"#).unwrap();
    let inputs = serde_json::from_value::<Vec<ExtractInput>>(inputs_json).unwrap();
    let config_json: serde_json::Value = serde_json::from_str(r#"{"url":{"mode":"document"}}"#).unwrap();
    let config = serde_json::from_value(config_json).unwrap();
    let _ = extract_batch(inputs, &config).await;
}

```
