```dart title="Dart"
import 'package:xberg/xberg.dart';

Future<void> main() async {
  final inputs = [
    const ExtractInput(kind: ExtractInputKind.uri, uri: 'report.pdf'),
    const ExtractInput(kind: ExtractInputKind.uri, uri: 'notes.txt'),
  ];

  final output = await XbergBridge.extractBatch(inputs);
  for (final result in output.results) {
    print(result.content);
  }
}
```
