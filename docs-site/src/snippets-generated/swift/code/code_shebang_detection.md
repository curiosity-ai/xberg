---
id: fixture_swift_code_shebang_detection
language: swift
target: swift
level: typecheck
requires: []
side_effect: server
---

Test language detection from shebang line via bytes input

```swift title="Swift"
import Xberg

_ = try await Xberg.extract("{\"kind\":\"uri\",\"mime_type\":\"text/x-source-code\",\"uri\":\"https://example.com/code/script.sh\"}", "{}")

```
