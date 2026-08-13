---
id: fixture_swift_ocr_backends_unregister
language: swift
target: swift
level: typecheck
requires: []
side_effect: safe
---

Unregister nonexistent OCR backend gracefully

```swift title="Swift"
import Xberg

try Xberg.unregisterOcrBackend(name: "nonexistent-backend-xyz")

```
