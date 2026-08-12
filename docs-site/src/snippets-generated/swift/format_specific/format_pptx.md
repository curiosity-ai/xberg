---
id: fixture_swift_format_pptx
language: swift
target: swift
level: typecheck
requires: []
side_effect: server
---

PPTX presentation extraction using extract

```swift title="Swift"
import Xberg

_ = try await Xberg.extract("{\"kind\":\"uri\",\"mime_type\":\"application/vnd.openxmlformats-officedocument.presentationml.presentation\",\"uri\":\"https://example.com/pptx/simple.pptx\"}", "{}")

```
