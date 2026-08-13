---
id: fixture_rust_format_hwpx_standalone
language: rust
target: rust
level: typecheck
requires: []
side_effect: server
---

Standalone HWPX extraction using extract

```rust title="Rust"
use xberg::extract;
use xberg::ExtractInput;

#[tokio::main]
async fn main() {
    let input_json: serde_json::Value = serde_json::from_str(r#"{"filename":"simple.hwpx","kind":"uri","mime_type":"application/haansofthwpx","uri":"https://example.com/hwpx/simple.hwpx"}"#).unwrap();
    let input = serde_json::from_value::<ExtractInput>(input_json).unwrap();
    let config = Default::default();
    let _ = extract(input, &config).await;
}

```
