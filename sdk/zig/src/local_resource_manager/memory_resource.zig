//! Single-row owned-memory lifecycle, composed by the ledger state owner.
//! Call only while holding the target's synchronization boundary. Addressing,
//! index publication, table accounting and the current epoch belong to that owner.
const std = @import("std");
const builtin = @import("builtin");
const memory = @import("memory_region.zig");
const registrations = @import("allocation_registration.zig");
const activity = @import("activity_window.zig");

pub const Error = memory.Error || activity.Error || error{ InvalidArgument, InvalidLease, NotRegistered, GenerationExhausted, AllocationTooSmall, CleanupPending };
pub const Retention = enum { fixed, dynamic };
/// `resource_generation` carries the owner-assigned allocation number.
pub const Lease = struct { resource_generation: u64, use_generation: u64 };
pub const Borrow = struct { lease: Lease, region: []u8 };
pub const UseResult = union(enum) { in_memory: Borrow, not_in_memory, loading };
pub const PushResult = union(enum) { resident: Borrow, fresh: Borrow, loading };
pub const EndDisposition = enum { complete, discard, abandon };
pub const EndResult = enum { completed, force_released, cleanup_failed, abandoned };
pub const CloseResult = enum { closed, in_use };
pub const UpdateResult = union(enum) { resident: []u8, registration_only, nonexistent, in_use, force_released };

pub const Resource = Configured(.{
    .width = activity.Active.width,
    .score_epochs = (activity.Policy{}).score_epochs,
    .idle_epochs = (activity.Policy{}).idle_epochs,
});

pub const Configuration = struct { width: u8, score_epochs: u8, idle_epochs: u8, development_write_watch: bool = false };

pub fn Configured(comptime config: Configuration) type {
    return WithRegion(config, memory.Region);
}

