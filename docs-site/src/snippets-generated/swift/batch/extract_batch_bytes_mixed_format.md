---
id: fixture_swift_extract_batch_bytes_mixed_format
language: swift
target: swift
level: typecheck
requires: []
side_effect: safe
---

extract_batch: handles unsupported MIME gracefully

```swift title="Swift"
import Xberg

let _item_inputsArray_0 = try Xberg.extractInputFromJson("{\"bytes\":[80,68,70,32,112,108,97,99,101,104,111,108,100,101,114],\"kind\":\"bytes\",\"mime_type\":\"application/x-unknown\"}")
let inputsArray = [_item_inputsArray_0]
let configObj = try Xberg.extractionConfigFromJson("{}")
_ = try await Xberg.extractBatch(inputs: inputsArray, config: configObj)

```
