---
id: fixture_java_register_reranker_backend_trait_bridge
language: java
target: java
level: typecheck
requires: []
side_effect: safe
---

register_reranker_backend: trait bridge

```java title="Java"
import io.xberg.Xberg.*;

public final class Example {
    public static void main(String[] args) throws Exception {
        class TestStubRegisterRerankerBackendTraitBridge implements io.xberg.IRerankerBackend {
    @Override
    public java.util.List<Float> rerank(String query, java.util.List<String> documents) {
        return new java.util.ArrayList<>();
    }
    @Override
    public String name() { return "test-reranker-backend"; }
    @Override
    public String version() {
        return "";
    }
    @Override
    public void initialize() {}
    @Override
    public void shutdown() {}
}

        Xberg.registerRerankerBackend(new TestStubRegisterRerankerBackendTraitBridge());
    }
}

```
