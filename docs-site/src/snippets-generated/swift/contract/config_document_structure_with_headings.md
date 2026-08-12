---
id: fixture_swift_config_document_structure_with_headings
language: swift
target: swift
level: typecheck
requires: []
side_effect: server
---

Tests document structure with DOCX heading-driven nesting

```swift title="Swift"
import Xberg

_ = try await Xberg.extract("{\"kind\":\"uri\",\"uri\":\"https://example.com/docx/fake.docx\"}", "{\"include_document_structure\":true}")

```
