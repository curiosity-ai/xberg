---
id: fixture_swift_smoke_image_png
language: swift
target: swift
level: typecheck
requires: []
side_effect: server
---

Smoke test: PNG image (without OCR, metadata only)

```swift title="Swift"
import Xberg

_ = try await Xberg.extract("{\"kind\":\"uri\",\"uri\":\"https://example.com/images/sample.png\"}", "{\"disable_ocr\":true}")

```
