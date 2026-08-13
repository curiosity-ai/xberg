---
id: fixture_swift_format_pdf_text
language: swift
target: swift
level: typecheck
requires: []
side_effect: server
---

Standalone PDF text extraction using extract

```swift title="Swift"
import Xberg

_ = try await Xberg.extract("{\"filename\":\"fake_memo.pdf\",\"kind\":\"uri\",\"mime_type\":\"application/pdf\",\"uri\":\"https://example.com/pdf/fake_memo.pdf\"}", "{}")

```
