---
id: fixture_kotlin_android_extract_batch_uri_partial_failure
language: kotlin
target: kotlin_android
level: typecheck
requires: []
side_effect: safe
---

extract_batch with mixed valid and missing URI inputs

```kotlin title="Kotlin (Android)"
import io.xberg.*

fun main() = kotlinx.coroutines.runBlocking {
    val result = Xberg.extractBatch(listOf(MAPPER.readValue("{\"kind\":\"uri\",\"uri\":\"text/plain.txt\"}", ExtractInput::class.java), MAPPER.readValue("{\"kind\":\"uri\",\"uri\":\"/nonexistent/missing.pdf\"}", ExtractInput::class.java)), ExtractionConfig())
}

```
