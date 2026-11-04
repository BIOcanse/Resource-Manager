//! A binding embedded in the source owner, never in Control's item directory.
//! The source owns immutable publications (and any payload/result handles).
//! Keep the binding at a stable address after first use. Keep publications
//! alive until publishers, callbacks and readers have drained. A read
//! borrows that lifetime; this protocol neither allocates nor retains payloads.
const std = @import("std");
const state = @import("rm_control").state;
const t = @import("rm_control").types;

pub fn Read(comptime T: type) type {
    return if (T == bool) t.Read else union(enum) {
        value: T,
        not_published,
        owner_stopped,
        read_failed,
    };
}

// C# Monitor allows a synchronous notification to publish again. Serialize
// other publishers, but preserve that reentry and revision-guarded rollback.
fn PublicationGate(comptime Runtime: type) type {
    return struct {
        mutex: @TypeOf(@as(Runtime, undefined).mutex) = .{},
        owner: std.atomic.Value(std.Thread.Id) = .init(0),
        depth: usize = 0,
        fn lock(self: *@This()) void {
            const thread = std.Thread.getCurrentId();
            if (self.owner.load(.acquire) != thread) {
                self.mutex.lock();
                self.owner.store(thread, .release);
            }
            self.depth += 1;
        }
        fn unlock(self: *@This()) void {
            self.depth -= 1;
            if (self.depth == 0) {
                self.owner.store(0, .release);
                self.mutex.unlock();
            }
        }
    };
}

pub fn Cell(comptime T: type, comptime Runtime: type) type {
    if (T == bool) return struct {
        runtime: *Runtime,
        claim: state.Claim,
        key: t.ItemKey,
        pub const Value = Read(T);
        const Self = @This();
        pub fn init(runtime: *Runtime, claim: state.Claim, key: t.ItemKey) Self {
            return .{ .runtime = runtime, .claim = claim, .key = key };
        }
        pub fn read(self: *const Self) !Value {
            return self.runtime.read(self.key);
        }
        pub fn publish(self: *Self, value: *const Value, why: t.Reason, token: ?u64) !void {
            return self.runtime.publish(self.claim, value.*, why, token);
        }
        pub fn bind(self: *Self, why: t.Reason, token: ?u64) !void {
            return self.runtime.bind(self.claim, why, token);
        }
        pub fn unbind(self: *Self, why: t.Reason, token: ?u64) !void {
            return self.runtime.unbind(self.claim, why, token);
        }
    };
    return struct {
        runtime: *Runtime,
        claim: state.Claim,
        gate: PublicationGate(Runtime) = .{},
        current: *const Value = &not_published,
        exposed: std.atomic.Value(*const Value) = .init(&not_published),
        bound: bool = true,
        revision: u64 = 0,
        pub const Value = Read(T);
        const not_published: Value = .not_published;
        const owner_stopped: Value = .owner_stopped;
        const Self = @This();
        pub fn init(runtime: *Runtime, claim: state.Claim, _: t.ItemKey) Self {
            return .{ .runtime = runtime, .claim = claim };
        }
        /// One atomic load, with no collection, locks, allocation or waiting.
        pub fn read(self: *const Self) !Value {
            return self.exposed.load(.acquire).*;
        }
        const Previous = struct { current: *const Value, exposed: *const Value, bound: bool, revision: u64 };
        fn begin(self: *Self) Previous {
            const previous: Previous = .{ .current = self.current, .exposed = self.exposed.load(.acquire), .bound = self.bound, .revision = self.revision +% 1 };
            self.revision = previous.revision;
            return previous;
        }
        fn rollback(self: *Self, previous: Previous) void {
            if (self.revision != previous.revision) return;
            self.current = previous.current;
            self.bound = previous.bound;
            self.exposed.store(previous.exposed, .release);
            self.revision +%= 1;
        }
        pub fn publish(self: *Self, value: *const Value, why: t.Reason, token: ?u64) !void {
            self.gate.lock();
            defer self.gate.unlock();
            const previous = self.begin();
            self.current = value;
            if (self.bound) self.exposed.store(value, .release);
            errdefer self.rollback(previous);
            try self.runtime.publishPresence(self.claim, switch (value.*) {
                .value => .present,
                .not_published => .not_published,
                .owner_stopped => .owner_stopped,
                .read_failed => .read_failed,
            }, why, token);
        }
        pub fn bind(self: *Self, why: t.Reason, token: ?u64) !void {
            self.gate.lock();
            defer self.gate.unlock();
            const previous = self.begin();
            self.bound = true;
            self.exposed.store(self.current, .release);
            errdefer self.rollback(previous);
            try self.runtime.bind(self.claim, why, token);
        }
        pub fn unbind(self: *Self, why: t.Reason, token: ?u64) !void {
            self.gate.lock();
            defer self.gate.unlock();
            const previous = self.begin();
            self.bound = false;
            self.exposed.store(&owner_stopped, .release);
            errdefer self.rollback(previous);
            try self.runtime.unbind(self.claim, why, token);
        }
    };
}

pub fn claimTypedCell(comptime Runtime: type, runtime: *Runtime, comptime T: type, key: t.ItemKey) !Cell(T, Runtime) {
    runtime.mutex.lock();
    defer runtime.mutex.unlock();
    const entry = &runtime.authority.entries[try runtime.authority.indexOf(key)];
    if (entry.declaration.value_type != (if (T == bool) @import("rm_control").declarations.ValueType.bool else @import("rm_control").declarations.ValueType.host)) return error.TypeMismatch;
    const capability = try runtime.authority.claimCell(key);
    return Cell(T, Runtime).init(runtime, capability, key);
}
