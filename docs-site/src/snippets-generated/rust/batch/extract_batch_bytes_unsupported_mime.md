---
id: fixture_rust_extract_batch_bytes_unsupported_mime
language: rust
target: rust
level: typecheck
requires: []
side_effect: safe
---

extract_batch with unsupported bytes MIME type

```rust title="Rust"
use xberg::extract_batch;
use xberg::ExtractInput;

#[tokio::main]
async fn main() {
    let inputs_json: serde_json::Value = serde_json::from_str(r#"[{"bytes":[100,97,116,97],"kind":"bytes","mime_type":"application/x-unknown"}]"#).unwrap();
    let inputs = serde_json::from_value::<Vec<ExtractInput>>(inputs_json).unwrap();
    let config = Default::default();
    let _ = extract_batch(inputs, &config).await;
}

```
