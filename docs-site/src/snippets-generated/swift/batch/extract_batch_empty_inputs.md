---
id: fixture_swift_extract_batch_empty_inputs
language: swift
target: swift
level: typecheck
requires: []
side_effect: safe
---

extract_batch: empty batch

```swift title="Swift"
import Xberg

let configObj = try Xberg.extractionConfigFromJson("{}")
_ = try await Xberg.extractBatch(inputs: [], config: configObj)

```
