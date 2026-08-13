---
id: fixture_rust_error_empty_bytes
language: rust
target: rust
level: typecheck
requires: []
side_effect: safe
---

Graceful handling of empty bytes (should not error)

```rust title="Rust"
use xberg::extract;
use xberg::ExtractInput;

#[tokio::main]
async fn main() {
    let input_json: serde_json::Value = serde_json::from_str(r#"{"bytes":[],"config":{},"filename":"empty.txt","kind":"bytes","mime_type":"text/plain"}"#).unwrap();
    let input = serde_json::from_value::<ExtractInput>(input_json).unwrap();
    let config_json: serde_json::Value = serde_json::from_str(r#"{}"#).unwrap();
    let config = serde_json::from_value(config_json).unwrap();
    let _ = extract(input, &config).await;
}

```