pub fn WithRegion(comptime config: Configuration, comptime Region: type) type {
    const Activity = activity.Configured(.{ .width = config.width, .score_epochs = config.score_epochs, .idle_epochs = config.idle_epochs });
    const Registration = registrations.WithRegion(config.development_write_watch, Region);
    return struct {
        const Self = @This();
        // This is the global allocation number of the current/last resident
        // allocation. The owner supplies a new number for each fresh allocation.
        generation: u64,
        retention: Retention,
        registration: ?Registration,
        // Linear lease stamps are distinct from owner-assigned allocation numbers.
        next_use_generation: u64 = 1,
        use_count: u64 = 0,
        // XOR of outstanding linear lease stamps identifies the sole holder at
        // count one, including after out-of-order returns. It is not activity.
        active_use_xor: u64 = 0,
        forced_through_use_generation: u64 = 0,
        loading: bool = false,
        activity_history: Activity.History = .{},

        pub fn fixed(allocation_bytes: u64) Error!Self {
            return .{ .generation = 0, .retention = .fixed, .registration = try Registration.fixed(allocation_bytes) };
        }

        pub fn dynamic(allocation_bytes: u64) Error!Self {
            return .{ .generation = 0, .retention = .dynamic, .registration = try Registration.dynamic(allocation_bytes) };
        }

        pub fn push(self: *Self, current_epoch: u64, allocation_number: u64) Error!PushResult {
            const registration = if (self.registration) |*value| value else return error.NotRegistered;
            if (self.discardPending()) return error.CleanupPending;
            if (self.loading) return .loading;
            try self.canAcquire();
            // Loading is also a lease: its covered rounds are active, without
            // publishing the content before the loading endUse succeeds.
            const history = if (self.use_count == 0)
                try self.activity_history.withAccess(current_epoch)
            else
                try self.historyAt(current_epoch);
            if (registration.region) |region| {
                return .{ .resident = self.acquire(region.bytes(), history) };
            }
            if (allocation_number == 0 or allocation_number <= self.generation) return error.InvalidArgument;
            const region = try registration.enterMemory();
            self.generation = allocation_number;
            const borrow = self.acquire(region, history);
            self.loading = true;
            return .{ .fresh = borrow };
        }

        pub fn use(self: *Self, current_epoch: u64) Error!UseResult {
            if (self.discardPending()) return .not_in_memory;
            const registration = self.registration orelse return .not_in_memory;
            const region = registration.region orelse return .not_in_memory;
            if (self.loading) return .loading;
            try self.canAcquire();
            const history = if (self.use_count == 0)
                try self.activity_history.withAccess(current_epoch)
            else
                try self.historyAt(current_epoch);
            return .{ .in_memory = self.acquire(region.bytes(), history) };
        }

        /// Queries project live holds through the requested round without writes.
        /// The existing last-activity epoch is also the start of any unmaterialized
        /// held interval, so no per-lease production log or extra epoch field is needed.
        pub fn historyAt(self: Self, current_epoch: u64) Error!Activity.History {
            return if (self.use_count != 0)
                self.activity_history.withContinuousUse(current_epoch)
            else blk: {
                _ = try self.activity_history.aligned(current_epoch);
                break :blk self.activity_history;
            };
        }

        pub fn update(self: *Self, allocation_bytes: u64, caller: ?Lease) Error!UpdateResult {
            if (caller) |lease| {
                if (lease.resource_generation == 0 or lease.use_generation == 0) return error.InvalidLease;
                if (lease.resource_generation != self.generation or lease.use_generation <= self.forced_through_use_generation)
                    return .force_released;
                if (lease.use_generation >= self.next_use_generation or self.use_count == 0) return error.InvalidLease;
            }
            if (caller != null and self.use_count == 1 and caller.?.use_generation != self.active_use_xor) return error.InvalidLease;
            if (self.discardPending()) return error.CleanupPending;
            const registration = if (self.registration) |*value| value else return .nonexistent;
            const new_page_bytes = try memory.requiredBytes(allocation_bytes);
            if (registration.region) |region| {
                if (allocation_bytes < region.allocation_bytes) return error.AllocationTooSmall;
                // Shared same-page growth does not invalidate any issued pointer.
                // Only a move needs the valid sole holder's explicit authorization.
                if (new_page_bytes != region.page_bytes and self.use_count != 0 and
                    (self.use_count != 1 or caller == null)) return .in_use;
            }
            const region = try registration.update(allocation_bytes);
            return if (region) |bytes| .{ .resident = bytes } else .registration_only;
        }

        /// A returned lease is linear: callers return it once. The outer lease adds
        /// manager/table/slot identity; this stamp never performs a UID lookup.
        pub fn endUse(self: *Self, lease: Lease, disposition: EndDisposition, current_epoch: u64) Error!EndResult {
            if (lease.resource_generation == 0 or lease.use_generation == 0) return error.InvalidLease;
            if (lease.resource_generation != self.generation or
                lease.use_generation <= self.forced_through_use_generation) return .force_released;
            if (self.use_count == 0 or lease.use_generation >= self.next_use_generation) return error.InvalidLease;
            if (self.use_count == 1 and lease.use_generation != self.active_use_xor) return error.InvalidLease;
            // Loading admits exactly its fresh lease. Published resources may have
            // several linear leases; callers return each once, in any order.
            if (self.loading and lease.use_generation != self.next_use_generation - 1) return error.InvalidLease;
            const history = try self.historyAt(current_epoch);
            const discard_loading = self.loading and disposition != .complete;
            self.activity_history = history;
            self.use_count -= 1;
            self.active_use_xor ^= lease.use_generation;
            // loading with no holder is precisely an unpublished discard waiting
            // for cleanup. It is not a hidden use and cannot expose failed data.
            self.loading = discard_loading;
            if (disposition == .abandon) return .abandoned;
            if (discard_loading) self.cleanupDiscard() catch return .cleanup_failed;
            return .completed;
        }

        pub fn discardPending(self: Self) bool {
            return self.loading and self.use_count == 0;
        }

        /// No lease is consumed here. A failed OS release preserves every page
        /// descriptor; an owner must prepare its index removal before this call.
        pub fn cleanupDiscard(self: *Self) Error!void {
            if (!self.discardPending()) return error.InvalidArgument;
            const registration = if (self.registration) |*value| value else return error.NotRegistered;
            try registration.unload();
            if (self.retention == .dynamic) self.registration = null;
            self.loading = false;
        }

        /// Safe unload retains a fixed declaration but removes a dynamic resource.
        /// The owner checks recoverability/impact/legal action and its pre-release
        /// decision before calling this; owning pages alone is not eligibility.
        pub fn unload(self: *Self) Error!CloseResult {
            if (self.use_count != 0) return .in_use;
            if (self.registration) |*registration| try registration.unload();
            if (self.retention == .dynamic) self.registration = null;
            self.loading = false;
            return .closed;
        }

        /// After the owner's complete eligibility/tree check, close releases the
        /// current allocation. Fixed declarations remain registered for reload.
        pub fn close(self: *Self) Error!CloseResult {
            if (self.use_count != 0) return .in_use;
            if (self.registration) |*registration| try registration.unload();
            if (self.retention == .dynamic) self.registration = null;
            self.loading = false;
            return .closed;
        }

        /// The caller must first stop all memory access. No size check or lease wait.
        pub fn forceRelease(self: *Self) Error!void {
            if (self.registration) |*registration| try registration.unload();
            if (self.use_count != 0) self.forced_through_use_generation = self.next_use_generation - 1;
            self.use_count = 0;
            self.active_use_xor = 0;
            self.loading = false;
            if (self.retention == .dynamic) self.registration = null;
        }

        fn canAcquire(self: Self) Error!void {
            if (self.next_use_generation == std.math.maxInt(u64) or self.use_count == std.math.maxInt(u64)) return error.GenerationExhausted;
        }

        fn acquire(self: *Self, region: []u8, history: ?Activity.History) Borrow {
            const generation = self.next_use_generation;
            self.next_use_generation += 1;
            self.use_count += 1;
            self.active_use_xor ^= generation;
            if (history) |access_history| self.activity_history = access_history;
            return .{ .lease = .{ .resource_generation = self.generation, .use_generation = generation }, .region = region };
        }
    };
}

