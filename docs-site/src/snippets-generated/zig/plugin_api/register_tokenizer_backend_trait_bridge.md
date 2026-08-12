---
id: fixture_zig_register_tokenizer_backend_trait_bridge
language: zig
target: zig
level: typecheck
requires: []
side_effect: safe
---

register_tokenizer_backend: trait bridge

```zig title="Zig"
const std = @import("std");
const xberg = @import("xberg");

pub fn main() !void {
    const TestStub_register_tokenizer_backend_trait_bridge = struct {
        pub fn count_tokens(_: *@This(), _: [*c]const u8) u64 { return 0; }
    };
    var stub_register_tokenizer_backend_trait_bridge = TestStub_register_tokenizer_backend_trait_bridge{};
    const vtable_register_tokenizer_backend_trait_bridge = xberg.make_tokenizer_backend_vtable(TestStub_register_tokenizer_backend_trait_bridge, &stub_register_tokenizer_backend_trait_bridge);
    var out_err_register_tokenizer_backend_trait_bridge: ?[*c]u8 = null;
    _ = xberg.register_tokenizer_backend("test", vtable_register_tokenizer_backend_trait_bridge, &stub_register_tokenizer_backend_trait_bridge, @ptrCast(&out_err_register_tokenizer_backend_trait_bridge));
}

```
