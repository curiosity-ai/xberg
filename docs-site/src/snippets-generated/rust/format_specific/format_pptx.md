---
id: fixture_rust_format_pptx
language: rust
target: rust
level: typecheck
requires: []
side_effect: server
---

PPTX presentation extraction using extract

```rust title="Rust"
use xberg::extract;
use xberg::ExtractInput;

#[tokio::main]
async fn main() {
    let input_json: serde_json::Value = serde_json::from_str(r#"{"kind":"uri","mime_type":"application/vnd.openxmlformats-officedocument.presentationml.presentation","uri":"https://example.com/pptx/simple.pptx"}"#).unwrap();
    let input = serde_json::from_value::<ExtractInput>(input_json).unwrap();
    let config = Default::default();
    let _ = extract(input, &config).await;
}

```
