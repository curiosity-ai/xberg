---
id: fixture_java_unregister_post_processor_after_register
language: java
target: java
level: typecheck
requires: []
side_effect: safe
---

unregister_post_processor

```java title="Java"
import io.xberg.Xberg.*;

public final class Example {
    public static void main(String[] args) throws Exception {
        Xberg.unregisterPostProcessor("test-processor");
    }
}

```
