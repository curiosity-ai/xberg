---
id: fixture_rust_register_reranker_backend_trait_bridge
language: rust
target: rust
level: typecheck
requires: []
side_effect: safe
---

register_reranker_backend: trait bridge

```rust title="Rust"
use xberg::register_reranker_backend;

fn main() {
    #[allow(unused_imports)]
    use xberg::RerankerBackend;
    #[allow(unused_imports)]
    use xberg::XbergError;
    struct TestStubRegisterRerankerBackendTraitBridge { _name: &'static str }
    impl xberg::plugins::Plugin for TestStubRegisterRerankerBackendTraitBridge {
        fn name(&self) -> &str { self._name }
    }
    #[async_trait::async_trait]
    impl RerankerBackend for TestStubRegisterRerankerBackendTraitBridge {
        async fn rerank(&self, _p0: String, _p1: Vec<String>) -> Result<Vec<f32>, XbergError> { Ok(Vec::new()) }
    }
    let _ = register_reranker_backend(std::sync::Arc::new(TestStubRegisterRerankerBackendTraitBridge { _name: "test-reranker-backend" }));
}

```
