---
id: fixture_kotlin_android_api_extract_batch_uri_with_config
language: kotlin
target: kotlin_android
level: typecheck
requires: []
side_effect: server
---

Tests batch URI extraction with per-input config (extract_batch)

```kotlin title="Kotlin (Android)"
import io.xberg.*

fun main() = kotlinx.coroutines.runBlocking {
    val result = Xberg.extractBatch(listOf(MAPPER.readValue("{\"config\":{\"output_format\":\"markdown\"},\"kind\":\"uri\",\"uri\":\"https://example.com/pdf/fake_memo.pdf\"}", ExtractInput::class.java)), ExtractionConfig())
}

```
