//! C2 binding, not a second Ledger. Composition supplies its existing instance,
//! operation workspace and stable area declarations. Never copy a live Context.
const std = @import("std");
const native = @import("ledger_sdk").native;

pub const Status = native.Status;
pub const Borrow = native.Borrow;
pub const End = enum(u32) { complete = 0, discard_loading = 1, abandon = 2 };

pub fn ended(status: i32) bool {
    return status == @backingInt(Status.completed) or
        status == @backingInt(Status.force_released) or
        status == @backingInt(Status.ended_cleanup_failed) or
        status == @backingInt(Status.abandoned);
}

/// One Context per supplied operation workspace, shared by all its area users.
/// It borrows the instance: neither shutdown nor area close destroys the Ledger,
/// ticks it, changes its policy, or closes another domain's registration.
pub fn Context(comptime Binding: type, comptime uid_bytes: usize) type {
    return struct {
        const Self = @This();
        pub const Area = struct { uid: [uid_bytes]u8, bytes: usize };
        pub const Begin = union(enum) { acquired: Borrow, failed: i32 };

        instance: *anyopaque,
        operations: *Binding.OperationWorkspaceType,
        table: u32,
        operation_gate: std.atomic.Value(bool) = .init(false),

        pub fn init(instance: *anyopaque, operations: *Binding.OperationWorkspaceType, table: u32) Self {
            return .{ .instance = instance, .operations = operations, .table = table };
        }

        fn lock(self: *Self) void {
            while (self.operation_gate.cmpxchgWeak(false, true, .acquire, .monotonic) != null)
                std.atomic.spinLoopHint();
        }
        fn unlock(self: *Self) void {
            self.operation_gate.store(false, .release);
        }

        /// The registering owner must serialize writers to this area. Slots do
        /// that for HTTP; domain owners retain their existing concurrency rule.
        pub fn begin(self: *Self, area: Area) Begin {
            var borrow = std.mem.zeroes(Borrow);
            var status = Binding.rm_ledger_use(self.instance, &area.uid, uid_bytes, &borrow);
            if (status == @backingInt(Status.not_in_memory) or status == @backingInt(Status.nonexistent)) {
                const target = native.Target{ .kind = 0, .table = self.table, .parent_uid = null, .parent_uid_length = 0 };
                self.lock();
                defer self.unlock();
                status = Binding.rm_ledger_push(self.instance, &target, &area.uid, uid_bytes, 1, area.bytes, self.operations, @sizeOf(Binding.OperationWorkspaceType), &borrow);
            }
            if (status != @backingInt(Status.in_memory) and status != @backingInt(Status.newly_allocated))
                return .{ .failed = status };
            if (borrow.length != area.bytes or (area.bytes != 0 and borrow.data == null)) {
                // A colliding declaration is an error, never an undersized view.
                // Abandon is a terminal end and does not force another borrower.
                const end_status = self.finish(&borrow, .abandon);
                std.debug.assert(ended(end_status));
                return .{ .failed = @backingInt(Status.input_error) };
            }
            return .{ .acquired = borrow };
        }

        /// Only the local owning use may finish this credential, after any real
        /// asynchronous IO using its bytes has completed/cancelled and drained.
        pub fn finish(self: *Self, borrow: *const Borrow, how: End) i32 {
            return Binding.rm_ledger_end_use(self.instance, &borrow.lease, @backingInt(how));
        }

        /// Called only by the registration owner after its users drain. SDK
        /// notifications must not reenter this Context's structural operations.
        pub fn close(self: *Self, area: Area) i32 {
            self.lock();
            defer self.unlock();
            return Binding.rm_ledger_close(self.instance, &area.uid, uid_bytes, self.operations, @sizeOf(Binding.OperationWorkspaceType));
        }
    };
}
