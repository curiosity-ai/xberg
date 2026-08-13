---
id: fixture_zig_list_validators
language: zig
target: zig
level: typecheck
requires: []
side_effect: safe
---

List validators

```zig title="Zig"
const std = @import("std");
const xberg = @import("xberg");

pub fn main() !void {
    _ = try xberg.list_validators();
}

```
