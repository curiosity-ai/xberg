---
id: fixture_kotlin_android_extract_batch_bytes_mixed_format
language: kotlin
target: kotlin_android
level: typecheck
requires: []
side_effect: safe
---

extract_batch: handles unsupported MIME gracefully

```kotlin title="Kotlin (Android)"
import io.xberg.*

fun main() = kotlinx.coroutines.runBlocking {
    val result = Xberg.extractBatch(listOf(MAPPER.readValue("{\"bytes\":[80,68,70,32,112,108,97,99,101,104,111,108,100,101,114],\"kind\":\"bytes\",\"mime_type\":\"application/x-unknown\"}", ExtractInput::class.java)), ExtractionConfig())
}

```
