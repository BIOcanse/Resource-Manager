//! Native run callbacks use the same controller as the managed bridge. A run
//! belongs to its domain owner; no worker, cancellation source or timer is
//! created here. The owner returns only after its actual work has drained.
const t = @import("rm_control").types;

pub fn Run(comptime Runtime: type) type {
    return struct {
        runtime: *Runtime,
        block: u32,
        number: u64,
        const Self = @This();
        pub fn running(self: Self) !void {
            try self.runtime.reportRunning(self.block, self.number);
        }
        /// Call after callbacks, timers, handles and work have been released.
        /// A stop request or a control-round completion is not this boundary.
        pub fn returned(self: Self) !void {
            try self.runtime.reportReturned(self.block, self.number);
        }
    };
}

/// Owner supplies start(Run), stop(Run), complete(token), log(level, bytes),
/// itemChanged(id, key, reason), and controller() -> *Runtime. It is stable
/// until controller destruction and callback drain. Start/stop enqueue or
/// signal the owner's work; they must not wait for a control round.
pub fn callbacks(comptime Runtime: type, owner: anytype) t.Host {
    const Pointer = @TypeOf(owner);
    const Adapter = struct {
        fn from(context: ?*anyopaque) Pointer {
            return @ptrCast(@alignCast(context.?));
        }
        fn start(context: ?*anyopaque, block: u32, number: u64) callconv(.c) void {
            const target = from(context);
            target.start(@as(Run(Runtime), .{ .runtime = target.controller(), .block = block, .number = number }));
        }
        fn stop(context: ?*anyopaque, block: u32, number: u64) callconv(.c) void {
            const target = from(context);
            target.stop(@as(Run(Runtime), .{ .runtime = target.controller(), .block = block, .number = number }));
        }
        fn complete(context: ?*anyopaque, token: u64) callconv(.c) void {
            from(context).complete(token);
        }
        fn log(context: ?*anyopaque, level: u32, bytes: [*]const u8, length: usize) callconv(.c) void {
            from(context).log(level, bytes[0..length]);
        }
        fn itemChanged(context: ?*anyopaque, id: u64, key: t.ItemKeyView, why: *const t.ReasonView) callconv(.c) void {
            from(context).itemChanged(id, key.toKey(), why.toReason());
        }
    };
    return .{ .context = owner, .start = Adapter.start, .stop = Adapter.stop, .complete = Adapter.complete, .log = Adapter.log, .item_changed = Adapter.itemChanged };
}
