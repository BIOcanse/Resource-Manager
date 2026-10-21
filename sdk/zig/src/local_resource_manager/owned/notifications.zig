//! Fixed-capacity, caller-owned notification subscriptions for one ledger table.
//! The notifier never allocates or stores notifications for later delivery.
const std = @import("std");
const sync = @import("sync.zig");

pub const max_subscriptions: usize = 64;

/// One notifier belongs to one table. The caller owns the stable record slice
/// and must keep it, the notifier, and callback contexts alive through all
/// dispatches and successful unsubscribe calls.
pub fn Notifier(comptime Uid: type) type {
    return struct {
        const Self = @This();

        pub const Handle = struct {
            slot: u32,
            generation: u64,
        };

        /// The C ABI callback has no result. Native callbacks must not unwind
        /// across this boundary. A managed callback must use a trampoline that
        /// catches its exceptions before returning here. That keeps one
        /// subscriber's failure from becoming the ledger call's result or
        /// preventing later subscribers from running.
        pub const Callback = *const fn (
            context: ?*anyopaque,
            uid: Uid,
            allocation_number: u64,
        ) callconv(.c) void;

        /// Caller-owned explicit metadata. A record is reusable only after
        /// unsubscribe has stopped new reservations and all old reservations
        /// have completed.
        pub const Record = struct {
            generation: u64 = 0,
            in_flight: u64 = 0,
            callback: ?Callback = null,
            context: ?*anyopaque = null,
            active: bool = false,
            retiring: bool = false,
        };

        const Snapshot = struct {
            slot: u32,
            generation: u64,
            callback: Callback,
            context: ?*anyopaque,
        };

        records: []Record,
        lock: sync.Lock = .{},

        /// Initializes caller-provided storage before this notifier is shared.
        /// An empty slice is valid and represents a table with no subscription
        /// capacity. The record slice must contain at most 64 entries.
        pub fn init(records: []Record) error{InvalidCapacity}!Self {
            if (records.len > max_subscriptions) return error.InvalidCapacity;
            for (records) |*record| record.* = .{};
            return .{ .records = records };
        }

        /// A snapshot query for owners sizing pre-commit notification workspace.
        /// Subscription changes should be serialized with the owning table's
        /// structural mutations when this result controls whether that
        /// workspace is needed.
        pub fn hasSubscribers(self: *Self) bool {
            self.lock.lock();
            defer self.lock.unlock();
            for (self.records) |record| {
                if (record.active) return true;
            }
            return false;
        }

        /// Uses the first reusable record. Generations start at one and never
        /// wrap; an exhausted record stays unavailable for the notifier's life.
        pub fn subscribe(
            self: *Self,
            callback: Callback,
            context: ?*anyopaque,
        ) error{SpecificationLimit}!Handle {
            self.lock.lock();
            defer self.lock.unlock();

            for (self.records, 0..) |*record, slot| {
                if (record.active or record.retiring or record.generation == std.math.maxInt(u64)) continue;
                record.generation += 1;
                record.callback = callback;
                record.context = context;
                record.in_flight = 0;
                record.active = true;
                return .{ .slot = @intCast(slot), .generation = record.generation };
            }
            return error.SpecificationLimit;
        }

        /// Stops new dispatch reservations, then waits without holding the
        /// notifier lock until every callback already reserved for this handle
        /// has returned. Call only after releasing table and resource locks.
        /// Callbacks must not reenter the ledger, including by unsubscribing.
        pub fn unsubscribe(self: *Self, handle: Handle) error{NonexistentHandle}!void {
            const slot: usize = @intCast(handle.slot);
            if (slot >= self.records.len) return error.NonexistentHandle;

            self.lock.lock();
            const record = &self.records[slot];
            if (record.generation != handle.generation or !record.active) {
                self.lock.unlock();
                return error.NonexistentHandle;
            }
            record.active = false;
            record.retiring = true;
            self.lock.unlock();

            while (true) {
                self.lock.lock();
                const retiring_record = &self.records[slot];
                std.debug.assert(retiring_record.generation == handle.generation);
                std.debug.assert(retiring_record.retiring and !retiring_record.active);
                if (retiring_record.in_flight == 0) {
                    retiring_record.callback = null;
                    retiring_record.context = null;
                    retiring_record.retiring = false;
                    self.lock.unlock();
                    return;
                }
                self.lock.unlock();
                std.Thread.yield() catch {};
            }
        }

        /// Reserves all currently active subscribers in one locked snapshot,
        /// then invokes them synchronously on this thread with no notifier
        /// lock held. If there are no subscribers, this returns without making
        /// a snapshot or saving a notification. The owner must call dispatch
        /// after table/resource locks are released and pass identity saved
        /// before the resource slot can be reused.
        pub fn dispatch(self: *Self, uid: Uid, allocation_number: u64) void {
            self.lock.lock();
            var has_active = false;
            for (self.records) |record| {
                if (record.active) {
                    has_active = true;
                    std.debug.assert(record.in_flight < std.math.maxInt(u64));
                }
            }
            if (!has_active) {
                self.lock.unlock();
                return;
            }

            var snapshots: [max_subscriptions]Snapshot = undefined;
            var snapshot_count: usize = 0;
            for (self.records, 0..) |*record, slot| {
                if (!record.active) continue;
                const callback = record.callback orelse unreachable;
                record.in_flight += 1;
                snapshots[snapshot_count] = .{
                    .slot = @intCast(slot),
                    .generation = record.generation,
                    .callback = callback,
                    .context = record.context,
                };
                snapshot_count += 1;
            }
            self.lock.unlock();

            for (snapshots[0..snapshot_count]) |snapshot| {
                self.invoke(snapshot, uid, allocation_number);
            }
        }

        fn invoke(self: *Self, snapshot: Snapshot, uid: Uid, allocation_number: u64) void {
            defer self.release(snapshot);
            snapshot.callback(snapshot.context, uid, allocation_number);
        }

        fn release(self: *Self, snapshot: Snapshot) void {
            self.lock.lock();
            defer self.lock.unlock();
            const record = &self.records[@as(usize, snapshot.slot)];
            std.debug.assert(record.generation == snapshot.generation);
            std.debug.assert(record.in_flight != 0);
            record.in_flight -= 1;
        }
    };
}

