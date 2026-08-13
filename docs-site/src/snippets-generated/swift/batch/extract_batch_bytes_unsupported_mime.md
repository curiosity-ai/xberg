---
id: fixture_swift_extract_batch_bytes_unsupported_mime
language: swift
target: swift
level: typecheck
requires: []
side_effect: safe
---

extract_batch with unsupported bytes MIME type

```swift title="Swift"
import Xberg

let _item_inputsArray_0 = try Xberg.extractInputFromJson("{\"bytes\":[100,97,116,97],\"kind\":\"bytes\",\"mime_type\":\"application/x-unknown\"}")
let inputsArray = [_item_inputsArray_0]
let configObj = try Xberg.extractionConfigFromJson("{}")
_ = try await Xberg.extractBatch(inputs: inputsArray, config: configObj)

```
