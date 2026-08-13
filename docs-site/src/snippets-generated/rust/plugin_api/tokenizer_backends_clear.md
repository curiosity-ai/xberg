---
id: fixture_rust_tokenizer_backends_clear
language: rust
target: rust
level: typecheck
requires: []
side_effect: safe
---

Clear all tokenizer backends and verify list is empty

```rust title="Rust"
use xberg::clear_tokenizer_backends;

fn main() {
    let _ = clear_tokenizer_backends();
}

```
