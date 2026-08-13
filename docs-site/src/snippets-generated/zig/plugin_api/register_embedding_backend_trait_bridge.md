---
id: fixture_zig_register_embedding_backend_trait_bridge
language: zig
target: zig
level: typecheck
requires: []
side_effect: safe
---

register_embedding_backend: trait bridge

```zig title="Zig"
const std = @import("std");
const xberg = @import("xberg");

pub fn main() !void {
    const TestStub_register_embedding_backend_trait_bridge = struct {
        pub fn dimensions(_: *@This()) u64 { return 0; }
        pub fn embed(_: *@This(), _: [*c]const u8) ![*c]const u8 { return ""; }
    };
    var stub_register_embedding_backend_trait_bridge = TestStub_register_embedding_backend_trait_bridge{};
    const vtable_register_embedding_backend_trait_bridge = xberg.make_embedding_backend_vtable(TestStub_register_embedding_backend_trait_bridge, &stub_register_embedding_backend_trait_bridge);
    var out_err_register_embedding_backend_trait_bridge: ?[*c]u8 = null;
    _ = xberg.register_embedding_backend("test", vtable_register_embedding_backend_trait_bridge, &stub_register_embedding_backend_trait_bridge, @ptrCast(&out_err_register_embedding_backend_trait_bridge));
}

```
