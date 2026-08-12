---
id: fixture_rust_url_crawl_linked_pages
language: rust
target: rust
level: typecheck
requires: []
side_effect: server
---

extract: crawl mode follows linked pages

```rust title="Rust"
use xberg::extract;
use xberg::ExtractInput;

#[tokio::main]
async fn main() {
    let input_json: serde_json::Value = serde_json::from_str(r#"{"kind":"uri","uri":"https://example.com"}"#).unwrap();
    let input = serde_json::from_value::<ExtractInput>(input_json).unwrap();
    let config_json: serde_json::Value = serde_json::from_str(r#"{"url":{"crawl":{"max_depth":1,"max_pages":4,"respect_robots_txt":false},"mode":"crawl"}}"#).unwrap();
    let config = serde_json::from_value(config_json).unwrap();
    let _ = extract(input, &config).await;
}

```
