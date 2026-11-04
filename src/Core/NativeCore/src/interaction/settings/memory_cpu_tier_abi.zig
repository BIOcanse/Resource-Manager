const tier = @import("memory_cpu_tier.zig");
// Actions are separate operations of one owner, not runtime old/new selection.
pub export fn rm_memory_cpu_tier_resolve_parameters(context: ?*anyopaque, callbacks: ?*const tier.Callbacks, input: ?*const tier.Input, action: u32, output: ?*tier.Effective, parsed: ?*tier.Value) callconv(.c) i32 {
    const source = tier.Source{ .context = context, .callbacks = callbacks orelse return 1 };
    const request = input orelse return 1;
    const result = switch (action) {
        0 => blk: {
            const target = output orelse return 1;
            target.* = tier.resolve(source, request.*) catch |err| return status(err);
            break :blk @as(i32, 0);
        },
        1 => blk: {
            tier.validate(source, request.*) catch |err| return status(err);
            break :blk @as(i32, 0);
        },
        2 => blk: {
            tier.validateTable(source, request.*) catch |err| return status(err);
            break :blk @as(i32, 0);
        },
        3 => blk: {
            const target = parsed orelse return 1;
            const parsed_result = tier.parseStandalone(source) catch |err| return status(err);
            target.* = parsed_result.value;
            if (!parsed_result.accepted) return 5;
            break :blk @as(i32, 0);
        },
        else => return 1,
    };
    return result;
}
fn status(err: tier.Error) i32 {
    return switch (err) {
        error.Source => 2,
        error.InvalidTable => 3,
        error.MissingOption, error.InvalidInput => 4,
    };
}
