```swift title="Swift"
import Xberg

let inputs = try [
    Xberg.extractInputFromJson(#"{"kind":"uri","uri":"report.pdf"}"#),
    Xberg.extractInputFromJson(#"{"kind":"uri","uri":"notes.txt"}"#),
]
let config = try Xberg.extractionConfigFromJson("{}")
let output = try await Xberg.extractBatch(inputs: inputs, config: config)

for result in output.results() {
    print(result.content().toString())
}
```
