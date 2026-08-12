---
id: fixture_zig_post_processors_clear
language: zig
target: zig
level: typecheck
requires: []
side_effect: safe
---

Clear all post-processors and verify list is empty

```zig title="Zig"
const std = @import("std");
const xberg = @import("xberg");

pub fn main() !void {
    _ = try xberg.clear_post_processors();
}

```
