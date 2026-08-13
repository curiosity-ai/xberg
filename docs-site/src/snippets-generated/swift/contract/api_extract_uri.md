---
id: fixture_swift_api_extract_uri
language: swift
target: swift
level: typecheck
requires: []
side_effect: server
---

Tests URI extraction API

```swift title="Swift"
import Xberg

_ = try await Xberg.extract("{\"kind\":\"uri\",\"uri\":\"https://example.com/pdf/fake_memo.pdf\"}", "{}")

```
