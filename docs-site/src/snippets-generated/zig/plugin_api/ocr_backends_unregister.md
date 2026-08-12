---
id: fixture_zig_ocr_backends_unregister
language: zig
target: zig
level: typecheck
requires: []
side_effect: safe
---

Unregister nonexistent OCR backend gracefully

```zig title="Zig"
const std = @import("std");
const xberg = @import("xberg");

pub fn main() !void {
    _ = try xberg.unregister_ocr_backend("nonexistent-backend-xyz");
}

```
