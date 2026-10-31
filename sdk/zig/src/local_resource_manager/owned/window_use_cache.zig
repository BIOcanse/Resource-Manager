//! One physical lifecycle borrow, many linear public leases. Identity, snapshot,
//! loading are protected by the owning row's shared/exclusive gate. Links are
//! structure metadata under table.write; shared use/end never read the links.
//! Refcount and XOR change together: separate atomic words would allow a valid
//! final return to observe an intermediate XOR and reject a live lease.
const std = @import("std");
const lifecycle = @import("../memory_resource.zig");
const sync = @import("sync.zig");
const types = @import("types.zig");

pub const Entry = struct {
    physical: ?lifecycle.Borrow = null,
    logical: std.atomic.Value(u128) = std.atomic.Value(u128).init(0),
    // Finalization is under a shared gate; a writer sees count and this epoch
    // together after taking exclusive. Delayed internal retirement must use the
    // real logical end, not the newer current epoch.
    last_end_epoch: std.atomic.Value(u64) = std.atomic.Value(u64).init(0),
    stamps: sync.Counter = .{},
    retired_through: u64 = 0,
    next: u32 = types.no_slot,
    previous: u32 = types.no_slot,
    linked: bool = false,

    pub fn count(self: *const Entry) u64 {
        return @truncate(self.logical.load(.acquire));
    }
    pub fn valid(self: *const Entry, stamp: u64) bool {
        const value = self.logical.load(.acquire);
        const refs: u64 = @truncate(value);
        const xor: u64 = @truncate(value >> 64);
        return stamp != 0 and stamp > self.retired_through and stamp < self.stamps.next.load(.monotonic) and
            refs != 0 and (refs != 1 or xor == stamp);
    }
    pub fn acquire(self: *Entry) !u64 {
        // Called only after the shared gate has protected full row identity.
        const stamp = try self.stamps.take();
        var value = self.logical.load(.monotonic);
        while (true) {
            const refs: u64 = @truncate(value);
            if (refs == std.math.maxInt(u64)) return error.SpecificationLimit;
            const next = ((value ^ (@as(u128, stamp) << 64)) & ~@as(u128, std.math.maxInt(u64))) | (refs + 1);
            if (self.logical.cmpxchgWeak(value, next, .acq_rel, .monotonic)) |changed| value = changed else return stamp;
        }
    }
    pub fn end(self: *Entry, stamp: u64) !void {
        if (stamp == 0 or stamp >= self.stamps.next.load(.monotonic)) return error.InvalidLease;
        if (stamp <= self.retired_through) return error.InvalidLease;
        var value = self.logical.load(.monotonic);
        while (true) {
            const refs: u64 = @truncate(value);
            const xor: u64 = @truncate(value >> 64);
            if (refs == 0 or (refs == 1 and xor != stamp)) return error.InvalidLease;
            const next = (@as(u128, xor ^ stamp) << 64) | (refs - 1);
            if (self.logical.cmpxchgWeak(value, next, .acq_rel, .monotonic)) |changed| value = changed else return;
        }
    }
    pub fn endedAt(self: *Entry, epoch: u64) void {
        var old = self.last_end_epoch.load(.monotonic);
        while (old < epoch) {
            if (self.last_end_epoch.cmpxchgWeak(old, epoch, .release, .monotonic)) |changed| old = changed else return;
        }
    }
    /// Exclusive row gate; never reset the stamp source on slot reuse.
    pub fn invalidate(self: *Entry) void {
        self.physical = null;
        self.logical.store(0, .release);
    }
    pub fn forceInvalidate(self: *Entry) void {
        // Only force can invalidate a still-live public lease. An ordinary
        // retirement must not make a duplicate normal end look force-released.
        if (self.count() != 0) self.retired_through = self.stamps.next.load(.monotonic) - 1;
        self.invalidate();
    }
};

pub fn Hints(comptime slots: u32) type {
    return struct {
        slots: [slots]std.atomic.Value(u32) = @splat(std.atomic.Value(u32).init(types.no_slot)),
        pub fn position(uid: anytype) usize {
            // A bounded hint, deliberately storing no UID and owning no identity.
            return @intCast(std.hash.Wyhash.hash(0, &uid.bytes) % slots);
        }
    };
}
