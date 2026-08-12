---
id: fixture_swift_extract_batch_uri_basic
language: swift
target: swift
level: typecheck
requires: []
side_effect: safe
---

extract_batch over URI inputs

```swift title="Swift"
import Xberg

let _item_inputsArray_0 = try Xberg.extractInputFromJson("{\"kind\":\"uri\",\"uri\":\"pdf/fake_memo.pdf\"}")
let _item_inputsArray_1 = try Xberg.extractInputFromJson("{\"kind\":\"uri\",\"uri\":\"text/fake_text.txt\"}")
let inputsArray = [_item_inputsArray_0, _item_inputsArray_1]
let configObj = try Xberg.extractionConfigFromJson("{}")
_ = try await Xberg.extractBatch(inputs: inputsArray, config: configObj)

```
