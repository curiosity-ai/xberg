---
id: fixture_rust_config_tree_sitter
language: rust
target: rust
level: typecheck
requires: []
side_effect: server
---

Tests tree-sitter configuration round-trip

```rust title="Rust"
use xberg::extract;
use xberg::ExtractInput;

#[tokio::main]
async fn main() {
    let input_json: serde_json::Value = serde_json::from_str(r#"{"kind":"uri","uri":"https://example.com/code/hello.py"}"#).unwrap();
    let input = serde_json::from_value::<ExtractInput>(input_json).unwrap();
    let config_json: serde_json::Value = serde_json::from_str(r#"{"tree_sitter":{"groups":["web"],"languages":["python","rust"],"process":{"comments":false,"diagnostics":false,"docstrings":false,"exports":true,"imports":true,"structure":true,"symbols":false}}}"#).unwrap();
    let config = serde_json::from_value(config_json).unwrap();
    let _ = extract(input, &config).await;
}

```
