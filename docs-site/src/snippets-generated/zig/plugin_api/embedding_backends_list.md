---
id: fixture_zig_embedding_backends_list
language: zig
target: zig
level: typecheck
requires: []
side_effect: safe
---

List all registered embedding backends

```zig title="Zig"
const std = @import("std");
const xberg = @import("xberg");

pub fn main() !void {
    _ = try xberg.list_embedding_backends();
}

```
