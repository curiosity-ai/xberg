---
id: fixture_dart_register_validator_trait_bridge
language: dart
target: dart
level: typecheck
requires: []
side_effect: safe
---

register_validator: trait bridge

```dart title="Dart"
import 'package:xberg/xberg.dart';
Future<void> main() async {
  final result = await XbergBridge.registerValidator(await _createTestStubRegisterValidatorTraitBridgeWrapper());
}

```
