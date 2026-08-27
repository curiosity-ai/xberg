```kotlin title="Kotlin"
import io.xberg.ExtractInput
import io.xberg.ExtractInputKind
import io.xberg.ExtractionConfig
import io.xberg.Xberg

val inputs = listOf(
    ExtractInput(kind = ExtractInputKind.URI, uri = "report.pdf"),
    ExtractInput(kind = ExtractInputKind.URI, uri = "notes.txt"),
)

val output = Xberg.extractBatch(inputs, ExtractionConfig())
output.results.forEach { println(it.content) }
```
