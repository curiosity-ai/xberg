---
id: fixture_swift_url_remote_text_document
language: swift
target: swift
level: typecheck
requires: []
side_effect: server
---

extract: remote text document URL

```swift title="Swift"
import Xberg

_ = try await Xberg.extract("{\"kind\":\"uri\",\"uri\":\"https://example.com\"}", "{\"url\":{\"mode\":\"document\"}}")

```
