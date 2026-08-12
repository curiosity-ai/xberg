---
id: fixture_swift_register_validator_trait_bridge
language: swift
target: swift
level: typecheck
requires: []
side_effect: safe
---

register_validator: trait bridge

```swift title="Swift"
import Xberg

class TestStubRegisterValidatorTraitBridge: SwiftValidatorBridge {
    var name: String { "register_validator_trait_bridge" }
    func version() -> String { "1.0.0" }
    func initialize() throws {}
    func shutdown() throws {}
    func validate(result: String, config: String) throws -> Void { () }
}

try Xberg.registerValidator(TestStubRegisterValidatorTraitBridge())
try? Xberg.unregisterValidator("register_validator_trait_bridge")

```
