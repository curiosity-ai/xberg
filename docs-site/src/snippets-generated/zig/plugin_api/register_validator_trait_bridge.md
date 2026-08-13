---
id: fixture_zig_register_validator_trait_bridge
language: zig
target: zig
level: typecheck
requires: []
side_effect: safe
---

register_validator: trait bridge

```zig title="Zig"
const std = @import("std");
const xberg = @import("xberg");

pub fn main() !void {
    const TestStub_register_validator_trait_bridge = struct {
        pub fn validate(_: *@This(), _: [*c]const u8, _: [*c]const u8) !void {}
        pub fn should_validate(_: *@This(), _: [*c]const u8, _: [*c]const u8) i32 { return 0; }
        pub fn priority(_: *@This()) i32 { return 0; }
    };
    var stub_register_validator_trait_bridge = TestStub_register_validator_trait_bridge{};
    const vtable_register_validator_trait_bridge = xberg.make_validator_vtable(TestStub_register_validator_trait_bridge, &stub_register_validator_trait_bridge);
    var out_err_register_validator_trait_bridge: ?[*c]u8 = null;
    _ = xberg.register_validator("test", vtable_register_validator_trait_bridge, &stub_register_validator_trait_bridge, @ptrCast(&out_err_register_validator_trait_bridge));
}

```
