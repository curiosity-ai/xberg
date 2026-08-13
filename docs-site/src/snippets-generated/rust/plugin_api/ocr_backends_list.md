---
id: fixture_rust_ocr_backends_list
language: rust
target: rust
level: typecheck
requires: []
side_effect: safe
---

List all registered OCR backends

```rust title="Rust"
use xberg::list_ocr_backends;

fn main() {
    let _ = list_ocr_backends();
}

```
