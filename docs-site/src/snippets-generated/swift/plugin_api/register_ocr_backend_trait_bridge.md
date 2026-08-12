---
id: fixture_swift_register_ocr_backend_trait_bridge
language: swift
target: swift
level: typecheck
requires: []
side_effect: safe
---

register_ocr_backend: trait bridge

```swift title="Swift"
import Xberg

class TestStubRegisterOcrBackendTraitBridge: SwiftOcrBackendBridge {
    var name: String { "register_ocr_backend_trait_bridge" }
    func version() -> String { "1.0.0" }
    func initialize() throws {}
    func shutdown() throws {}
    func processImage(imageBytes: Data, config: String) throws -> String { "null" }
    func supportsLanguage(lang: String) -> Bool { false }
    func backendType() -> String { "\"Tesseract\"" }
}

try Xberg.registerOcrBackend(TestStubRegisterOcrBackendTraitBridge())
try? Xberg.unregisterOcrBackend("register_ocr_backend_trait_bridge")

```
