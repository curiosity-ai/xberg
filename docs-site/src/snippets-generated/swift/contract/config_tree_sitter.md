---
id: fixture_swift_config_tree_sitter
language: swift
target: swift
level: typecheck
requires: []
side_effect: server
---

Tests tree-sitter configuration round-trip

```swift title="Swift"
import Xberg

_ = try await Xberg.extract("{\"kind\":\"uri\",\"uri\":\"https://example.com/code/hello.py\"}", "{\"tree_sitter\":{\"groups\":[\"web\"],\"languages\":[\"python\",\"rust\"],\"process\":{\"comments\":false,\"diagnostics\":false,\"docstrings\":false,\"exports\":true,\"imports\":true,\"structure\":true,\"symbols\":false}}}")

```
