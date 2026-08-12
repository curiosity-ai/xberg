---
id: fixture_rust_config_security_limits
language: rust
target: rust
level: typecheck
requires: []
side_effect: server
---

Tests archive extraction with custom security limits

```rust title="Rust"
use xberg::extract;
use xberg::ExtractInput;

#[tokio::main]
async fn main() {
    let input_json: serde_json::Value = serde_json::from_str(r#"{"kind":"uri","uri":"https://example.com/archives/documents.zip"}"#).unwrap();
    let input = serde_json::from_value::<ExtractInput>(input_json).unwrap();
    let config_json: serde_json::Value = serde_json::from_str(r#"{"security_limits":{"max_archive_size":104857600,"max_compression_ratio":50,"max_files_in_archive":100}}"#).unwrap();
    let config = serde_json::from_value(config_json).unwrap();
    let _ = extract(input, &config).await;
}

```
