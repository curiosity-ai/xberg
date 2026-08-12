---
id: fixture_zig_list_renderers
language: zig
target: zig
level: typecheck
requires: []
side_effect: safe
---

List renderers

```zig title="Zig"
const std = @import("std");
const xberg = @import("xberg");

pub fn main() !void {
    _ = try xberg.list_renderers();
}

```
