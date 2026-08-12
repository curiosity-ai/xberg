---
id: fixture_swift_register_reranker_backend_trait_bridge
language: swift
target: swift
level: typecheck
requires: []
side_effect: safe
---

register_reranker_backend: trait bridge

```swift title="Swift"
import Xberg

class TestStubRegisterRerankerBackendTraitBridge: SwiftRerankerBackendBridge {
    var name: String { "register_reranker_backend_trait_bridge" }
    func version() -> String { "1.0.0" }
    func initialize() throws {}
    func shutdown() throws {}
    func rerank(query: String, documents: [String]) throws -> [Float] { [] }
}

try Xberg.registerRerankerBackend(TestStubRegisterRerankerBackendTraitBridge())
try? Xberg.unregisterRerankerBackend("register_reranker_backend_trait_bridge")

```