const TestNotifier = Notifier(u64);

const CallbackState = struct {
    calls: std.atomic.Value(u32) = std.atomic.Value(u32).init(0),
    uid: u64 = 0,
    allocation_number: u64 = 0,
};

fn recordCallback(context: ?*anyopaque, uid: u64, allocation_number: u64) callconv(.c) void {
    const state: *CallbackState = @ptrCast(@alignCast(context orelse unreachable));
    state.uid = uid;
    state.allocation_number = allocation_number;
    _ = state.calls.fetchAdd(1, .release);
}

test "dispatch synchronously delivers copied identity to every active subscriber" {
    var records: [2]TestNotifier.Record = .{ .{}, .{} };
    var notifier = try TestNotifier.init(&records);
    var first = CallbackState{};
    var second = CallbackState{};

    const first_handle = try notifier.subscribe(recordCallback, &first);
    const second_handle = try notifier.subscribe(recordCallback, &second);
    try std.testing.expect(notifier.hasSubscribers());

    notifier.dispatch(0x1122, 37);

    try std.testing.expectEqual(@as(u32, 1), first.calls.load(.acquire));
    try std.testing.expectEqual(@as(u32, 1), second.calls.load(.acquire));
    try std.testing.expectEqual(@as(u64, 0x1122), first.uid);
    try std.testing.expectEqual(@as(u64, 37), first.allocation_number);
    try std.testing.expectEqual(@as(u64, 0x1122), second.uid);
    try std.testing.expectEqual(@as(u64, 37), second.allocation_number);

    try notifier.unsubscribe(first_handle);
    try notifier.unsubscribe(second_handle);
    try std.testing.expect(!notifier.hasSubscribers());
}

test "capacity exhaustion and stale handles cannot affect a reused record" {
    var records: [1]TestNotifier.Record = .{.{}};
    var notifier = try TestNotifier.init(&records);
    var state = CallbackState{};

    const old_handle = try notifier.subscribe(recordCallback, &state);
    try std.testing.expectError(error.SpecificationLimit, notifier.subscribe(recordCallback, &state));
    notifier.dispatch(9, 4);
    try notifier.unsubscribe(old_handle);
    try std.testing.expectError(error.NonexistentHandle, notifier.unsubscribe(old_handle));
    notifier.dispatch(9, 4);
    try std.testing.expectEqual(@as(u32, 1), state.calls.load(.acquire));

    const new_handle = try notifier.subscribe(recordCallback, &state);
    try std.testing.expectEqual(old_handle.slot, new_handle.slot);
    try std.testing.expectEqual(old_handle.generation + 1, new_handle.generation);
    try std.testing.expectError(error.NonexistentHandle, notifier.unsubscribe(old_handle));
    notifier.dispatch(10, 5);
    try std.testing.expectEqual(@as(u32, 2), state.calls.load(.acquire));
    try notifier.unsubscribe(new_handle);
}

test "capacity and generation bounds fail without wrapping" {
    var too_many: [max_subscriptions + 1]TestNotifier.Record = undefined;
    try std.testing.expectError(error.InvalidCapacity, TestNotifier.init(&too_many));

    var records: [1]TestNotifier.Record = .{.{}};
    var notifier = try TestNotifier.init(&records);
    records[0].generation = std.math.maxInt(u64);
    var state = CallbackState{};
    try std.testing.expectError(error.SpecificationLimit, notifier.subscribe(recordCallback, &state));
    try std.testing.expect(!notifier.hasSubscribers());
}

