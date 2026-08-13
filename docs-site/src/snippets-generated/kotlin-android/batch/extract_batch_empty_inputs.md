---
id: fixture_kotlin_android_extract_batch_empty_inputs
language: kotlin
target: kotlin_android
level: typecheck
requires: []
side_effect: safe
---

extract_batch: empty batch

```kotlin title="Kotlin (Android)"
import io.xberg.*

fun main() = kotlinx.coroutines.runBlocking {
    val result = Xberg.extractBatch(listOf(), ExtractionConfig())
}

```
