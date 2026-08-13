---
id: fixture_kotlin_android_extract_batch_uri_basic
language: kotlin
target: kotlin_android
level: typecheck
requires: []
side_effect: safe
---

extract_batch over URI inputs

```kotlin title="Kotlin (Android)"
import io.xberg.*

fun main() = kotlinx.coroutines.runBlocking {
    val result = Xberg.extractBatch(listOf(MAPPER.readValue("{\"kind\":\"uri\",\"uri\":\"pdf/fake_memo.pdf\"}", ExtractInput::class.java), MAPPER.readValue("{\"kind\":\"uri\",\"uri\":\"text/fake_text.txt\"}", ExtractInput::class.java)), ExtractionConfig())
}

```
