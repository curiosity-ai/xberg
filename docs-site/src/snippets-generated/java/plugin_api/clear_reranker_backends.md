---
id: fixture_java_clear_reranker_backends
language: java
target: java
level: typecheck
requires: []
side_effect: safe
---

Clear all reranker backends and verify list is empty

```java title="Java"
import io.xberg.Xberg.*;

public final class Example {
    public static void main(String[] args) throws Exception {
        Xberg.clearRerankerBackends();
    }
}

```
