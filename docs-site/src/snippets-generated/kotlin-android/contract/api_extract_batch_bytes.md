---
id: fixture_kotlin_android_api_extract_batch_bytes
language: kotlin
target: kotlin_android
level: typecheck
requires: []
side_effect: safe
---

Tests batch bytes extraction API (extract_batch)

```kotlin title="Kotlin (Android)"
import io.xberg.*

fun main() = kotlinx.coroutines.runBlocking {
    val result = Xberg.extractBatch(listOf(MAPPER.readValue("{\"bytes\":\"test_documents/pdf/fake_memo.pdf\",\"filename\":\"fake_memo.pdf\",\"kind\":\"bytes\"}", ExtractInput::class.java)), ExtractionConfig())
}

```
