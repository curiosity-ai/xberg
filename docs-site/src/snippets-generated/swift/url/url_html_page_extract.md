---
id: fixture_swift_url_html_page_extract
language: swift
target: swift
level: typecheck
requires: []
side_effect: server
---

extract: website URL returns page content

```swift title="Swift"
import Xberg

_ = try await Xberg.extract("{\"kind\":\"uri\",\"uri\":\"https://example.com\"}", "{\"url\":{\"mode\":\"document\"}}")

```
