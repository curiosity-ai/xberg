---
id: fixture_rust_api_extract_batch_uri
language: rust
target: rust
level: typecheck
requires: []
side_effect: server
---

Tests batch URI extraction API (extract_batch)

```rust title="Rust"
use xberg::extract_batch;
use xberg::ExtractInput;

#[tokio::main]
async fn main() {
    let inputs_json: serde_json::Value = serde_json::from_str(r#"[{"kind":"uri","uri":"https://example.com/pdf/fake_memo.pdf"}]"#).unwrap();
    let inputs = serde_json::from_value::<Vec<ExtractInput>>(inputs_json).unwrap();
    let config = Default::default();
    let _ = extract_batch(inputs, &config).await;
}

```
