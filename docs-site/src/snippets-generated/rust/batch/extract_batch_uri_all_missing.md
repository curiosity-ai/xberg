---
id: fixture_rust_extract_batch_uri_all_missing
language: rust
target: rust
level: typecheck
requires: []
side_effect: safe
---

extract_batch with missing URI inputs

```rust title="Rust"
use xberg::extract_batch;
use xberg::ExtractInput;

#[tokio::main]
async fn main() {
    let inputs_json: serde_json::Value = serde_json::from_str(r#"[{"kind":"uri","uri":"/nonexistent/a.pdf"},{"kind":"uri","uri":"/nonexistent/b.txt"}]"#).unwrap();
    let inputs = serde_json::from_value::<Vec<ExtractInput>>(inputs_json).unwrap();
    let config = Default::default();
    let _ = extract_batch(inputs, &config).await;
}

```
