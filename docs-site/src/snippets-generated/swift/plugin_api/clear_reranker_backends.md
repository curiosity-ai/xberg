---
id: fixture_swift_clear_reranker_backends
language: swift
target: swift
level: typecheck
requires: []
side_effect: safe
---

Clear all reranker backends and verify list is empty

```swift title="Swift"
import Xberg

try Xberg.clearRerankerBackends()

```
