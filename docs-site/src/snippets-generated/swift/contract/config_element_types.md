---
id: fixture_swift_config_element_types
language: swift
target: swift
level: typecheck
requires: []
side_effect: server
---

Tests element-based result format with element type assertions on DOCX

```swift title="Swift"
import Xberg

_ = try await Xberg.extract("{\"kind\":\"uri\",\"uri\":\"https://example.com/docx/unit_test_headers.docx\"}", "{\"result_format\":\"element_based\"}")

```
