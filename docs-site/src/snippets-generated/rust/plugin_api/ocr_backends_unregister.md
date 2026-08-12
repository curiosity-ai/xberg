---
id: fixture_rust_ocr_backends_unregister
language: rust
target: rust
level: typecheck
requires: []
side_effect: safe
---

Unregister nonexistent OCR backend gracefully

```rust title="Rust"
use xberg::unregister_ocr_backend;

fn main() {
    let name = r#"nonexistent-backend-xyz"#;
    let _ = unregister_ocr_backend(name);
}

```