test "failed discarded-load unload consumes its lease and permits cleanup retry without another end" {
    const fault = @import("owned/end_failure_pages.zig");
    const R = WithRegion(.{ .width = 8, .score_epochs = 8, .idle_epochs = 8 }, fault.Region);
    var resource = try R.dynamic(33);
    defer resource.forceRelease() catch @panic("end-contract fixture cleanup failed");
    defer fault.failures = 0;
    const loading = (try resource.push(1, 1)).fresh;
    loading.region[0] = 0x91;
    fault.failures = 1;
    try std.testing.expectEqual(.cleanup_failed, try resource.endUse(loading.lease, .discard, 4));
    try std.testing.expectEqual(@as(u64, 0), resource.use_count);
    try std.testing.expectEqual(@as(u64, 0), resource.active_use_xor);
    try std.testing.expect(resource.discardPending());
    try std.testing.expectEqual(.not_in_memory, try resource.use(4));
    try std.testing.expectError(error.InvalidLease, resource.endUse(loading.lease, .complete, 4));
    try std.testing.expectEqual(@as(u8, 0x91), resource.registration.?.region.?.bytes()[0]);
    try resource.cleanupDiscard();
    try std.testing.expect(resource.registration == null);
    try std.testing.expectEqual(@as(u64, 4), resource.activity_history.last_activity_epoch);
}

test "configured resource rows compile each bitmap width without runtime type switching" {
    if (builtin.os.tag != .windows and builtin.os.tag != .linux) return error.SkipZigTest;
    inline for (.{ 8, 16, 32, 64 }) |width| {
        const R = Configured(.{ .width = width, .score_epochs = width, .idle_epochs = width });
        var resource = try R.fixed(17);
        defer resource.forceRelease() catch @panic("configured row cleanup failed");
        const loaded = (try resource.push(1, 1)).fresh;
        try std.testing.expectEqual(@as(usize, width / 8), @sizeOf(@TypeOf(resource.activity_history.bits)));
        _ = try resource.endUse(loaded.lease, .complete, 1);
        try std.testing.expectEqual(@as(u8, 1), try resource.activity_history.score(width, .{}));
        try std.testing.expectEqual(@as(u8, 0), try resource.activity_history.score(width + 1, .{}));
        const next = (try resource.use(width + 1)).in_memory;
        try std.testing.expectEqual(@as(u8, 1), try resource.activity_history.score(width + 1, .{}));
        _ = try resource.endUse(next.lease, .complete, width + 1);
    }
}

test "owner supplies fresh allocation numbers and hits ignore unused reservations" {
    if (builtin.os.tag != .windows and builtin.os.tag != .linux) return error.SkipZigTest;
    var resource = try Resource.fixed(23);
    defer resource.forceRelease() catch @panic("memory resource cleanup failed");
    try std.testing.expectEqual(@as(u64, 0), resource.generation);
    const initial = resource;
    try std.testing.expectError(error.InvalidArgument, resource.push(1, 0));
    try std.testing.expectEqualDeep(initial, resource);

    const first = (try resource.push(1, 7)).fresh;
    try std.testing.expectEqual(@as(u64, 7), resource.generation);
    try std.testing.expectEqual(@as(u64, 7), first.lease.resource_generation);
    try std.testing.expectEqual(@as(u8, 1), try resource.activity_history.score(1, .{}));
    _ = try resource.endUse(first.lease, .complete, 1);
    const published = resource.activity_history;
    try std.testing.expectError(error.InvalidLease, resource.endUse(first.lease, .complete, 9));
    try std.testing.expectEqualDeep(published, resource.activity_history);

    const hit = (try resource.push(2, 0)).resident;
    try std.testing.expectEqual(@as(u64, 7), resource.generation);
    const hit_history = resource.activity_history;
    try std.testing.expectEqual(@as(u64, 1), published.last_activity_epoch);
    try std.testing.expectEqual(@as(u64, 2), hit_history.last_activity_epoch);
    _ = try resource.endUse(hit.lease, .discard, 2);
    try std.testing.expectEqualDeep(hit_history, resource.activity_history);

    try std.testing.expectEqual(CloseResult.closed, try resource.unload());
    const before_reused_number = resource;
    try std.testing.expectError(error.InvalidArgument, resource.push(3, 7));
    try std.testing.expectEqualDeep(before_reused_number, resource);
    const reloaded = (try resource.push(3, 8)).fresh;
    const before_stale_update = resource;
    try std.testing.expectEqual(UpdateResult.force_released, try resource.update(25, first.lease));
    try std.testing.expectEqualDeep(before_stale_update, resource);
    _ = try resource.endUse(reloaded.lease, .complete, 3);

    try std.testing.expectEqual(CloseResult.closed, try resource.close());
    try std.testing.expect(resource.registration != null);
    try std.testing.expect(resource.registration.?.region == null);
    const before_force = resource;
    try resource.forceRelease();
    try std.testing.expectEqualDeep(before_force, resource);
    try std.testing.expect(resource.registration != null);
}

