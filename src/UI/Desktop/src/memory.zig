//! Desktop's named reusable large workspaces. Only the Native public ABI is used.
//! Keep Owner at a stable address. Keep each Lease local, defer endUse, and
//! finish all child threads/callbacks that access its region before returning it.
const std = @import("std");
const native = @import("ledger_sdk").native;
const Binding = @import("ledger_entry").LedgerBinding;
const resources = @import("desktop_ledger_resources");

pub const Resource = resources.Resource;
pub const Status = native.Status;

pub const Owner = struct {
    state: Binding.StateType = undefined,
    instance: ?*anyopaque = null,

    /// In-place initialization: moving an initialized owner invalidates Native state.
    pub fn init(self: *Owner) !void {
        std.debug.assert(self.instance == null);
        @memset(std.mem.asBytes(&self.state), 0);
        try expect(Binding.rm_ledger_create(&self.state, @sizeOf(Binding.StateType), &self.instance), .ok);
    }

    /// Normal destruction only. Stop/join users and return every lease first.
    /// Failure leaves this owner live; do not free its storage on failure.
    pub fn deinit(self: *Owner) !void {
        var scratch: Binding.DestroyWorkspaceType = undefined;
        try expect(Binding.rm_ledger_destroy(self.instance, 0, &scratch, @sizeOf(@TypeOf(scratch))), .ok);
        self.instance = null;
    }

    /// Read an existing resource. Null means reclaimed/not yet published: reload
    /// through the existing producer. No allocation or implicit reload occurs.
    pub fn use(self: *Owner, resource: Resource) !?Lease {
        var borrow = std.mem.zeroes(native.Borrow);
        const status = Binding.rm_ledger_use_fixed(self.instance, @backingInt(resource), &borrow);
        if (status == @backingInt(Status.not_in_memory)) return null;
        try expect(status, .in_memory);
        return .{ .owner = self, .borrow = borrow, .newly_allocated = false };
    }

    /// Take a workspace, re-pushing its stable UID if reclaimed. Size is the
    /// consumer's actual type/selected capacity, never an independent default.
    /// A new region is zeroed by Ledger. Initialize it and call publish() only
    /// after success; otherwise defer endUse discards that unfinished load.
    pub fn acquire(self: *Owner, resource: Resource, allocation_bytes: usize) !Lease {
        if (try self.use(resource)) |lease| {
            if (lease.borrow.length != allocation_bytes) {
                var wrong_size = lease;
                _ = wrong_size.endUse();
                return error.WorkspaceSizeChanged;
            }
            return lease;
        }
        var scratch: Binding.OperationWorkspaceType = undefined;
        var borrow = std.mem.zeroes(native.Borrow);
        const target = native.Target{ .kind = 0, .table = 0, .parent_uid = null, .parent_uid_length = 0 };
        const uid = resources.uids[@backingInt(resource)];
        const status = Binding.rm_ledger_push(self.instance, &target, &uid, uid.len, 1, allocation_bytes, &scratch, @sizeOf(@TypeOf(scratch)), &borrow);
        if (status != @backingInt(Status.newly_allocated)) try expect(status, .in_memory);
        var lease = Lease{ .owner = self, .borrow = borrow, .newly_allocated = status == @backingInt(Status.newly_allocated) };
        if (borrow.length != allocation_bytes) {
            _ = lease.endUse();
            return error.WorkspaceSizeChanged;
        }
        return lease;
    }

    /// Call from one existing host execution path, with an existing selected
    /// cadence. No timer/thread/activity marker is created here.
    pub fn tick(self: *Owner) !native.TickResult {
        var scratch: Binding.TickWorkspaceType = undefined;
        var result = std.mem.zeroes(native.TickResult);
        try expect(Binding.rm_ledger_tick(self.instance, &scratch, @sizeOf(@TypeOf(scratch)), &result), .ok);
        return result;
    }
};

/// Owning scoped adapter. Do not copy, store in business fields, or let its
/// pointers escape. Concurrent leases protect pages, not application data races.
pub const Lease = struct {
    owner: *Owner,
    borrow: native.Borrow,
    newly_allocated: bool,
    publish_on_end: bool = false,
    ended: ?Status = null,

    pub fn bytes(self: *const Lease) []u8 {
        std.debug.assert(self.ended == null);
        const length: usize = @intCast(self.borrow.length);
        if (length == 0) return &.{};
        return self.borrow.data.?[0..length];
    }

    pub fn as(self: *const Lease, comptime T: type) *T {
        std.debug.assert(self.borrow.length >= @sizeOf(T));
        return @ptrCast(@alignCast(self.bytes().ptr));
    }

    pub fn publish(self: *Lease) void {
        std.debug.assert(self.ended == null);
        self.publish_on_end = true;
    }

    /// Terminal cleanup failures still end this hold. Native owns deferred
    /// cleanup. Invalid credentials are a programming error, never guessed ended.
    /// Idempotent for this adapter; copying it is forbidden by its scope contract.
    pub fn endUse(self: *Lease) Status {
        if (self.ended) |status| return status;
        const disposition: u32 = if (self.newly_allocated and !self.publish_on_end) 1 else 0;
        const status: Status = @fromBackingInt(@intCast(Binding.rm_ledger_end_use(self.owner.instance, &self.borrow.lease, disposition)));
        switch (status) {
            .completed, .force_released, .ended_cleanup_failed, .abandoned => {},
            else => @panic("Desktop Ledger endUse rejected valid scoped credential"),
        }
        self.ended = status;
        self.borrow.data = null;
        return status;
    }
};

fn expect(actual: i32, expected: Status) !void {
    if (actual == @backingInt(expected)) return;
    return switch (actual) {
        @backingInt(Status.loading) => error.LedgerLoading,
        @backingInt(Status.in_use) => error.LedgerInUse,
        @backingInt(Status.blocked) => error.LedgerBlocked,
        @backingInt(Status.allocation_failed) => error.OutOfMemory,
        @backingInt(Status.cleanup_pending) => error.LedgerCleanupPending,
        @backingInt(Status.specification_limit) => error.LedgerSpecificationLimit,
        else => error.LedgerUnexpectedStatus,
    };
}
