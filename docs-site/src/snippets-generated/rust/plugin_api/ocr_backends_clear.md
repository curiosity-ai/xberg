---
id: fixture_rust_ocr_backends_clear
language: rust
target: rust
level: typecheck
requires: []
side_effect: safe
---

Clear all OCR backends and verify list is empty

```rust title="Rust"
use xberg::clear_ocr_backends;

fn main() {
    let _ = clear_ocr_backends();
}

```
