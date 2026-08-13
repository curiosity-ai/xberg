---
id: fixture_rust_register_post_processor_trait_bridge
language: rust
target: rust
level: typecheck
requires: []
side_effect: safe
---

register_post_processor: trait bridge

```rust title="Rust"
use xberg::register_post_processor;

fn main() {
    #[allow(unused_imports)]
    use xberg::ExtractedDocument;
    #[allow(unused_imports)]
    use xberg::ExtractionConfig;
    #[allow(unused_imports)]
    use xberg::PostProcessor;
    #[allow(unused_imports)]
    use xberg::ProcessingStage;
    #[allow(unused_imports)]
    use xberg::XbergError;
    struct TestStubRegisterPostProcessorTraitBridge { _name: &'static str }
    impl xberg::plugins::Plugin for TestStubRegisterPostProcessorTraitBridge {
        fn name(&self) -> &str { self._name }
    }
    #[async_trait::async_trait]
    impl PostProcessor for TestStubRegisterPostProcessorTraitBridge {
        async fn process(&self, _p0: &mut ExtractedDocument, _p1: &ExtractionConfig) -> Result<(), XbergError> { Ok(()) }
        fn processing_stage(&self) -> ProcessingStage { ProcessingStage::default() }
    }
    let _ = register_post_processor(std::sync::Arc::new(TestStubRegisterPostProcessorTraitBridge { _name: "test-processor" }));
}

```