test "stale update is checked before a dynamic registration that force release removed" {
    if (builtin.os.tag != .windows and builtin.os.tag != .linux) return error.SkipZigTest;
    var resource = try Resource.dynamic(11);
    defer resource.forceRelease() catch @panic("memory resource cleanup failed");
    const loading = (try resource.push(1, 5)).fresh;
    try resource.forceRelease();
    try std.testing.expect(resource.registration == null);
    const forced = resource;
    try std.testing.expectEqual(UpdateResult.force_released, try resource.update(19, loading.lease));
    try std.testing.expectEqualDeep(forced, resource);
}

test "loading holds cover every queried round and same-round acquisitions do not repeat bitmap work" {
    if (builtin.os.tag != .windows and builtin.os.tag != .linux) return error.SkipZigTest;
    var resource = try Resource.fixed(41);
    defer resource.forceRelease() catch @panic("memory resource cleanup failed");
    const policy = activity.Policy{ .score_epochs = 8, .idle_epochs = 8 };
    try std.testing.expectEqual(@as(u8, 0), try resource.activity_history.score(1, policy));
    const loaded = (try resource.push(1, 1)).fresh;
    const before_publish = resource.activity_history;
    try std.testing.expectEqual(@as(u8, 1), try before_publish.score(1, policy));
    try std.testing.expectEqual(UseResult.loading, try resource.use(20));
    try std.testing.expectEqual(PushResult.loading, try resource.push(20, 0));
    try std.testing.expectEqualDeep(before_publish, resource.activity_history);
    const projected = try resource.historyAt(20);
    try std.testing.expectEqual(@as(u8, 8), try projected.score(20, policy));
    try std.testing.expect(!try projected.idle(20, policy));
    try std.testing.expectEqualDeep(before_publish, resource.activity_history);
    try std.testing.expectEqual(CloseResult.in_use, try resource.unload());
    _ = try resource.endUse(loaded.lease, .complete, 1);
    const first_history = resource.activity_history;
    try std.testing.expectEqual(@as(u8, 1), try first_history.score(1, policy));
    try std.testing.expect(try resource.activity_history.idle(20, policy));
    try std.testing.expectEqual(CloseResult.closed, try resource.unload());
    _ = try resource.update(45, null);
    try std.testing.expectEqualDeep(first_history, resource.activity_history);

    const reloaded = (try resource.push(20, 2)).fresh;
    _ = try resource.endUse(reloaded.lease, .complete, 20);
    const hit = (try resource.push(21, 0)).resident;
    _ = try resource.endUse(hit.lease, .complete, 21);
    const borrowed = (try resource.use(23)).in_memory;
    const successful_history = resource.activity_history;
    try std.testing.expectEqual(@as(u8, 3), try successful_history.score(23, policy));
    const second = (try resource.use(23)).in_memory;
    const third = (try resource.push(23, 0)).resident;
    try std.testing.expectEqual(@as(u64, 3), resource.use_count);
    try std.testing.expectEqualDeep(successful_history, resource.activity_history);
    const grown = (try resource.update(47, borrowed.lease)).resident;
    try std.testing.expectEqual(borrowed.region.ptr, grown.ptr);
    try std.testing.expectEqualDeep(successful_history, resource.activity_history);
    try resource.forceRelease();
    try std.testing.expectEqual(EndResult.force_released, try resource.endUse(borrowed.lease, .complete, 1));
    try std.testing.expectEqual(EndResult.force_released, try resource.endUse(second.lease, .complete, 1));
    try std.testing.expectEqual(EndResult.force_released, try resource.endUse(third.lease, .complete, 1));
    try std.testing.expectEqualDeep(successful_history, resource.activity_history);
}

