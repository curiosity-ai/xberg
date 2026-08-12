---
id: fixture_rust_unregister_post_processor_after_register
language: rust
target: rust
level: typecheck
requires: []
side_effect: safe
---

unregister_post_processor

```rust title="Rust"
use xberg::unregister_post_processor;

fn main() {
    let name = r#"test-processor"#;
    let _ = unregister_post_processor(name);
}

```
