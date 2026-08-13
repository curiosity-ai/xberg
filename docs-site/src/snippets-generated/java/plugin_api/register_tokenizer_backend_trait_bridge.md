---
id: fixture_java_register_tokenizer_backend_trait_bridge
language: java
target: java
level: typecheck
requires: []
side_effect: safe
---

register_tokenizer_backend: trait bridge

```java title="Java"
import io.xberg.Xberg.*;

public final class Example {
    public static void main(String[] args) throws Exception {
        class TestStubRegisterTokenizerBackendTraitBridge implements io.xberg.ITokenizerBackend {
    @Override
    public long count_tokens(String text) {
        return 3;
    }
    @Override
    public String name() { return "test-tokenizer-backend"; }
    @Override
    public String version() {
        return "";
    }
    @Override
    public void initialize() {}
    @Override
    public void shutdown() {}
}

        Xberg.registerTokenizerBackend(new TestStubRegisterTokenizerBackendTraitBridge());
    }
}

```
