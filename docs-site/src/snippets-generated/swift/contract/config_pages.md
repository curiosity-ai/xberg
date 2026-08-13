---
id: fixture_swift_config_pages
language: swift
target: swift
level: typecheck
requires: []
side_effect: server
---

Tests page extraction and page marker configuration

```swift title="Swift"
import Xberg

_ = try await Xberg.extract("{\"kind\":\"uri\",\"uri\":\"https://example.com/pdf/fake_memo.pdf\"}", "{\"pages\":{\"extract_pages\":true,\"insert_page_markers\":true}}")

```
