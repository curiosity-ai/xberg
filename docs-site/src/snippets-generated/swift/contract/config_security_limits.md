---
id: fixture_swift_config_security_limits
language: swift
target: swift
level: typecheck
requires: []
side_effect: server
---

Tests archive extraction with custom security limits

```swift title="Swift"
import Xberg

_ = try await Xberg.extract("{\"kind\":\"uri\",\"uri\":\"https://example.com/archives/documents.zip\"}", "{\"security_limits\":{\"max_archive_size\":104857600,\"max_compression_ratio\":50,\"max_files_in_archive\":100}}")

```
