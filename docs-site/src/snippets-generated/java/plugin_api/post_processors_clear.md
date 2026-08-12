---
id: fixture_java_post_processors_clear
language: java
target: java
level: typecheck
requires: []
side_effect: safe
---

Clear all post-processors and verify list is empty

```java title="Java"
import io.xberg.Xberg.*;

public final class Example {
    public static void main(String[] args) throws Exception {
        Xberg.clearPostProcessors();
    }
}

```
