const declarations = @import("declarations.zig");
pub const BlockState = enum(u32) { stopped, starting, running, stopping };
pub const ItemKey = struct { item: u32, block: ?u32 = null };
pub const Reason = struct { kind: u32, owner: []const u8, cause: []const u8, plan_version: u64 };
pub const EventCause = enum { initial, item, state, permit, branch_summary, bind, unbind, shutdown };
pub const EventSource = struct { cause: EventCause = .initial, uid: u32 = 0, block: ?u32 = null };
/// Presence only: host objects stay with their host-language owner.
pub const Presence = union(enum) { present, not_published, owner_stopped, read_failed };
pub const Read = union(enum) {
    value: bool,
    not_published,
    owner_stopped,
    read_failed,
    pub fn valueOr(self: Read, fallback: bool) bool {
        return switch (self) {
            .value => |v| v,
            else => fallback,
        };
    }
};
pub const Input = struct {
    context: *anyopaque,
    block: ?u32,
    read_fn: *const fn (*anyopaque, ItemKey) anyerror!Read,
    condition_fn: *const fn (*anyopaque, u32) anyerror!bool,
    child_fn: *const fn (*anyopaque, u32) anyerror!BlockState,
    pub fn read(self: *Input, key: ItemKey) !Read {
        return self.read_fn(self.context, key);
    }
    pub fn condition(self: *Input, uid: u32) !bool {
        return self.condition_fn(self.context, uid);
    }
    pub fn childState(self: *Input, uid: u32) !BlockState {
        return self.child_fn(self.context, uid);
    }
};
pub const Compute = struct { condition: declarations.ConditionUid, function: *const fn (*Input) anyerror!bool };
/// A synchronous read of an owner's already published boolean. No sampling,
/// waiting, allocation, or owner locks are permitted in this callback.
pub const ReadView = extern struct {
    kind: u32,
    value: u32,
    pub fn toRead(self: ReadView) Read {
        return switch (self.kind) {
            0 => .not_published,
            1 => .owner_stopped,
            2 => .read_failed,
            3 => if (self.value <= 1) .{ .value = self.value != 0 } else .read_failed,
            else => .read_failed,
        };
    }
};
/// Registered once; the host retains the context until controller destruction,
/// including across unbind, so a captured read may safely finish.
pub const ReadFunction = extern struct {
    context: ?*anyopaque,
    read: *const fn (?*anyopaque, *ReadView) callconv(.c) void,
};
/// C-compatible view of an item key; block zero denotes a single-valued item.
pub const ItemKeyView = extern struct {
    item: u32,
    block: u32,
    pub fn fromKey(key: ItemKey) ItemKeyView {
        return .{ .item = key.item, .block = key.block orelse 0 };
    }
    pub fn toKey(self: ItemKeyView) ItemKey {
        return .{ .item = self.item, .block = if (self.block == 0) null else self.block };
    }
};
/// Borrowed only for the duration of an item_changed callback.
pub const ReasonView = extern struct {
    kind: u32,
    owner: [*]const u8,
    owner_length: usize,
    cause: [*]const u8,
    cause_length: usize,
    plan_version: u64,
    pub fn fromReason(reason: Reason) ReasonView {
        return .{ .kind = reason.kind, .owner = reason.owner.ptr, .owner_length = reason.owner.len, .cause = reason.cause.ptr, .cause_length = reason.cause.len, .plan_version = reason.plan_version };
    }
    pub fn toReason(self: *const ReasonView) Reason {
        return .{ .kind = self.kind, .owner = self.owner[0..self.owner_length], .cause = self.cause[0..self.cause_length], .plan_version = self.plan_version };
    }
};
pub const Host = extern struct {
    context: ?*anyopaque,
    start: *const fn (?*anyopaque, u32, u64) callconv(.c) void,
    stop: *const fn (?*anyopaque, u32, u64) callconv(.c) void,
    complete: *const fn (?*anyopaque, u64) callconv(.c) void,
    log: *const fn (?*anyopaque, u32, [*]const u8, usize) callconv(.c) void,
    /// The sixth callback kind in authority 3c; poll is added in step 6.
    item_changed: *const fn (?*anyopaque, u64, ItemKeyView, *const ReasonView) callconv(.c) void,
};
pub const Config = struct {
    worker_count: usize,
    history_capacity: usize,
    completion_capacity: usize,
    reader_capacity: usize,
    reader_bytes: usize,
    worker_arena_bytes: usize,
    /// Maximum combined owner/cause bytes retained for one decision reason.
    reason_text_capacity: usize = 256,
    shutdown_item: u32,
    host_blocks: []const u32,
};
