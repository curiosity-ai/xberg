---
id: fixture_swift_url_batch_mixed_inputs
language: swift
target: swift
level: typecheck
requires: []
side_effect: server
---

extract_batch: mixed bytes and URL inputs share one output envelope

```swift title="Swift"
import Xberg

let _item_inputsArray_0 = try Xberg.extractInputFromJson("{\"kind\":\"uri\",\"uri\":\"https://example.com\"}")
let _item_inputsArray_1 = try Xberg.extractInputFromJson("{\"bytes\":[66,97,116,99,104,32,98,121,116,101,115,32,99,111,110,116,101,110,116],\"filename\":\"inline.txt\",\"kind\":\"bytes\",\"mime_type\":\"text/plain\"}")
let inputsArray = [_item_inputsArray_0, _item_inputsArray_1]
let configObj = try Xberg.extractionConfigFromJson("{\"url\":{\"mode\":\"document\"}}")
_ = try await Xberg.extractBatch(inputs: inputsArray, config: configObj)

```
