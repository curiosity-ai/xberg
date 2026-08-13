---
id: fixture_rust_register_embedding_backend_trait_bridge
language: rust
target: rust
level: typecheck
requires: []
side_effect: safe
---

register_embedding_backend: trait bridge

```rust title="Rust"
use xberg::register_embedding_backend;

fn main() {
    #[allow(unused_imports)]
    use xberg::EmbeddingBackend;
    #[allow(unused_imports)]
    use xberg::XbergError;
    struct TestStubRegisterEmbeddingBackendTraitBridge { _name: &'static str }
    impl xberg::plugins::Plugin for TestStubRegisterEmbeddingBackendTraitBridge {
        fn name(&self) -> &str { self._name }
    }
    #[async_trait::async_trait]
    impl EmbeddingBackend for TestStubRegisterEmbeddingBackendTraitBridge {
        fn dimensions(&self) -> usize { 0 }
        async fn embed(&self, _p0: Vec<String>) -> Result<Vec<Vec<f32>>, XbergError> { Ok(Vec::new()) }
    }
    let _ = register_embedding_backend(std::sync::Arc::new(TestStubRegisterEmbeddingBackendTraitBridge { _name: "test-embedding-backend" }));
}

```
