---
id: fixture_swift_url_crawl_linked_pages
language: swift
target: swift
level: typecheck
requires: []
side_effect: server
---

extract: crawl mode follows linked pages

```swift title="Swift"
import Xberg

_ = try await Xberg.extract("{\"kind\":\"uri\",\"uri\":\"https://example.com\"}", "{\"url\":{\"crawl\":{\"max_depth\":1,\"max_pages\":4,\"respect_robots_txt\":false},\"mode\":\"crawl\"}}")

```
