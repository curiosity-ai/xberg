---
id: fixture_kotlin_android_extract_batch_bytes_unsupported_mime
language: kotlin
target: kotlin_android
level: typecheck
requires: []
side_effect: safe
---

extract_batch with unsupported bytes MIME type

```kotlin title="Kotlin (Android)"
import io.xberg.*

fun main() = kotlinx.coroutines.runBlocking {
    val result = Xberg.extractBatch(listOf(MAPPER.readValue("{\"bytes\":[100,97,116,97],\"kind\":\"bytes\",\"mime_type\":\"application/x-unknown\"}", ExtractInput::class.java)), ExtractionConfig())
}

```
