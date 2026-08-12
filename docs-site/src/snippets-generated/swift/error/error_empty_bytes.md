---
id: fixture_swift_error_empty_bytes
language: swift
target: swift
level: typecheck
requires: []
side_effect: safe
---

Graceful handling of empty bytes (should not error)

```swift title="Swift"
import Xberg

_ = try await Xberg.extract("{\"bytes\":[],\"config\":{},\"filename\":\"empty.txt\",\"kind\":\"bytes\",\"mime_type\":\"text/plain\"}", "{}")

```
