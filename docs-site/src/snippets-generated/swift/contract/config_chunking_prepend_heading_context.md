---
id: fixture_swift_config_chunking_prepend_heading_context
language: swift
target: swift
level: typecheck
requires: []
side_effect: server
---

Tests markdown chunker records heading hierarchy on chunk metadata

```swift title="Swift"
import Xberg

_ = try await Xberg.extract("{\"kind\":\"uri\",\"uri\":\"document.md\"}", "{\"chunking\":{\"chunker_type\":\"markdown\",\"max_characters\":500,\"overlap\":50,\"prepend_heading_context\":true}}")

```
