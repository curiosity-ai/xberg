---
id: fixture_swift_register_tokenizer_backend_trait_bridge
language: swift
target: swift
level: typecheck
requires: []
side_effect: safe
---

register_tokenizer_backend: trait bridge

```swift title="Swift"
import Xberg

class TestStubRegisterTokenizerBackendTraitBridge: SwiftTokenizerBackendBridge {
    var name: String { "register_tokenizer_backend_trait_bridge" }
    func version() -> String { "1.0.0" }
    func initialize() throws {}
    func shutdown() throws {}
    func countTokens(text: String) -> UInt { 3 }
}

try Xberg.registerTokenizerBackend(TestStubRegisterTokenizerBackendTraitBridge())
try? Xberg.unregisterTokenizerBackend("register_tokenizer_backend_trait_bridge")

```
