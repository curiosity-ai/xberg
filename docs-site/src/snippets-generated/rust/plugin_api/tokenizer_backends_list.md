---
id: fixture_rust_tokenizer_backends_list
language: rust
target: rust
level: typecheck
requires: []
side_effect: safe
---

List all registered tokenizer backends

```rust title="Rust"
use xberg::list_tokenizer_backends;

fn main() {
    let _ = list_tokenizer_backends();
}

```
