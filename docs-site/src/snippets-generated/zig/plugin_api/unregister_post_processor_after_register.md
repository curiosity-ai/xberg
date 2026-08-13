---
id: fixture_zig_unregister_post_processor_after_register
language: zig
target: zig
level: typecheck
requires: []
side_effect: safe
---

unregister_post_processor

```zig title="Zig"
const std = @import("std");
const xberg = @import("xberg");

pub fn main() !void {
    _ = try xberg.unregister_post_processor("test-processor");
}

```
