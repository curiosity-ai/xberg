---
id: fixture_swift_output_format_markdown
language: swift
target: swift
level: typecheck
requires: []
side_effect: server
---

Tests Markdown output format

```swift title="Swift"
import Xberg

_ = try await Xberg.extract("{\"kind\":\"uri\",\"uri\":\"https://example.com/pdf/fake_memo.pdf\"}", "{\"output_format\":\"markdown\"}")

```
