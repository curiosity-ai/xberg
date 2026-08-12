---
id: fixture_swift_format_docx_standalone
language: swift
target: swift
level: typecheck
requires: []
side_effect: server
---

Standalone DOCX extraction using extract

```swift title="Swift"
import Xberg

_ = try await Xberg.extract("{\"filename\":\"fake.docx\",\"kind\":\"uri\",\"mime_type\":\"application/vnd.openxmlformats-officedocument.wordprocessingml.document\",\"uri\":\"https://example.com/docx/fake.docx\"}", "{}")

```
