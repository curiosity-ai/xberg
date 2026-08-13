---
id: fixture_kotlin_android_extract_batch_bytes_invalid_mime
language: kotlin
target: kotlin_android
level: typecheck
requires: []
side_effect: safe
---

extract_batch with invalid bytes MIME type

```kotlin title="Kotlin (Android)"
import io.xberg.*

fun main() = kotlinx.coroutines.runBlocking {
    val result = Xberg.extractBatch(listOf(MAPPER.readValue("{\"bytes\":[72,101,108,108,111],\"kind\":\"bytes\",\"mime_type\":\"application/x-nonexistent\"}", ExtractInput::class.java)), ExtractionConfig())
}

```
