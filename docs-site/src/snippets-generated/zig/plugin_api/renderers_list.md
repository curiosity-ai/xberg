---
id: fixture_zig_renderers_list
language: zig
target: zig
level: typecheck
requires: []
side_effect: safe
---

List all registered renderers

```zig title="Zig"
const std = @import("std");
const xberg = @import("xberg");

pub fn main() !void {
    _ = try xberg.list_renderers();
}

```
