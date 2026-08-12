---
id: fixture_zig_unregister_reranker_backend
language: zig
target: zig
level: typecheck
requires: []
side_effect: safe
---

unregister_reranker_backend

```zig title="Zig"
const std = @import("std");
const xberg = @import("xberg");

pub fn main() !void {
    _ = try xberg.unregister_reranker_backend("test-reranker-backend");
}

```
