---
id: fixture_swift_url_recursive_document_urls
language: swift
target: swift
level: typecheck
requires: []
side_effect: server
---

extract: recursive URL extraction follows document links discovered in results

```swift title="Swift"
import Xberg

_ = try await Xberg.extract("{\"kind\":\"uri\",\"uri\":\"https://example.com\"}", "{\"url\":{\"crawl\":{\"document_url_depth\":1,\"follow_document_urls\":true,\"respect_robots_txt\":false},\"mode\":\"document\"}}")

```
