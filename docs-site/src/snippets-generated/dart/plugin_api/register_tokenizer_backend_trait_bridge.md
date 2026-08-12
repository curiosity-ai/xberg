---
id: fixture_dart_register_tokenizer_backend_trait_bridge
language: dart
target: dart
level: typecheck
requires: []
side_effect: safe
---

register_tokenizer_backend: trait bridge

```dart title="Dart"
import 'package:xberg/xberg.dart';
Future<void> main() async {
  final result = await XbergBridge.registerTokenizerBackend(await _createTestStubRegisterTokenizerBackendTraitBridgeWrapper());
}

```
