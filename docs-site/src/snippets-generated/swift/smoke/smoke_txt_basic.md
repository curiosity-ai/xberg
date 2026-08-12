---
id: fixture_swift_smoke_txt_basic
language: swift
target: swift
level: typecheck
requires: []
side_effect: server
---

Smoke test: Plain text file

```swift title="Swift"
import Xberg

_ = try await Xberg.extract("{\"kind\":\"uri\",\"mime_type\":\"text/plain\",\"uri\":\"https://example.com/text/report.txt\"}", "{}")

```
