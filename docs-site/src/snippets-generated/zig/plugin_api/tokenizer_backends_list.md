---
id: fixture_zig_tokenizer_backends_list
language: zig
target: zig
level: typecheck
requires: []
side_effect: safe
---

List all registered tokenizer backends

```zig title="Zig"
const std = @import("std");
const xberg = @import("xberg");

pub fn main() !void {
    _ = try xberg.list_tokenizer_backends();
}

```
