---
id: fixture_swift_register_post_processor_trait_bridge
language: swift
target: swift
level: typecheck
requires: []
side_effect: safe
---

register_post_processor: trait bridge

```swift title="Swift"
import Xberg

class TestStubRegisterPostProcessorTraitBridge: SwiftPostProcessorBridge {
    var name: String { "register_post_processor_trait_bridge" }
    func version() -> String { "1.0.0" }
    func initialize() throws {}
    func shutdown() throws {}
    func process(result: String, config: String) throws -> Void { () }
    func processingStage() -> String { "\"Early\"" }
}

try Xberg.registerPostProcessor(TestStubRegisterPostProcessorTraitBridge())
try? Xberg.unregisterPostProcessor("register_post_processor_trait_bridge")

```
