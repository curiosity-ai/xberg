---
id: fixture_swift_api_extract_batch_bytes
language: swift
target: swift
level: typecheck
requires: []
side_effect: safe
---

Tests batch bytes extraction API (extract_batch)

```swift title="Swift"
import Xberg

let _item_inputsArray_0 = try Xberg.extractInputFromJson("{\"bytes\":\"test_documents/pdf/fake_memo.pdf\",\"filename\":\"fake_memo.pdf\",\"kind\":\"bytes\"}")
let inputsArray = [_item_inputsArray_0]
let configObj = try Xberg.extractionConfigFromJson("{}")
_ = try await Xberg.extractBatch(inputs: inputsArray, config: configObj)

```
