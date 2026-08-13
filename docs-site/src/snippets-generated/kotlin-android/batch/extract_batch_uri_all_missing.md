---
id: fixture_kotlin_android_extract_batch_uri_all_missing
language: kotlin
target: kotlin_android
level: typecheck
requires: []
side_effect: safe
---

extract_batch with missing URI inputs

```kotlin title="Kotlin (Android)"
import io.xberg.*

fun main() = kotlinx.coroutines.runBlocking {
    val result = Xberg.extractBatch(listOf(MAPPER.readValue("{\"kind\":\"uri\",\"uri\":\"/nonexistent/a.pdf\"}", ExtractInput::class.java), MAPPER.readValue("{\"kind\":\"uri\",\"uri\":\"/nonexistent/b.txt\"}", ExtractInput::class.java)), ExtractionConfig())
}

```
