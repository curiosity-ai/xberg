---
id: fixture_swift_api_extract_batch_uri_with_config
language: swift
target: swift
level: typecheck
requires: []
side_effect: server
---

Tests batch URI extraction with per-input config (extract_batch)

```swift title="Swift"
import Xberg

let _item_inputsArray_0 = try Xberg.extractInputFromJson("{\"config\":{\"output_format\":\"markdown\"},\"kind\":\"uri\",\"uri\":\"https://example.com/pdf/fake_memo.pdf\"}")
let inputsArray = [_item_inputsArray_0]
let configObj = try Xberg.extractionConfigFromJson("{}")
_ = try await Xberg.extractBatch(inputs: inputsArray, config: configObj)

```
