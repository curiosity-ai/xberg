---
id: fixture_swift_extract_batch_uri_partial_failure
language: swift
target: swift
level: typecheck
requires: []
side_effect: safe
---

extract_batch with mixed valid and missing URI inputs

```swift title="Swift"
import Xberg

let _item_inputsArray_0 = try Xberg.extractInputFromJson("{\"kind\":\"uri\",\"uri\":\"text/plain.txt\"}")
let _item_inputsArray_1 = try Xberg.extractInputFromJson("{\"kind\":\"uri\",\"uri\":\"/nonexistent/missing.pdf\"}")
let inputsArray = [_item_inputsArray_0, _item_inputsArray_1]
let configObj = try Xberg.extractionConfigFromJson("{}")
_ = try await Xberg.extractBatch(inputs: inputsArray, config: configObj)

```
