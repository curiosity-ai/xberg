import 'package:test/test.dart';
import 'package:xberg/xberg.dart' as xberg;

void main() {
  test('CacheStats equality holds for identical field values', () {
    // Literal-constructs the generated `CacheStats` DTO twice with identical field
    // values and compares them for equality, so a constructor that drops/renames a
    // field, or generated equality that stops being field-based, fails `dart test`
    // immediately instead of shipping green with a suite that asserts nothing about
    // the generated API. Create-only scaffold seed. ~keep
    final a = xberg.CacheStats(totalFiles: 1, totalSizeMb: 1.5, availableSpaceMb: 1.5, oldestFileAgeDays: 1.5, newestFileAgeDays: 1.5);
    final b = xberg.CacheStats(totalFiles: 1, totalSizeMb: 1.5, availableSpaceMb: 1.5, oldestFileAgeDays: 1.5, newestFileAgeDays: 1.5);
    expect(a, equals(b));
  });
}
