---
id: fixture_zig_list_post_processors
language: zig
target: zig
level: typecheck
requires: []
side_effect: safe
---

List post-processors

```zig title="Zig"
const std = @import("std");
const xberg = @import("xberg");

pub fn main() !void {
    _ = try xberg.list_post_processors();
}

```
