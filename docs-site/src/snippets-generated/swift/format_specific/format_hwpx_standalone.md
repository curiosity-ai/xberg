---
id: fixture_swift_format_hwpx_standalone
language: swift
target: swift
level: typecheck
requires: []
side_effect: server
---

Standalone HWPX extraction using extract

```swift title="Swift"
import Xberg

_ = try await Xberg.extract("{\"filename\":\"simple.hwpx\",\"kind\":\"uri\",\"mime_type\":\"application/haansofthwpx\",\"uri\":\"https://example.com/hwpx/simple.hwpx\"}", "{}")

```
