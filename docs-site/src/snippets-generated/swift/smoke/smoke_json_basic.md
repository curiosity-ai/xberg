---
id: fixture_swift_smoke_json_basic
language: swift
target: swift
level: typecheck
requires: []
side_effect: server
---

Smoke test: JSON file extraction

```swift title="Swift"
import Xberg

_ = try await Xberg.extract("{\"kind\":\"uri\",\"mime_type\":\"application/json\",\"uri\":\"https://example.com/json/simple.json\"}", "{}")

```
