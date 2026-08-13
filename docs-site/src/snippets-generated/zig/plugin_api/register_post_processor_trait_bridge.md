---
id: fixture_zig_register_post_processor_trait_bridge
language: zig
target: zig
level: typecheck
requires: []
side_effect: safe
---

register_post_processor: trait bridge

```zig title="Zig"
const std = @import("std");
const xberg = @import("xberg");

pub fn main() !void {
    const TestStub_register_post_processor_trait_bridge = struct {
        pub fn process(_: *@This(), _: [*c]const u8, _: [*c]const u8) !void {}
        pub fn processing_stage(_: *@This()) [*c]const u8 { return "{}"; }
        pub fn should_process(_: *@This(), _: [*c]const u8, _: [*c]const u8) i32 { return 0; }
        pub fn estimated_duration_ms(_: *@This(), _: [*c]const u8) u64 { return 0; }
        pub fn priority(_: *@This()) i32 { return 0; }
    };
    var stub_register_post_processor_trait_bridge = TestStub_register_post_processor_trait_bridge{};
    const vtable_register_post_processor_trait_bridge = xberg.make_post_processor_vtable(TestStub_register_post_processor_trait_bridge, &stub_register_post_processor_trait_bridge);
    var out_err_register_post_processor_trait_bridge: ?[*c]u8 = null;
    _ = xberg.register_post_processor("test", vtable_register_post_processor_trait_bridge, &stub_register_post_processor_trait_bridge, @ptrCast(&out_err_register_post_processor_trait_bridge));
}

```
