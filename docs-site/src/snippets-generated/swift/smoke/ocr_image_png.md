---
id: fixture_swift_ocr_image_png
language: swift
target: swift
level: typecheck
requires: []
side_effect: safe
---

OCR: PNG image extraction with OCR enabled. In WASM this exercises the Uint8Array bridge parameter and Promise await in the generated OcrBackend bridge.

```swift title="Swift"
import Xberg

_ = try await Xberg.extract("{\"bytes\":\"test_documents/images/test_hello_world.png\",\"config\":{},\"filename\":\"test_hello_world.png\",\"kind\":\"bytes\",\"mime_type\":\"image/png\"}", "{}")

```
