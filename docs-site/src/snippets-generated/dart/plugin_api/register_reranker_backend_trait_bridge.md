---
id: fixture_dart_register_reranker_backend_trait_bridge
language: dart
target: dart
level: typecheck
requires: []
side_effect: safe
---

register_reranker_backend: trait bridge

```dart title="Dart"
import 'package:xberg/xberg.dart';
Future<void> main() async {
  final result = await XbergBridge.registerRerankerBackend(await _createTestStubRegisterRerankerBackendTraitBridgeWrapper());
}

```