test "epoch validation precedes allocation and failed loads retain fixed registration without publication" {
    if (builtin.os.tag != .windows and builtin.os.tag != .linux) return error.SkipZigTest;
    var resource = try Resource.fixed(41);
    defer resource.forceRelease() catch @panic("memory resource cleanup failed");
    const initial = resource;
    try std.testing.expectError(error.InvalidEpoch, resource.push(0, 1));
    try std.testing.expectEqualDeep(initial, resource);
    const loaded = (try resource.push(20, 1)).fresh;
    _ = try resource.endUse(loaded.lease, .complete, 20);
    const published = resource;
    try std.testing.expectError(error.InvalidEpoch, resource.use(19));
    try std.testing.expectEqualDeep(published, resource);
    _ = try resource.unload();
    const absent = resource;
    try std.testing.expectError(error.InvalidEpoch, resource.push(19, 2));
    try std.testing.expectEqualDeep(absent, resource);
    const failed_load = (try resource.push(100, 2)).fresh;
    const history = resource.activity_history;
    _ = try resource.endUse(failed_load.lease, .discard, 100);
    try std.testing.expect(resource.registration != null);
    try std.testing.expect(resource.registration.?.region == null);
    try std.testing.expectEqual(@as(u64, 41), resource.registration.?.allocation_bytes);
    try std.testing.expectEqualDeep(history, resource.activity_history);
    try std.testing.expectEqual(UseResult.not_in_memory, try resource.use(101));
    try std.testing.expectEqualDeep(history, resource.activity_history);
    const reloaded = (try resource.push(101, 3)).fresh;
    try std.testing.expectEqual(@as(usize, 41), reloaded.region.len);
    _ = try resource.endUse(reloaded.lease, .complete, 101);
}

test "loading lease protects memory and discard removes failed bytes without publication" {
    if (builtin.os.tag != .windows and builtin.os.tag != .linux) return error.SkipZigTest;
    var resource = try Resource.dynamic(41);
    defer resource.forceRelease() catch @panic("memory resource cleanup failed");
    const first = (try resource.push(1, 1)).fresh;
    @memset(first.region, 0x6e);
    try std.testing.expectEqual(UseResult.loading, try resource.use(1));
    try std.testing.expectEqual(PushResult.loading, try resource.push(1, 1));
    const before = resource;
    try std.testing.expectEqual(CloseResult.in_use, try resource.close());
    try std.testing.expectEqual(CloseResult.in_use, try resource.unload());
    try std.testing.expectEqualDeep(before, resource);
    try std.testing.expectEqual(EndResult.completed, try resource.endUse(first.lease, .discard, 1));
    try std.testing.expect(resource.registration == null);
    try std.testing.expectEqual(UseResult.not_in_memory, try resource.use(1));
    try std.testing.expectEqual(UpdateResult.nonexistent, try resource.update(100, null));
    try std.testing.expectError(error.NotRegistered, resource.push(1, 1));
}

test "first completion publishes while ordinary discard is a normal lease return" {
    if (builtin.os.tag != .windows and builtin.os.tag != .linux) return error.SkipZigTest;
    const page_size = std.heap.pageSize();
    var resource = try Resource.fixed(31);
    defer resource.forceRelease() catch @panic("memory resource cleanup failed");
    const first = (try resource.push(1, 1)).fresh;
    @memset(first.region, 0x7b);
    const replacement = (try resource.update(page_size + 3, first.lease)).resident;
    try std.testing.expectEqual(@as(u64, 1), resource.use_count);
    try std.testing.expect(resource.loading);
    for (replacement[0..31]) |byte| try std.testing.expectEqual(@as(u8, 0x7b), byte);
    for (replacement[31..]) |byte| try std.testing.expectEqual(@as(u8, 0), byte);
    try std.testing.expectEqual(EndResult.completed, try resource.endUse(first.lease, .complete, 1));
    const ordinary = (try resource.use(1)).in_memory;
    const ordinary_history = resource.activity_history;
    try std.testing.expectEqual(EndResult.completed, try resource.endUse(ordinary.lease, .discard, 1));
    try std.testing.expectEqual(@as(u64, 0), resource.use_count);
    try std.testing.expectEqualDeep(ordinary_history, resource.activity_history);
    const ordinary_again = (try resource.use(1)).in_memory;
    const second = (try resource.use(1)).in_memory;
    const third = (try resource.push(1, 0)).resident;
    try std.testing.expectEqual(@as(u64, 3), resource.use_count);
    try std.testing.expectEqual(CloseResult.in_use, try resource.close());
    const shared = resource;
    try std.testing.expectError(error.AllocationTooSmall, resource.update(9, ordinary.lease));
    try std.testing.expectEqualDeep(shared, resource);
    try std.testing.expectEqual(@as(u64, 3), resource.use_count);
    try std.testing.expectEqual(EndResult.completed, try resource.endUse(ordinary_again.lease, .complete, 1));
    try std.testing.expectEqual(CloseResult.in_use, try resource.close());
    _ = try resource.endUse(third.lease, .complete, 1);
    try std.testing.expectEqual(CloseResult.in_use, try resource.unload());
    _ = try resource.update(page_size * 2 + 9, second.lease);
    _ = try resource.endUse(second.lease, .complete, 1);
    try std.testing.expectEqual(@as(u64, 0), resource.use_count);
    try std.testing.expectError(error.InvalidLease, resource.endUse(ordinary_again.lease, .complete, 1));
    const hit = (try resource.push(1, 0)).resident;
    try std.testing.expectEqual(@as(usize, page_size * 2 + 9), hit.region.len);
    _ = try resource.endUse(hit.lease, .complete, 1);
    try std.testing.expectEqual(CloseResult.closed, try resource.close());
    try std.testing.expectEqual(UpdateResult.registration_only, try resource.update(19, null));
    try std.testing.expect(resource.registration != null);
}

