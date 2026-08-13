---
id: fixture_zig_post_processors_list
language: zig
target: zig
level: typecheck
requires: []
side_effect: safe
---

List all registered post-processors

```zig title="Zig"
const std = @import("std");
const xberg = @import("xberg");

pub fn main() !void {
    _ = try xberg.list_post_processors();
}

```
