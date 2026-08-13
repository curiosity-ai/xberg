---
id: fixture_kotlin_android_api_extract_batch_uri
language: kotlin
target: kotlin_android
level: typecheck
requires: []
side_effect: server
---

Tests batch URI extraction API (extract_batch)

```kotlin title="Kotlin (Android)"
import io.xberg.*

fun main() = kotlinx.coroutines.runBlocking {
    val result = Xberg.extractBatch(listOf(MAPPER.readValue("{\"kind\":\"uri\",\"uri\":\"https://example.com/pdf/fake_memo.pdf\"}", ExtractInput::class.java)), ExtractionConfig())
}

```