test "fixed safe unload retains registration, absent update is deferred and dynamic unload removes it" {
    if (builtin.os.tag != .windows and builtin.os.tag != .linux) return error.SkipZigTest;
    var fixed = try Resource.fixed(17);
    defer fixed.forceRelease() catch @panic("memory resource cleanup failed");
    const first = (try fixed.push(1, 1)).fresh;
    _ = try fixed.endUse(first.lease, .complete, 1);
    try std.testing.expectEqual(CloseResult.closed, try fixed.unload());
    try std.testing.expectEqual(UpdateResult.registration_only, try fixed.update(27, null));
    try std.testing.expectEqual(UseResult.not_in_memory, try fixed.use(1));
    const next = (try fixed.push(1, 2)).fresh;
    try std.testing.expectEqual(@as(usize, 27), next.region.len);
    for (next.region) |byte| try std.testing.expectEqual(@as(u8, 0), byte);
    _ = try fixed.endUse(next.lease, .complete, 1);
    var dynamic = try Resource.dynamic(11);
    defer dynamic.forceRelease() catch @panic("memory resource cleanup failed");
    const loaded = (try dynamic.push(1, 1)).fresh;
    _ = try dynamic.endUse(loaded.lease, .complete, 1);
    try std.testing.expectEqual(CloseResult.closed, try dynamic.unload());
    try std.testing.expectEqual(UpdateResult.nonexistent, try dynamic.update(27, null));
}

test "multiple force releases keep late leases distinct without retaining old pages or a token list" {
    if (builtin.os.tag != .windows and builtin.os.tag != .linux) return error.SkipZigTest;
    var resource = try Resource.fixed(19);
    defer resource.forceRelease() catch @panic("memory resource cleanup failed");
    var old: [8]Lease = undefined;
    for (&old, 0..) |*lease, index| {
        const loaded = (try resource.push(1, @as(u64, @intCast(index + 1)))).fresh;
        lease.* = loaded.lease;
        try resource.forceRelease();
        try std.testing.expect(resource.registration.?.region == null);
    }
    const current = (try resource.push(1, 9)).fresh;
    for (old) |lease| {
        try std.testing.expectEqual(EndResult.force_released, try resource.endUse(lease, .complete, 1));
        try std.testing.expectEqual(@as(u64, 1), resource.use_count);
        try std.testing.expect(resource.loading);
    }
    // A force-released dynamic row is then replaced in the same outer slot.
    var replaced = try Resource.dynamic(13);
    defer replaced.forceRelease() catch @panic("memory resource cleanup failed");
    const removed = (try replaced.push(1, 1)).fresh;
    try replaced.forceRelease();
    try std.testing.expect(replaced.registration == null);
    replaced = try Resource.dynamic(29);
    const other = (try replaced.push(1, 2)).fresh;
    try std.testing.expectEqual(EndResult.force_released, try replaced.endUse(removed.lease, .complete, 1));
    try std.testing.expectEqual(@as(u64, 1), replaced.use_count);
    try std.testing.expectEqual(@as(usize, 29), replaced.registration.?.region.?.bytes().len);
    _ = try replaced.endUse(other.lease, .complete, 1);
    _ = try resource.endUse(current.lease, .complete, 1);
}

