---
id: fixture_java_register_embedding_backend_trait_bridge
language: java
target: java
level: typecheck
requires: []
side_effect: safe
---

register_embedding_backend: trait bridge

```java title="Java"
import io.xberg.Xberg.*;

public final class Example {
    public static void main(String[] args) throws Exception {
        class TestStubRegisterEmbeddingBackendTraitBridge implements io.xberg.IEmbeddingBackend {
    @Override
    public long dimensions() {
        return 768;
    }
    @Override
    public java.util.List<java.util.List<Float>> embed(java.util.List<String> texts) {
        return new java.util.ArrayList<>();
    }
    @Override
    public String name() { return "test-embedding-backend"; }
    @Override
    public String version() {
        return "";
    }
    @Override
    public void initialize() {}
    @Override
    public void shutdown() {}
}

        Xberg.registerEmbeddingBackend(new TestStubRegisterEmbeddingBackendTraitBridge());
    }
}

```
