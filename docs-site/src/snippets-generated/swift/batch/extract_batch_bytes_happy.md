---
id: fixture_swift_extract_batch_bytes_happy
language: swift
target: swift
level: typecheck
requires: []
side_effect: safe
---

Extract multiple in-memory documents in one batch.

```swift title="Swift"
import Xberg

let _item_inputsArray_0 = try Xberg.extractInputFromJson("{\"bytes\":[72,101,108,108,111,44,32,119,111,114,108,100,33],\"kind\":\"bytes\",\"mime_type\":\"text/plain\"}")
let _item_inputsArray_1 = try Xberg.extractInputFromJson("{\"bytes\":\"test_documents/html/html.html\",\"kind\":\"bytes\",\"mime_type\":\"text/html\"}")
let inputsArray = [_item_inputsArray_0, _item_inputsArray_1]
let configObj = try Xberg.extractionConfigFromJson("{}")
_ = try await Xberg.extractBatch(inputs: inputsArray, config: configObj)

```
