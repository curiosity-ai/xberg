---
id: fixture_rust_list_ocr_backends
language: rust
target: rust
level: typecheck
requires: []
side_effect: safe
---

List OCR backends

```rust title="Rust"
use xberg::list_ocr_backends;

fn main() {
    let _ = list_ocr_backends();
}

```
