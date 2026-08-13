---
id: fixture_rust_register_tokenizer_backend_trait_bridge
language: rust
target: rust
level: typecheck
requires: []
side_effect: safe
---

register_tokenizer_backend: trait bridge

```rust title="Rust"
use xberg::register_tokenizer_backend;

fn main() {
    #[allow(unused_imports)]
    use xberg::TokenizerBackend;
    struct TestStubRegisterTokenizerBackendTraitBridge { _name: &'static str }
    impl xberg::plugins::Plugin for TestStubRegisterTokenizerBackendTraitBridge {
        fn name(&self) -> &str { self._name }
    }
    impl TokenizerBackend for TestStubRegisterTokenizerBackendTraitBridge {
        fn count_tokens(&self, _p0: &str) -> usize { 0 }
    }
    let _ = register_tokenizer_backend(std::sync::Arc::new(TestStubRegisterTokenizerBackendTraitBridge { _name: "test-tokenizer-backend" }));
}

```