test "generation exhaustion rejects acquisition before allocation or mutation" {
    if (builtin.os.tag != .windows and builtin.os.tag != .linux) return error.SkipZigTest;
    var resource = try Resource.fixed(23);
    defer resource.forceRelease() catch @panic("memory resource cleanup failed");
    resource.next_use_generation = std.math.maxInt(u64);
    const before = resource;
    try std.testing.expectError(error.GenerationExhausted, resource.push(1, 1));
    try std.testing.expectEqualDeep(before, resource);
    try std.testing.expect(resource.registration.?.region == null);
    resource.next_use_generation = 1;
    resource.use_count = std.math.maxInt(u64);
    const count_exhausted = resource;
    try std.testing.expectError(error.GenerationExhausted, resource.push(1, 1));
    try std.testing.expectEqualDeep(count_exhausted, resource);
    // Restore the diagnostic injection before cleanup.
    resource.use_count = 0;
}

test "shared resize is blocked without changing pages declaration leases or activity" {
    if (builtin.os.tag != .windows and builtin.os.tag != .linux) return error.SkipZigTest;
    var resource = try Resource.fixed(41);
    defer resource.forceRelease() catch @panic("memory resource cleanup failed");
    const loading = (try resource.push(1, 1)).fresh;
    @memset(loading.region, 0x71);
    _ = try resource.endUse(loading.lease, .complete, 1);
    const first = (try resource.use(2)).in_memory;
    const second = (try resource.push(2, 0)).resident;
    const before = resource;
    for ([_]?Lease{ null, first.lease, second.lease }) |caller| {
        try std.testing.expectEqual(UpdateResult.in_use, try resource.update(std.heap.pageSize() + 9, caller));
        try std.testing.expectEqualDeep(before, resource);
        // A rejected update must leave both old pointers valid, not just retain
        // the descriptor. These reads/writes exercise the actual OS pages.
        try std.testing.expectEqual(@as(u8, 0x71), first.region[40]);
        second.region[0] = 0x72;
        try std.testing.expectEqual(@as(u8, 0x72), first.region[0]);
    }
    _ = try resource.endUse(second.lease, .complete, 2);
    try std.testing.expectEqual(UpdateResult.in_use, try resource.update(std.heap.pageSize() + 9, null));
    _ = try resource.endUse(first.lease, .complete, 3);
    try std.testing.expectEqual(@as(u64, 0), resource.active_use_xor);
    const resized = (try resource.update(std.heap.pageSize() + 9, null)).resident;
    try std.testing.expectEqual(@as(u8, 0x72), resized[0]);
    try std.testing.expectEqual(@as(u8, 0x71), resized[40]);
    for (resized[41..]) |byte| try std.testing.expectEqual(@as(u8, 0), byte);
}

test "resident allocation cannot shrink and shared same-page growth preserves issued pointers" {
    if (builtin.os.tag != .windows and builtin.os.tag != .linux) return error.SkipZigTest;
    var resource = try Resource.fixed(41);
    defer resource.forceRelease() catch @panic("memory resource cleanup failed");
    const fresh = (try resource.push(1, 1)).fresh;
    @memset(fresh.region, 0x51);
    const initial = resource;
    try std.testing.expectError(error.AllocationTooSmall, resource.update(40, fresh.lease));
    try std.testing.expectEqualDeep(initial, resource);
    _ = try resource.endUse(fresh.lease, .complete, 1);
    const first = (try resource.use(2)).in_memory;
    const second = (try resource.use(2)).in_memory;
    const shared = resource;
    for ([_]?Lease{ null, first.lease, second.lease }) |caller| {
        try std.testing.expectError(error.AllocationTooSmall, resource.update(9, caller));
        try std.testing.expectEqualDeep(shared, resource);
    }
    const grown = (try resource.update(47, null)).resident;
    try std.testing.expectEqual(first.region.ptr, grown.ptr);
    try std.testing.expectEqual(@as(usize, 47), grown.len);
    try std.testing.expectEqual(@as(u64, 2), resource.use_count);
    try std.testing.expectEqualDeep(shared.activity_history, resource.activity_history);
    for (grown[41..]) |byte| try std.testing.expectEqual(@as(u8, 0), byte);
    second.region[40] = 0x52;
    try std.testing.expectEqual(@as(u8, 0x52), first.region[40]);
    _ = try resource.endUse(first.lease, .complete, 2);
    _ = try resource.endUse(second.lease, .complete, 2);
    const unleased = resource;
    try std.testing.expectError(error.AllocationTooSmall, resource.update(41, null));
    try std.testing.expectEqualDeep(unleased, resource);
    _ = try resource.unload();
    try std.testing.expectEqual(UpdateResult.registration_only, try resource.update(9, null));
    const reloaded = (try resource.push(3, 2)).fresh;
    try std.testing.expectEqual(@as(usize, 9), reloaded.region.len);
    _ = try resource.endUse(reloaded.lease, .complete, 3);
}

