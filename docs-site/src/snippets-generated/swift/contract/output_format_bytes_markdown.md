---
id: fixture_swift_output_format_bytes_markdown
language: swift
target: swift
level: typecheck
requires: []
side_effect: safe
---

Tests markdown output format via bytes extraction API

```swift title="Swift"
import Xberg

_ = try await Xberg.extract("{\"bytes\":\"test_documents/pdf/fake_memo.pdf\",\"config\":{\"output_format\":\"markdown\"},\"filename\":\"fake_memo.pdf\",\"kind\":\"bytes\",\"mime_type\":\"application/pdf\"}", "{\"output_format\":\"markdown\"}")

```
