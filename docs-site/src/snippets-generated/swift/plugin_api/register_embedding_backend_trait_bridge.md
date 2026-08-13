---
id: fixture_swift_register_embedding_backend_trait_bridge
language: swift
target: swift
level: typecheck
requires: []
side_effect: safe
---

register_embedding_backend: trait bridge

```swift title="Swift"
import Xberg

class TestStubRegisterEmbeddingBackendTraitBridge: SwiftEmbeddingBackendBridge {
    var name: String { "register_embedding_backend_trait_bridge" }
    func version() -> String { "1.0.0" }
    func initialize() throws {}
    func shutdown() throws {}
    func dimensions() -> UInt { 768 }
    func embed(texts: [String]) throws -> [[Float]] { [] }
}

try Xberg.registerEmbeddingBackend(TestStubRegisterEmbeddingBackendTraitBridge())
try? Xberg.unregisterEmbeddingBackend("register_embedding_backend_trait_bridge")

```
