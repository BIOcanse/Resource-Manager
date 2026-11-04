//! C7: registration and coalesced wakeups, never a payload or sampling owner.
//! The source commits its current value before publish. Consumers register
//! before their first read. A wake means only "read current again".
const std = @import("std");

pub const Wake = enum(u32) { published = 1, ended = 2 };
pub const Notify = *const fn (?*anyopaque, Wake) callconv(.c) void;
pub const Poll = enum(u32) { empty = 0, published = 1, ended = 2 };

/// Caller-owned stable storage. Keep it and the callback context alive until
/// unsubscribe returns. No work area, payload, timer or lease is retained here.
pub const Subscription = struct {
    owner: ?*Signal = null,
    previous: ?*Subscription = null,
    next: ?*Subscription = null,
    context: ?*anyopaque = null,
    notify: ?Notify = null,
    pending: bool = false,
};

/// The registration owner supplies this small control object and one node per
/// subscriber. There is no private allocator, configured duplicate capacity,
/// current-value mirror, clock or collector.
pub const Signal = struct {
    mutex: std.atomic.Mutex = .unlocked,
    first: ?*Subscription = null,
    count: usize = 0,
    ended: bool = false,

    /// Callbacks run under the registration gate, so unsubscribe is also a
    /// callback drain barrier. Callbacks must only enqueue an asynchronous wake:
    /// no reentry, user code, source reads, waits or synchronous continuation.
    pub fn subscribe(self: *Signal, node: *Subscription, context: ?*anyopaque, notify: Notify) error{AlreadyRegistered}!void {
        self.lock();
        defer self.mutex.unlock();
        if (node.owner != null) return error.AlreadyRegistered;
        node.* = .{ .owner = self, .next = self.first, .context = context, .notify = notify };
        if (self.first) |first| first.previous = node;
        self.first = node;
        self.count += 1;
        if (self.ended) notify(context, .ended);
    }

    /// Independent cancellation: no other subscriber or accepted source work
    /// is affected. The caller serializes use/disposal of its node storage.
    pub fn unsubscribe(self: *Signal, node: *Subscription) void {
        self.lock();
        defer self.mutex.unlock();
        if (node.owner != self) return;
        if (node.previous) |previous| previous.next = node.next else self.first = node.next;
        if (node.next) |next| next.previous = node.previous;
        self.count -= 1;
        node.* = .{};
    }

    /// Consume the pending edge before reading the source. A publication
    /// racing the read either precedes that read or leaves a further wake.
    pub fn poll(self: *Signal, node: *Subscription) Poll {
        self.lock();
        defer self.mutex.unlock();
        if (node.owner != self or self.ended) return .ended;
        if (!node.pending) return .empty;
        node.pending = false;
        return .published;
    }

    pub fn publish(self: *Signal) void {
        self.lock();
        defer self.mutex.unlock();
        if (self.ended) return;
        var cursor = self.first;
        var remaining = self.count;
        while (cursor != null and remaining != 0) : (remaining -= 1) {
            const node = cursor.?;
            cursor = node.next;
            if (node.pending) continue;
            node.pending = true;
            node.notify.?(node.context, .published);
        }
        std.debug.assert(cursor == null and remaining == 0);
    }

    /// The source/lifetime owner, never a waiting consumer, ends a source.
    /// End is terminal and wakes even a node with a pending publication.
    pub fn end(self: *Signal) void {
        self.lock();
        defer self.mutex.unlock();
        if (self.ended) return;
        self.ended = true;
        var cursor = self.first;
        var remaining = self.count;
        while (cursor != null and remaining != 0) : (remaining -= 1) {
            const node = cursor.?;
            cursor = node.next;
            node.pending = false;
            node.notify.?(node.context, .ended);
        }
        std.debug.assert(cursor == null and remaining == 0);
    }

    pub fn deinit(self: *Signal) void {
        // The registration owner cannot destroy storage while users remain.
        std.debug.assert(self.count == 0 and self.first == null);
        self.* = undefined;
    }

    fn lock(self: *Signal) void {
        while (!self.mutex.tryLock()) std.atomic.spinLoopHint();
    }
};
