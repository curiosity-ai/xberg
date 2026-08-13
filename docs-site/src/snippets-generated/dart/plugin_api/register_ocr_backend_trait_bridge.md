---
id: fixture_dart_register_ocr_backend_trait_bridge
language: dart
target: dart
level: typecheck
requires: []
side_effect: safe
---

register_ocr_backend: trait bridge

```dart title="Dart"
import 'package:xberg/xberg.dart';
Future<void> main() async {
  final result = await XbergBridge.registerOcrBackend(await _createTestStubRegisterOcrBackendTraitBridgeWrapper());
}

```
