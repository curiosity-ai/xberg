---
id: fixture_java_ocr_backends_unregister
language: java
target: java
level: typecheck
requires: []
side_effect: safe
---

Unregister nonexistent OCR backend gracefully

```java title="Java"
import io.xberg.Xberg.*;

public final class Example {
    public static void main(String[] args) throws Exception {
        Xberg.unregisterOcrBackend("nonexistent-backend-xyz");
    }
}

```
