---
id: fixture_dart_register_post_processor_trait_bridge
language: dart
target: dart
level: typecheck
requires: []
side_effect: safe
---

register_post_processor: trait bridge

```dart title="Dart"
import 'package:xberg/xberg.dart';
Future<void> main() async {
  final result = await XbergBridge.registerPostProcessor(await _createTestStubRegisterPostProcessorTraitBridgeWrapper());
}

```
