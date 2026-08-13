---
id: fixture_zig_validators_list
language: zig
target: zig
level: typecheck
requires: []
side_effect: safe
---

List all registered validators

```zig title="Zig"
const std = @import("std");
const xberg = @import("xberg");

pub fn main() !void {
    _ = try xberg.list_validators();
}

```
