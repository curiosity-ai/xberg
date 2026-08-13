---
id: fixture_swift_smoke_docx_basic
language: swift
target: swift
level: typecheck
requires: []
side_effect: server
---

Smoke test: DOCX with formatted text

```swift title="Swift"
import Xberg

_ = try await Xberg.extract("{\"kind\":\"uri\",\"mime_type\":\"application/vnd.openxmlformats-officedocument.wordprocessingml.document\",\"uri\":\"https://example.com/docx/fake.docx\"}", "{}")

```
