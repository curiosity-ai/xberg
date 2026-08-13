---
id: fixture_swift_config_embedding_plugin
language: swift
target: swift
level: typecheck
requires: []
side_effect: server
---

Tests EmbeddingModelType::Plugin variant deserialization in ChunkingConfig — config accepts the plugin variant shape; actual dispatch requires a host-language backend registered via register_embedding_backend at runtime

```swift title="Swift"
import Xberg

_ = try await Xberg.extract("{\"kind\":\"uri\",\"uri\":\"https://example.com/pdf/fake_memo.pdf\"}", "{\"chunking\":{\"embedding\":{\"max_embed_duration_secs\":30,\"model\":{\"name\":\"test-plugin-backend\",\"type\":\"plugin\"},\"normalize\":true},\"max_chars\":500,\"max_overlap\":50}}")

```
