---
id: fixture_swift_smoke_pdf_basic
language: swift
target: swift
level: typecheck
requires: []
side_effect: server
---

Smoke test: PDF with simple text extraction

```swift title="Swift"
import Xberg

_ = try await Xberg.extract("{\"kind\":\"uri\",\"mime_type\":\"application/pdf\",\"uri\":\"https://example.com/pdf/fake_memo.pdf\"}", "{}")

```
