---
id: fixture_kotlin_android_extract_batch_uri_not_found
language: kotlin
target: kotlin_android
level: typecheck
requires: []
side_effect: safe
---

extract_batch with missing URI input

```kotlin title="Kotlin (Android)"
import io.xberg.*

fun main() = kotlinx.coroutines.runBlocking {
    val result = Xberg.extractBatch(listOf(MAPPER.readValue("{\"kind\":\"uri\",\"uri\":\"/nonexistent/a.pdf\"}", ExtractInput::class.java)), ExtractionConfig())
}

```
