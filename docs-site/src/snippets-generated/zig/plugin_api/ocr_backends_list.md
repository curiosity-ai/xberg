---
id: fixture_zig_ocr_backends_list
language: zig
target: zig
level: typecheck
requires: []
side_effect: safe
---

List all registered OCR backends

```zig title="Zig"
const std = @import("std");
const xberg = @import("xberg");

pub fn main() !void {
    _ = try xberg.list_ocr_backends();
}

```
