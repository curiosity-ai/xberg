---
id: fixture_rust_extract_batch_bytes_mixed_format
language: rust
target: rust
level: typecheck
requires: []
side_effect: safe
---

extract_batch: handles unsupported MIME gracefully

```rust title="Rust"
use xberg::extract_batch;
use xberg::ExtractInput;

#[tokio::main]
async fn main() {
    let inputs_json: serde_json::Value = serde_json::from_str(r#"[{"bytes":[80,68,70,32,112,108,97,99,101,104,111,108,100,101,114],"kind":"bytes","mime_type":"application/x-unknown"}]"#).unwrap();
    let inputs = serde_json::from_value::<Vec<ExtractInput>>(inputs_json).unwrap();
    let config = Default::default();
    let _ = extract_batch(inputs, &config).await;
}

```
