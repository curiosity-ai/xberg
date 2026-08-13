---
id: fixture_zig_list_reranker_backends
language: zig
target: zig
level: typecheck
requires: []
side_effect: safe
---

List all registered reranker backends

```zig title="Zig"
const std = @import("std");
const xberg = @import("xberg");

pub fn main() !void {
    _ = try xberg.list_reranker_backends();
}

```
