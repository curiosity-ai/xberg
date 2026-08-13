---
id: fixture_zig_tokenizer_backends_clear
language: zig
target: zig
level: typecheck
requires: []
side_effect: safe
---

Clear all tokenizer backends and verify list is empty

```zig title="Zig"
const std = @import("std");
const xberg = @import("xberg");

pub fn main() !void {
    _ = try xberg.clear_tokenizer_backends();
}

```