const BlockingState = struct {
    entered: std.atomic.Value(bool) = std.atomic.Value(bool).init(false),
    release: std.atomic.Value(bool) = std.atomic.Value(bool).init(false),
    calls: std.atomic.Value(u32) = std.atomic.Value(u32).init(0),
};

fn blockingCallback(context: ?*anyopaque, _: u64, _: u64) callconv(.c) void {
    const state: *BlockingState = @ptrCast(@alignCast(context orelse unreachable));
    _ = state.calls.fetchAdd(1, .release);
    state.entered.store(true, .release);
    while (!state.release.load(.acquire)) std.atomic.spinLoopHint();
}

const DispatchThreadState = struct {
    notifier: *TestNotifier,
    done: std.atomic.Value(bool) = std.atomic.Value(bool).init(false),
};

fn dispatchThread(state: *DispatchThreadState) void {
    state.notifier.dispatch(70, 12);
    state.done.store(true, .release);
}

const UnsubscribeThreadState = struct {
    notifier: *TestNotifier,
    handle: TestNotifier.Handle,
    done: std.atomic.Value(bool) = std.atomic.Value(bool).init(false),
    failed: std.atomic.Value(bool) = std.atomic.Value(bool).init(false),
};

fn unsubscribeThread(state: *UnsubscribeThreadState) void {
    state.notifier.unsubscribe(state.handle) catch {
        state.failed.store(true, .release);
        state.done.store(true, .release);
        return;
    };
    state.done.store(true, .release);
}

const TestPollLimit: usize = 1_000_000;

fn waitForTestFlag(flag: *std.atomic.Value(bool)) bool {
    var attempts: usize = 0;
    while (!flag.load(.acquire) and attempts < TestPollLimit) : (attempts += 1) {
        std.Thread.yield() catch {};
    }
    return flag.load(.acquire);
}

fn waitForInactive(notifier: *TestNotifier, handle: TestNotifier.Handle) bool {
    var attempts: usize = 0;
    while (attempts < TestPollLimit) : (attempts += 1) {
        notifier.lock.lock();
        const record = notifier.records[@as(usize, handle.slot)];
        notifier.lock.unlock();
        if (!record.active and record.retiring) return true;
        std.Thread.yield() catch {};
    }
    return false;
}

test "concurrent unsubscribe waits for entered callback and blocks later dispatch" {
    var records: [1]TestNotifier.Record = .{.{}};
    var notifier = try TestNotifier.init(&records);
    var callback_state = BlockingState{};
    const handle = try notifier.subscribe(blockingCallback, &callback_state);

    var dispatch_state = DispatchThreadState{ .notifier = &notifier };
    var dispatcher = try std.Thread.spawn(.{}, dispatchThread, .{&dispatch_state});
    if (!waitForTestFlag(&callback_state.entered)) {
        callback_state.release.store(true, .release);
        dispatcher.join();
        return error.TestExpectedCallbackEntry;
    }

    var unsubscribe_state = UnsubscribeThreadState{ .notifier = &notifier, .handle = handle };
    var unsubscriber = std.Thread.spawn(.{}, unsubscribeThread, .{&unsubscribe_state}) catch |err| {
        callback_state.release.store(true, .release);
        dispatcher.join();
        return err;
    };

    if (!waitForInactive(&notifier, handle)) {
        callback_state.release.store(true, .release);
        dispatcher.join();
        unsubscriber.join();
        return error.TestExpectedUnsubscribeRetirement;
    }
    const returned_while_callback_running = unsubscribe_state.done.load(.acquire);
    notifier.dispatch(71, 13);
    var later = CallbackState{};
    const subscribe_during_retirement = notifier.subscribe(recordCallback, &later);
    callback_state.release.store(true, .release);
    dispatcher.join();
    unsubscriber.join();

    try std.testing.expect(!returned_while_callback_running);
    try std.testing.expect(!unsubscribe_state.failed.load(.acquire));
    try std.testing.expect(unsubscribe_state.done.load(.acquire));
    try std.testing.expect(dispatch_state.done.load(.acquire));
    try std.testing.expectEqual(@as(u32, 1), callback_state.calls.load(.acquire));

    if (subscribe_during_retirement) |unexpected_handle| {
        try notifier.unsubscribe(unexpected_handle);
        return error.TestExpectedRetiringRecordUnavailable;
    } else |err| {
        try std.testing.expect(err == error.SpecificationLimit);
    }

    notifier.dispatch(72, 14);
    try std.testing.expectEqual(@as(u32, 1), callback_state.calls.load(.acquire));
    const reused_handle = try notifier.subscribe(recordCallback, &later);
    try std.testing.expectEqual(handle.slot, reused_handle.slot);
    try std.testing.expectEqual(handle.generation + 1, reused_handle.generation);
    notifier.dispatch(73, 15);
    try std.testing.expectEqual(@as(u32, 1), callback_state.calls.load(.acquire));
    try std.testing.expectEqual(@as(u32, 1), later.calls.load(.acquire));
    try notifier.unsubscribe(reused_handle);
}
