---
id: fixture_swift_config_keywords
language: swift
target: swift
level: typecheck
requires: []
side_effect: server
---

Tests keyword extraction via YAKE algorithm

```swift title="Swift"
import Xberg

_ = try await Xberg.extract("{\"kind\":\"uri\",\"uri\":\"https://example.com/pdf/fake_memo.pdf\"}", "{\"keywords\":{\"algorithm\":\"yake\",\"max_keywords\":10}}")

```
