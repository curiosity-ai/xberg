```zig title="Zig"
const std = @import("std");
const xberg = @import("xberg");

pub fn main() !void {
    const inputs =
        \\[{
        \\  "kind": "uri",
        \\  "uri": "report.pdf"
        \\}, {
        \\  "kind": "uri",
        \\  "uri": "notes.txt"
        \\}]
    ;
    const output_json = try xberg.extract_batch(inputs, "{}");
    defer std.heap.c_allocator.free(output_json);

    std.debug.print("{s}\n", .{output_json});
}
```
