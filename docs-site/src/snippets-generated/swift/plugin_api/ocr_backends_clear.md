---
id: fixture_swift_ocr_backends_clear
language: swift
target: swift
level: typecheck
requires: []
side_effect: safe
---

Clear all OCR backends and verify list is empty

```swift title="Swift"
import Xberg

try Xberg.clearOcrBackends()

```
