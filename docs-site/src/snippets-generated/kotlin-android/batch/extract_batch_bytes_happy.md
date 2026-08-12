---
id: fixture_kotlin_android_extract_batch_bytes_happy
language: kotlin
target: kotlin_android
level: typecheck
requires: []
side_effect: safe
---

Extract multiple in-memory documents in one batch.

```kotlin title="Kotlin (Android)"
import io.xberg.*

fun main() = kotlinx.coroutines.runBlocking {
    val result = Xberg.extractBatch(listOf(MAPPER.readValue("{\"bytes\":[72,101,108,108,111,44,32,119,111,114,108,100,33],\"kind\":\"bytes\",\"mime_type\":\"text/plain\"}", ExtractInput::class.java), MAPPER.readValue("{\"bytes\":\"test_documents/html/html.html\",\"kind\":\"bytes\",\"mime_type\":\"text/html\"}", ExtractInput::class.java)), ExtractionConfig())
}

```
