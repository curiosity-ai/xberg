---
id: fixture_swift_extract_bytes_input
language: swift
target: swift
level: typecheck
requires: []
side_effect: safe
---

extract bytes input from PDF document

```swift title="Swift"
import Xberg

_ = try await Xberg.extract("{\"bytes\":\"test_documents/pdf/fake_memo.pdf\",\"filename\":\"fake_memo.pdf\",\"kind\":\"bytes\",\"mime_type\":\"application/pdf\"}", "{}")

```
