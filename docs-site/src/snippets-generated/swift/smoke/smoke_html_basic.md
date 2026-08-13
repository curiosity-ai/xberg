---
id: fixture_swift_smoke_html_basic
language: swift
target: swift
level: typecheck
requires: []
side_effect: server
---

Smoke test: HTML table extraction

```swift title="Swift"
import Xberg

_ = try await Xberg.extract("{\"kind\":\"uri\",\"mime_type\":\"text/html\",\"uri\":\"https://example.com/html/simple_table.html\"}", "{}")

```
