---
id: fixture_rust_format_xlsx
language: rust
target: rust
level: typecheck
requires: []
side_effect: server
---

XLSX spreadsheet extraction using extract

```rust title="Rust"
use xberg::extract;
use xberg::ExtractInput;

#[tokio::main]
async fn main() {
    let input_json: serde_json::Value = serde_json::from_str(r#"{"kind":"uri","mime_type":"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet","uri":"https://example.com/xlsx/stanley_cups.xlsx"}"#).unwrap();
    let input = serde_json::from_value::<ExtractInput>(input_json).unwrap();
    let config = Default::default();
    let _ = extract(input, &config).await;
}

```
