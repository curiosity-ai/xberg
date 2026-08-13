---
id: fixture_java_unregister_embedding_backend_after_register
language: java
target: java
level: typecheck
requires: []
side_effect: safe
---

unregister_embedding_backend

```java title="Java"
import io.xberg.Xberg.*;

public final class Example {
    public static void main(String[] args) throws Exception {
        Xberg.unregisterEmbeddingBackend("test-embedding-backend");
    }
}

```