test "only the remaining linear lease can resize after arbitrary returns or force" {
    if (builtin.os.tag != .windows and builtin.os.tag != .linux) return error.SkipZigTest;
    for (0..3) |remaining| {
        var resource = try Resource.fixed(41);
        defer resource.forceRelease() catch @panic("memory resource cleanup failed");
        const loading = (try resource.push(1, 1)).fresh;
        @memset(loading.region, 0x63);
        _ = try resource.endUse(loading.lease, .complete, 1);
        const leases = [_]Lease{
            (try resource.use(2)).in_memory.lease,
            (try resource.use(2)).in_memory.lease,
            (try resource.use(2)).in_memory.lease,
        };
        _ = try resource.endUse(leases[(remaining + 1) % 3], .complete, 2);
        _ = try resource.endUse(leases[(remaining + 2) % 3], .complete, 2);
        const sole = leases[remaining];
        try std.testing.expectEqual(@as(u64, 1), resource.use_count);
        try std.testing.expectEqual(sole.use_generation, resource.active_use_xor);
        const before = resource;
        try std.testing.expectError(error.InvalidLease, resource.update(90, leases[(remaining + 1) % 3]));
        try std.testing.expectError(error.InvalidLease, resource.endUse(leases[(remaining + 1) % 3], .complete, 2));
        try std.testing.expectEqual(UpdateResult.force_released, try resource.update(90, .{ .resource_generation = resource.generation + 1, .use_generation = sole.use_generation }));
        try std.testing.expectError(error.SizeOverflow, resource.update(std.math.maxInt(u64), sole));
        try std.testing.expectEqualDeep(before, resource);
        const resized = (try resource.update(std.heap.pageSize() + 9, sole)).resident;
        try std.testing.expectEqual(@as(u8, 0x63), resized[40]);
        try std.testing.expectEqual(@as(u8, 0), resized[41]);
        try std.testing.expectEqualDeep(before.activity_history, resource.activity_history);
        _ = try resource.endUse(sole, .complete, 2);
        const unleased = resource;
        try std.testing.expectError(error.InvalidLease, resource.update(13, sole));
        try std.testing.expectEqualDeep(unleased, resource);
        const old = (try resource.use(3)).in_memory;
        try resource.forceRelease();
        try std.testing.expectEqual(@as(u64, 0), resource.active_use_xor);
        const reloaded = (try resource.push(4, 2)).fresh;
        const replacement = resource;
        try std.testing.expectEqual(UpdateResult.force_released, try resource.update(17, old.lease));
        try std.testing.expectEqual(EndResult.force_released, try resource.endUse(old.lease, .complete, 1));
        try std.testing.expectEqualDeep(replacement, resource);
        _ = try resource.update(std.heap.pageSize() * 2 + 51, reloaded.lease);
        _ = try resource.endUse(reloaded.lease, .complete, 4);
    }
}

test "force invalidates every counted lease and late returns do not decrement replacement users" {
    if (builtin.os.tag != .windows and builtin.os.tag != .linux) return error.SkipZigTest;
    var resource = try Resource.fixed(41);
    defer resource.forceRelease() catch @panic("memory resource cleanup failed");
    const loaded = (try resource.push(1, 1)).fresh;
    _ = try resource.endUse(loaded.lease, .complete, 1);
    var leases: [32]Lease = undefined;
    for (&leases, 0..) |*lease, index| {
        lease.* = if (index % 2 == 0) (try resource.use(2)).in_memory.lease else (try resource.push(2, 1)).resident.lease;
    }
    try std.testing.expectEqual(@as(u64, 32), resource.use_count);
    try std.testing.expectEqual(@as(u8, 2), try resource.activity_history.score(2, .{}));
    const original_history = resource.activity_history;
    try resource.forceRelease();
    try std.testing.expectEqual(@as(u64, 0), resource.use_count);
    try std.testing.expectEqualDeep(original_history, resource.activity_history);
    const replacement = (try resource.push(3, 2)).fresh;
    const before_returns = resource;
    for (leases) |lease| {
        try std.testing.expectEqual(EndResult.force_released, try resource.endUse(lease, .complete, 1));
        try std.testing.expectEqualDeep(before_returns, resource);
    }
    try std.testing.expectEqual(UseResult.loading, try resource.use(3));
    _ = try resource.endUse(replacement.lease, .complete, 3);
    const first = (try resource.use(3)).in_memory;
    const second = (try resource.use(3)).in_memory;
    _ = try resource.endUse(second.lease, .complete, 3);
    try std.testing.expectEqual(CloseResult.in_use, try resource.close());
    _ = try resource.endUse(first.lease, .complete, 3);
    try std.testing.expectEqual(@as(u64, 0), resource.use_count);
    try std.testing.expectEqual(CloseResult.closed, try resource.unload());
}
