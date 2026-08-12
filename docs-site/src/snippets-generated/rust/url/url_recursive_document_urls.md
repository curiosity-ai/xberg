---
id: fixture_rust_url_recursive_document_urls
language: rust
target: rust
level: typecheck
requires: []
side_effect: server
---

extract: recursive URL extraction follows document links discovered in results

```rust title="Rust"
use xberg::extract;
use xberg::ExtractInput;

#[tokio::main]
async fn main() {
    let input_json: serde_json::Value = serde_json::from_str(r#"{"kind":"uri","uri":"https://example.com"}"#).unwrap();
    let input = serde_json::from_value::<ExtractInput>(input_json).unwrap();
    let config_json: serde_json::Value = serde_json::from_str(r#"{"url":{"crawl":{"document_url_depth":1,"follow_document_urls":true,"respect_robots_txt":false},"mode":"document"}}"#).unwrap();
    let config = serde_json::from_value(config_json).unwrap();
    let _ = extract(input, &config).await;
}

```
