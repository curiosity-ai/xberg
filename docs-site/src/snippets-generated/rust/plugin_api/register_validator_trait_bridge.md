---
id: fixture_rust_register_validator_trait_bridge
language: rust
target: rust
level: typecheck
requires: []
side_effect: safe
---

register_validator: trait bridge

```rust title="Rust"
use xberg::register_validator;

fn main() {
    #[allow(unused_imports)]
    use xberg::ExtractedDocument;
    #[allow(unused_imports)]
    use xberg::ExtractionConfig;
    #[allow(unused_imports)]
    use xberg::Validator;
    #[allow(unused_imports)]
    use xberg::XbergError;
    struct TestStubRegisterValidatorTraitBridge { _name: &'static str }
    impl xberg::plugins::Plugin for TestStubRegisterValidatorTraitBridge {
        fn name(&self) -> &str { self._name }
    }
    #[async_trait::async_trait]
    impl Validator for TestStubRegisterValidatorTraitBridge {
        async fn validate(&self, _p0: &ExtractedDocument, _p1: &ExtractionConfig) -> Result<(), XbergError> { Ok(()) }
    }
    let _ = register_validator(std::sync::Arc::new(TestStubRegisterValidatorTraitBridge { _name: "test-validator" }));
}

```
