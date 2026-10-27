const std = @import("std");
const d = @import("declarations.zig");
const t = @import("types.zig");
const tr = @import("tree.zig");
const state = @import("state.zig");
const demand = @import("demand.zig");
const sync = @import("sync.zig");
const reader_storage = @import("readers.zig");

pub fn Controller(comptime registry: d.Registry, comptime topology: tr.Tree) type {
    @setEvalBranchQuota(10_000_000);
    comptime tr.validate(registry, topology);
    const block_count = registry.blocks.len;
    const instance_count = tr.instanceCount(registry, topology);
    const node_count = comptime blk: {
        var n: usize = 0;
        for (topology.blocks) |b| {
            var has = b.uid == topology.program;
            for (topology.blocks) |c| {
                if (c.parent == b.uid) has = true;
            }
            for (registry.conditions) |c| {
                if (!c.per_block and c.node == b.uid) has = true;
            }
            if (has) n += 1;
        }
        break :blk n;
    };
    const host_block_uids = comptime blk: {
        var ids: [block_count - 1]u32 = undefined;
        var next: usize = 0;
        for (registry.blocks) |block| {
            if (block.uid != topology.program) {
                ids[next] = block.uid;
                next += 1;
            }
        }
        break :blk ids;
    };
    const Meta = struct { declaration: usize, node: usize, block: ?u32, bit: u6, decides: ?usize };
    const node_uids = comptime blk: {
        var ids: [node_count]u32 = undefined;
        var n: usize = 0;
        for (topology.blocks) |b| {
            var has = b.uid == topology.program;
            for (topology.blocks) |c| {
                if (c.parent == b.uid) has = true;
            }
            for (registry.conditions) |c| {
                if (!c.per_block and c.node == b.uid) has = true;
            }
            if (has) {
                ids[n] = b.uid;
                n += 1;
            }
        }
        break :blk ids;
    };
    const metadata = comptime blk: {
        @setEvalBranchQuota(10_000_000);
        var result: [instance_count]Meta = undefined;
        var n: usize = 0;
        for (node_uids, 0..) |uid, ni| {
            var bit: u7 = 0;
            var used = [_]bool{false} ** registry.conditions.len;
            for (0..registry.conditions.len) |_| {
                for (registry.conditions, 0..) |c, ci| {
                    if (used[ci] or c.per_block or c.node != uid) continue;
                    var ready = true;
                    for (c.conditions) |lower| if (!used[tr.conditionIndex(registry, lower).?]) {
                        ready = false;
                    };
                    if (!ready) continue;
                    result[n] = .{ .declaration = ci, .node = ni, .block = null, .bit = @intCast(bit), .decides = if (c.decides) |child| tr.blockIndex(registry, child).? else null };
                    n += 1;
                    bit += 1;
                    used[ci] = true;
                }
            }
            for (topology.uses) |use| {
                if (use.node != uid) continue;
                result[n] = .{ .declaration = tr.conditionIndex(registry, use.condition).?, .node = ni, .block = use.child, .bit = @intCast(bit), .decides = tr.blockIndex(registry, use.child).? };
                n += 1;
                bit += 1;
            }
        }
        break :blk result;
    };
    const NodeItem = struct { node: usize, key: t.ItemKey, bits: u64 };
    const node_items = comptime blk: {
        var maximum: usize = node_count;
        for (metadata) |m| maximum += registry.conditions[m.declaration].items.len;
        var values: [maximum]NodeItem = undefined;
        var count: usize = 0;
        for (0..node_count) |ni| {
            for (metadata) |m| {
                if (m.node != ni) continue;
                for (registry.conditions[m.declaration].items) |read_key| {
                    const key: t.ItemKey = .{ .item = read_key.item, .block = if (m.block != null and read_key.block == null and registry.items[tr.itemIndex(registry, read_key.item).?].per_block) m.block else read_key.block };
                    var found = false;
                    for (values[0..count]) |*entry| {
                        if (entry.node == ni and entry.key.item == key.item and entry.key.block == key.block) {
                            entry.bits |= @as(u64, 1) << m.bit;
                            found = true;
                        }
                    }
                    if (!found) {
                        values[count] = .{ .node = ni, .key = key, .bits = @as(u64, 1) << m.bit };
                        count += 1;
                    }
                }
            }
        }
        break :blk values[0..count].*;
    };
    const key_offsets = comptime blk: {
        var offsets: [node_count + 1]usize = @splat(0);
        for (0..node_count) |ni| {
            offsets[ni + 1] = offsets[ni];
            for (node_items) |entry| {
                if (entry.node == ni) offsets[ni + 1] += 1;
            }
        }
        break :blk offsets;
    };
    return struct {
        const Self = @This();
        pub const Transition = struct { sequence: u64, block: u32, run: u64, from: t.BlockState, to: t.BlockState };
        pub const ItemValue = struct { key: t.ItemKey, value: t.Read };
        pub const Decision = struct { sequence: u64, node: u32, should_run: u64, source: t.EventSource, reason: ?t.Reason, items: []const ItemValue };
        const Block = struct { value: t.BlockState = .stopped, run: u64 = 0 };
        const ConditionValue = struct { value: bool = false, last_error: ?anyerror = null };
        const Node = struct {
            marks: u64 = 0,
            permitted: bool = false,
            drained: bool = true,
            needs_align: bool = true,
            queued: bool = false,
            busy: bool = false,
            requested: u64 = 0,
            completed: u64 = 0,
            sequence: u64 = 0,
            retained: usize = 0,
            source: t.EventSource = .{},
            reason: ?t.Reason = null,
        };
        const Action = union(enum) { transition: Transition, start: struct { block: u32, run: u64 }, stop: struct { block: u32, run: u64 }, info: struct { uid: u32, wanted: u64, source: t.EventSource, kind: ?u32, plan: ?u64 }, failure: struct { uid: u32, err: anyerror } };
        const Worker = struct { thread: ?std.Thread = null, buffer: []u8 };
        const Completion = struct { used: bool = false, token: u64 = 0, targets: [node_count]u64 = [_]u64{0} ** node_count };
        allocator: std.mem.Allocator,
        config: t.Config,
        host: t.Host,
        mutex: sync.Mutex = .{},
        changed: sync.Condition = .{},
        stopping: bool = false,
        active: bool = false,
        blocks: [block_count]Block = @splat(.{}),
        nodes: [node_count]Node = @splat(.{}),
        conditions: [instance_count]ConditionValue = @splat(.{}),
        authority: state.State,
        demands: demand.Demands,
        shutdown_claim: state.Claim,
        demand_claims: [block_count]?state.Claim = @splat(null),
        workers: []Worker,
        transitions: []Transition,
        transition_sequence: u64 = 0,
        transition_retained: usize = 0,
        decisions: []Decision,
        current_decisions: [node_count]?Decision = [_]?Decision{null} ** node_count,
        history_items: []ItemValue,
        current_items: []ItemValue,
        pending_reason_bytes: []u8,
        history_reason_bytes: []u8,
        current_reason_bytes: []u8,
        completions: []Completion,
        readers: reader_storage.Readers,
        queue: [node_count]usize = undefined,
        queue_head: usize = 0,
        queue_len: usize = 0,
        callbacks: usize = 0,

        pub fn create(allocator: std.mem.Allocator, config: t.Config, host: t.Host) !*Self {
            const item_scratch = std.math.mul(usize, node_items.len, @sizeOf(ItemValue)) catch return error.InvalidConfiguration;
            const scratch = std.math.add(usize, @sizeOf(Action) * (block_count * 3 + instance_count + 1), item_scratch) catch return error.InvalidConfiguration;
            const child_scratch = std.math.mul(usize, block_count, @sizeOf(t.BlockState)) catch return error.InvalidConfiguration;
            const scratch_with_children = std.math.add(usize, scratch, child_scratch) catch return error.InvalidConfiguration;
            const scratch_with_reason = std.math.add(usize, scratch_with_children, config.reason_text_capacity) catch return error.InvalidConfiguration;
            const scratch_with_alignment = std.math.add(usize, scratch_with_reason, @alignOf(Action) + @alignOf(ItemValue) + @alignOf(t.BlockState)) catch return error.InvalidConfiguration;
            if (config.worker_count == 0 or config.worker_arena_bytes < scratch_with_alignment) return error.InvalidConfiguration;
            if (config.host_blocks.len != block_count - 1) return error.HostBlocksMismatch;
            for (registry.blocks) |b| {
                if (b.uid == topology.program) continue;
                var found: usize = 0;
                for (config.host_blocks) |id| {
                    if (id == b.uid) found += 1;
                }
                if (found != 1) return error.HostBlocksMismatch;
            }
            var authority = try state.State.init(allocator, registry);
            errdefer authority.deinit();
            const shutdown_claim = try authority.claimWrite(.{ .item = config.shutdown_item });
            _ = try authority.publish(shutdown_claim, .{ .value = false });
            var demands = try demand.Demands.init(allocator, config.demand_capacity, config.demand_bytes);
            errdefer demands.deinit();
            const self = try allocator.create(Self);
            errdefer allocator.destroy(self);
            const workers = try allocator.alloc(Worker, config.worker_count);
            errdefer allocator.free(workers);
            var allocated: usize = 0;
            errdefer for (workers[0..allocated]) |w| allocator.free(w.buffer);
            for (workers) |*w| {
                w.* = .{ .buffer = try allocator.alloc(u8, config.worker_arena_bytes) };
                allocated += 1;
            }
            const transitions = try allocator.alloc(Transition, config.history_capacity);
            errdefer allocator.free(transitions);
            const decision_capacity = std.math.mul(usize, config.history_capacity, node_count) catch return error.InvalidConfiguration;
            const decisions = try allocator.alloc(Decision, decision_capacity);
            errdefer allocator.free(decisions);
            const history_item_capacity = std.math.mul(usize, config.history_capacity, node_items.len) catch return error.InvalidConfiguration;
            const history_items = try allocator.alloc(ItemValue, history_item_capacity);
            errdefer allocator.free(history_items);
            const current_items = try allocator.alloc(ItemValue, node_items.len);
            errdefer allocator.free(current_items);
            const pending_reason_capacity = std.math.mul(usize, node_count, config.reason_text_capacity) catch return error.InvalidConfiguration;
            const pending_reason_bytes = try allocator.alloc(u8, pending_reason_capacity);
            errdefer allocator.free(pending_reason_bytes);
            const history_reason_capacity = std.math.mul(usize, decision_capacity, config.reason_text_capacity) catch return error.InvalidConfiguration;
            const history_reason_bytes = try allocator.alloc(u8, history_reason_capacity);
            errdefer allocator.free(history_reason_bytes);
            const current_reason_capacity = std.math.mul(usize, node_count, config.reason_text_capacity) catch return error.InvalidConfiguration;
            const current_reason_bytes = try allocator.alloc(u8, current_reason_capacity);
            errdefer allocator.free(current_reason_bytes);
            const completions = try allocator.alloc(Completion, config.completion_capacity);
            errdefer allocator.free(completions);
            for (completions) |*c| c.* = .{};
            var runtime_readers = try reader_storage.Readers.init(allocator, config.reader_capacity, config.reader_bytes);
            errdefer runtime_readers.deinit();
            self.* = .{ .allocator = allocator, .config = config, .host = host, .authority = authority, .demands = demands, .shutdown_claim = shutdown_claim, .workers = workers, .transitions = transitions, .decisions = decisions, .history_items = history_items, .current_items = current_items, .pending_reason_bytes = pending_reason_bytes, .history_reason_bytes = history_reason_bytes, .current_reason_bytes = current_reason_bytes, .completions = completions, .readers = runtime_readers };
            self.config.host_blocks = &host_block_uids;
            for (&self.nodes, 0..) |*node, ni| {
                node.permitted = node_uids[ni] == topology.program;
                node.source = .{ .cause = .initial, .uid = node_uids[ni] };
                for (metadata) |m| {
                    if (m.node == ni) node.marks |= @as(u64, 1) << m.bit;
                }
            }
            if (config.demand_item) |uid| {
                for (registry.blocks, 0..) |b, bi| {
                    const claim = try self.authority.claimCell(.{ .item = uid, .block = b.uid });
                    self.demand_claims[bi] = claim;
                    _ = try self.authority.publish(claim, .{ .value = false });
                }
            }
            var started: usize = 0;
            errdefer {
                self.mutex.lock();
                self.stopping = true;
                self.changed.broadcast();
                self.mutex.unlock();
                for (workers[0..started]) |w| w.thread.?.join();
            }
            for (workers, 0..) |*w, index| {
                w.thread = try std.Thread.spawn(.{}, workerMain, .{ self, index });
                started += 1;
            }
            return self;
        }
        /// Attach the returned handle to the host before explicitly dispatching initial rounds.
        pub fn activate(self: *Self) void {
            self.mutex.lock();
            defer self.mutex.unlock();
            if (self.active) return;
            self.active = true;
            for (0..node_count) |ni| self.wakeLocked(ni, 0, true);
        }
        fn blockIndex(uid: u32) !usize {
            return tr.blockIndex(registry, uid) orelse error.UnregisteredBlock;
        }
        fn nodeIndex(uid: u32) ?usize {
            for (node_uids, 0..) |id, i| {
                if (id == uid) return i;
            }
            return null;
        }
        fn parentNode(bi: usize) usize {
            return nodeIndex(topology.blocks[tr.treeBlockIndex(topology, registry.blocks[bi].uid).?].parent.?).?;
        }
        fn enqueueLocked(self: *Self, ni: usize) void {
            const n = &self.nodes[ni];
            if (n.busy or n.queued or !self.active) return;
            std.debug.assert(self.queue_len < node_count);
            self.queue[(self.queue_head + self.queue_len) % node_count] = ni;
            self.queue_len += 1;
            n.queued = true;
            self.changed.broadcast();
        }
        fn wakeLocked(self: *Self, ni: usize, marks: u64, needs_align: bool) void {
            const n = &self.nodes[ni];
            n.marks |= marks;
            n.needs_align = n.needs_align or needs_align;
            n.requested += 1;
            self.enqueueLocked(ni);
        }
        fn copyReason(buffer: []u8, reason: t.Reason) t.Reason {
            std.debug.assert(reason.owner.len <= buffer.len and reason.cause.len <= buffer.len - reason.owner.len);
            @memcpy(buffer[0..reason.owner.len], reason.owner);
            @memcpy(buffer[reason.owner.len..][0..reason.cause.len], reason.cause);
            return .{ .kind = reason.kind, .owner = buffer[0..reason.owner.len], .cause = buffer[reason.owner.len..][0..reason.cause.len], .plan_version = reason.plan_version };
        }
        fn wakeSourceLocked(self: *Self, ni: usize, marks: u64, needs_align: bool, source: t.EventSource, reason: ?t.Reason) void {
            const start = ni * self.config.reason_text_capacity;
            self.nodes[ni].source = source;
            self.nodes[ni].reason = if (reason) |why| copyReason(self.pending_reason_bytes[start..][0..self.config.reason_text_capacity], why) else null;
            self.wakeLocked(ni, marks, needs_align);
        }
        fn stateChangedLocked(self: *Self, bi: usize) void {
            const uid = registry.blocks[bi].uid;
            if (nodeIndex(uid)) |ni| self.wakeSourceLocked(ni, 0, true, .{ .cause = .state, .uid = uid }, null);
            const parent = parentNode(bi);
            var marks: u64 = 0;
            for (metadata) |m| {
                if (m.node != parent) continue;
                for (registry.conditions[m.declaration].children) |child| {
                    if (child == uid) marks |= @as(u64, 1) << m.bit;
                }
            }
            self.wakeSourceLocked(parent, marks, true, .{ .cause = .state, .uid = uid }, null);
        }
        fn transitionLocked(self: *Self, bi: usize, to: t.BlockState) Transition {
            const from = self.blocks[bi].value;
            self.blocks[bi].value = to;
            self.transition_sequence += 1;
            if (self.transitions.len > 0) {
                self.transitions[@intCast((self.transition_sequence - 1) % self.transitions.len)] = .{ .sequence = self.transition_sequence, .block = registry.blocks[bi].uid, .run = self.blocks[bi].run, .from = from, .to = to };
                self.transition_retained = @min(self.transition_retained + 1, self.transitions.len);
            }
            self.stateChangedLocked(bi);
            return .{ .sequence = self.transition_sequence, .block = registry.blocks[bi].uid, .run = self.blocks[bi].run, .from = from, .to = to };
        }
        pub fn readState(self: *Self, block: u32) !t.BlockState {
            self.mutex.lock();
            defer self.mutex.unlock();
            return self.blocks[try blockIndex(block)].value;
        }
        fn logTransition(self: *Self, row: Transition) void {
            const name_capacity = comptime blk: {
                var max: usize = 0;
                for (registry.blocks) |b| {
                    if (b.name) |name| max = @max(max, name.len);
                }
                break :blk max;
            };
            var buffer: [name_capacity + 192]u8 = undefined;
            const name = registry.blocks[blockIndex(row.block) catch unreachable].name;
            const text = if (name) |n| std.fmt.bufPrint(&buffer, "Business block {d} ({s}): {s} -> {s}", .{ row.block, n, @tagName(row.from), @tagName(row.to) }) catch unreachable else std.fmt.bufPrint(&buffer, "Business block {d}: {s} -> {s}", .{ row.block, @tagName(row.from), @tagName(row.to) }) catch unreachable;
            self.host.log(self.host.context, 2, text.ptr, text.len);
        }
        fn report(self: *Self, block: u32, run: u64, running: bool) !void {
            self.mutex.lock();
            const bi = blockIndex(block) catch |err| {
                self.mutex.unlock();
                return err;
            };
            var transition: ?Transition = null;
            if (self.blocks[bi].run == run and (if (running) self.blocks[bi].value == .starting else self.blocks[bi].value != .stopped)) {
                transition = self.transitionLocked(bi, if (running) .running else .stopped);
                self.callbacks += 1;
            }
            self.mutex.unlock();
            if (transition) |row| {
                self.logTransition(row);
                self.mutex.lock();
                self.callbacks -= 1;
                self.changed.broadcast();
                self.mutex.unlock();
            }
        }
        pub fn reportRunning(self: *Self, block: u32, run: u64) !void {
            return self.report(block, run, true);
        }
        pub fn reportReturned(self: *Self, block: u32, run: u64) !void {
            return self.report(block, run, false);
        }
        pub fn claimWrite(self: *Self, key: t.ItemKey) !state.Claim {
            self.mutex.lock();
            defer self.mutex.unlock();
            return self.authority.claimWrite(key);
        }
        pub fn claimCell(self: *Self, key: t.ItemKey) !state.Claim {
            self.mutex.lock();
            defer self.mutex.unlock();
            return self.authority.claimCell(key);
        }
        pub fn read(self: *Self, key: t.ItemKey) !t.Read {
            return self.authority.read(key);
        }
        pub fn readPresence(self: *Self, key: t.ItemKey) !t.Presence {
            return self.authority.readPresence(key);
        }
        fn tokenLocked(self: *Self, token: ?u64) !?usize {
            if (token) |value| {
                if (!self.active) return error.NotActive;
                for (self.completions, 0..) |*c, ci| if (!c.used) {
                    c.* = .{ .used = true, .token = value };
                    return ci;
                };
                return error.CompletionFull;
            }
            return null;
        }
        fn eventLocked(self: *Self, key: t.ItemKey, completion: ?usize, cause: t.EventCause, reason: ?t.Reason) void {
            var root_marked = false;
            for (node_items) |route| {
                if (route.key.item != key.item or route.key.block != key.block) continue;
                const root_shutdown = node_uids[route.node] == topology.program and key.item == self.config.shutdown_item;
                self.wakeSourceLocked(route.node, route.bits, root_shutdown, .{ .cause = cause, .uid = key.item, .block = key.block }, reason);
                if (completion) |ci| self.completions[ci].targets[route.node] = self.nodes[route.node].requested;
                root_marked = root_marked or root_shutdown;
            }
            if (key.item == self.config.shutdown_item and !root_marked) {
                const ni = nodeIndex(topology.program).?;
                self.wakeSourceLocked(ni, 0, true, .{ .cause = .shutdown, .uid = key.item }, reason);
                if (completion) |ci| self.completions[ci].targets[ni] = self.nodes[ni].requested;
            }
        }
        const Publication = union(enum) { boolean: t.Read, presence: t.Presence };
        pub fn publish(self: *Self, claim: state.Claim, value: t.Read, why: t.Reason, token: ?u64) !void {
            return self.publishValue(claim, .{ .boolean = value }, why, token);
        }
        pub fn publishPresence(self: *Self, claim: state.Claim, value: t.Presence, why: t.Reason, token: ?u64) !void {
            return self.publishValue(claim, .{ .presence = value }, why, token);
        }
        fn validateReason(self: *Self, why: t.Reason) !void {
            if (why.owner.len == 0 or why.cause.len == 0) return error.InvalidReason;
            if (why.owner.len > self.config.reason_text_capacity or why.cause.len > self.config.reason_text_capacity - why.owner.len) return error.ReasonTooLong;
        }
        fn publishValue(self: *Self, claim: state.Claim, value: Publication, why: t.Reason, token: ?u64) !void {
            try self.validateReason(why);
            self.mutex.lock();
            const claim_index = self.authority.claimIndex(claim) catch |err| {
                self.mutex.unlock();
                return err;
            };
            const ci = self.tokenLocked(token) catch |err| {
                self.mutex.unlock();
                return err;
            };
            _ = (switch (value) {
                .boolean => |v| self.authority.publish(claim, v),
                .presence => |v| self.authority.publishPresence(claim, v),
            }) catch |err| {
                if (ci) |i| self.completions[i].used = false;
                self.mutex.unlock();
                return err;
            };
            const entry = &self.authority.entries[claim_index];
            const visible = entry.declaration.storage == .own or entry.bound;
            if (visible) self.eventLocked(entry.key, ci, if (entry.key.item == self.config.shutdown_item) .shutdown else .item, why);
            const last_reader = self.readers.last_id;
            self.mutex.unlock();
            self.completeReady();
            if (visible) self.notifyReaders(entry.key, why, last_reader);
        }
        fn binding(self: *Self, claim: state.Claim, bound: bool, why: t.Reason, token: ?u64) !void {
            try self.validateReason(why);
            self.mutex.lock();
            const ci = self.tokenLocked(token) catch |err| {
                self.mutex.unlock();
                return err;
            };
            _ = (if (bound) self.authority.bind(claim) else self.authority.unbind(claim)) catch |err| {
                if (ci) |i| self.completions[i].used = false;
                self.mutex.unlock();
                return err;
            };
            const key = self.authority.entries[self.authority.claimIndex(claim) catch unreachable].key;
            self.eventLocked(key, ci, if (bound) .bind else .unbind, why);
            const last_reader = self.readers.last_id;
            self.mutex.unlock();
            self.completeReady();
            self.notifyReaders(key, why, last_reader);
        }
        pub fn bind(self: *Self, claim: state.Claim, why: t.Reason, token: ?u64) !void {
            return self.binding(claim, true, why, token);
        }
        pub fn unbind(self: *Self, claim: state.Claim, why: t.Reason, token: ?u64) !void {
            return self.binding(claim, false, why, token);
        }
        pub fn shutdown(self: *Self, why: t.Reason, token: ?u64) !void {
            return self.publish(self.shutdown_claim, .{ .value = true }, why, token);
        }
        pub fn setDemand(self: *Self, key: []const u8, block: u32, why: t.Reason, token: ?u64) !void {
            return self.changeDemand(key, block, why, token);
        }
        pub fn clearDemand(self: *Self, key: []const u8, why: t.Reason, token: ?u64) !void {
            return self.changeDemand(key, null, why, token);
        }
        fn changeDemand(self: *Self, key: []const u8, block: ?u32, why: t.Reason, token: ?u64) !void {
            if (why.owner.len == 0 or why.cause.len == 0) return error.InvalidDemand;
            if (why.owner.len > self.config.reason_text_capacity or why.cause.len > self.config.reason_text_capacity - why.owner.len) return error.ReasonTooLong;
            self.mutex.lock();
            if (self.config.demand_item == null) {
                self.mutex.unlock();
                return error.NoDemandItem;
            }
            if (block) |uid| {
                _ = blockIndex(uid) catch |err| {
                    self.mutex.unlock();
                    return err;
                };
                if (uid == topology.program) {
                    self.mutex.unlock();
                    return error.InvalidDemand;
                }
            }
            const ci = self.tokenLocked(token) catch |err| {
                self.mutex.unlock();
                return err;
            };
            const change = (if (block) |uid| self.demands.set(key, uid, why) else self.demands.clear(key)) catch |err| {
                if (ci) |i| self.completions[i].used = false;
                self.mutex.unlock();
                return err;
            };
            const affected = [_]?u32{ change.previous, change.current };
            for (affected, 0..) |uid_opt, index| {
                const uid = uid_opt orelse continue;
                if (index == 1 and change.previous == uid) continue;
                const bi = blockIndex(uid) catch unreachable;
                const claim = self.demand_claims[bi].?;
                _ = self.authority.publish(claim, .{ .value = self.demands.has(uid) }) catch unreachable;
                self.eventLocked(.{ .item = self.config.demand_item.?, .block = uid }, ci, .demand, why);
            }
            const last_reader = self.readers.last_id;
            self.mutex.unlock();
            self.completeReady();
            for (affected, 0..) |uid_opt, index| {
                const uid = uid_opt orelse continue;
                if (index == 1 and change.previous == uid) continue;
                self.notifyReaders(.{ .item = self.config.demand_item.?, .block = uid }, why, last_reader);
            }
        }
        pub fn registerReader(self: *Self, key: t.ItemKey, holder: []const u8) !u64 {
            self.mutex.lock();
            defer self.mutex.unlock();
            _ = try self.authority.indexOf(key);
            return self.readers.register(key, holder);
        }
        pub fn unregisterReader(self: *Self, id: u64) !void {
            self.mutex.lock();
            defer self.mutex.unlock();
            self.readers.unregister(id);
        }
        fn notifyReaders(self: *Self, key: t.ItemKey, why: t.Reason, last_reader: u64) void {
            const key_view = t.ItemKeyView.fromKey(key);
            const reason_view = t.ReasonView.fromReason(why);
            for (0..self.readers.entries.len) |index| {
                self.mutex.lock();
                const entry = self.readers.entries[index];
                const notify = entry.used and entry.id <= last_reader and entry.key.item == key.item and entry.key.block == key.block;
                const id = entry.id;
                if (notify) self.callbacks += 1;
                self.mutex.unlock();
                if (notify) {
                    self.host.item_changed(self.host.context, id, key_view, &reason_view);
                    self.mutex.lock();
                    self.callbacks -= 1;
                    self.changed.broadcast();
                    self.mutex.unlock();
                }
            }
        }
        fn completeReady(self: *Self) void {
            for (0..self.completions.len) |index| {
                self.mutex.lock();
                const c = &self.completions[index];
                var ready = c.used;
                for (self.nodes, 0..) |n, ni| {
                    if (n.completed < c.targets[ni]) ready = false;
                }
                const token = c.token;
                if (ready) {
                    c.used = false;
                    self.callbacks += 1;
                }
                self.mutex.unlock();
                if (ready) {
                    self.host.complete(self.host.context, token);
                    self.mutex.lock();
                    self.callbacks -= 1;
                    self.changed.broadcast();
                    self.mutex.unlock();
                }
            }
        }
        const Evaluation = struct { self: *Self, instance: usize, captured: []const ItemValue, captured_children: []const t.BlockState };
        fn inputRead(context: *anyopaque, key: t.ItemKey) !t.Read {
            const e: *Evaluation = @ptrCast(@alignCast(context));
            const m = metadata[e.instance];
            for (registry.conditions[m.declaration].items) |r| {
                const block = if (m.block != null and r.block == null and registry.items[tr.itemIndex(registry, r.item).?].per_block) m.block else r.block;
                if (r.item == key.item and block == key.block) {
                    for (e.captured) |entry| {
                        if (entry.key.item == key.item and entry.key.block == key.block) return entry.value;
                    }
                    unreachable;
                }
            }
            return error.UndeclaredItem;
        }
        fn inputCondition(context: *anyopaque, uid: u32) !bool {
            const e: *Evaluation = @ptrCast(@alignCast(context));
            const m = metadata[e.instance];
            var declared = false;
            for (registry.conditions[m.declaration].conditions) |id| {
                if (id == uid) declared = true;
            }
            if (!declared) return error.UndeclaredCondition;
            e.self.mutex.lock();
            defer e.self.mutex.unlock();
            for (metadata, 0..) |lower, li| {
                if (lower.node == m.node and registry.conditions[lower.declaration].uid == uid) return e.self.conditions[li].value;
            }
            return error.UndeclaredCondition;
        }
        fn inputChild(context: *anyopaque, uid: u32) !t.BlockState {
            const e: *Evaluation = @ptrCast(@alignCast(context));
            const m = metadata[e.instance];
            for (registry.conditions[m.declaration].children) |id| {
                if (id == uid) return e.captured_children[blockIndex(uid) catch unreachable];
            }
            return error.UndeclaredChild;
        }
        fn setPermitLocked(self: *Self, ni: usize, value: bool, reason: ?t.Reason) bool {
            const n = &self.nodes[ni];
            if (n.permitted == value) return false;
            n.permitted = value;
            n.drained = false;
            self.wakeSourceLocked(ni, 0, true, .{ .cause = .permit, .uid = node_uids[ni] }, reason);
            if (node_uids[ni] != topology.program) self.wakeSourceLocked(parentNode(blockIndex(node_uids[ni]) catch unreachable), 0, true, .{ .cause = .permit, .uid = node_uids[ni] }, reason);
            return true;
        }
        fn wanted(self: *Self, ni: usize, bi: usize, active: bool) bool {
            if (!active) return false;
            for (metadata, 0..) |m, mi| {
                if (m.node == ni and m.decides == bi) return self.conditions[mi].value;
            }
            return false;
        }
        fn alignLocked(self: *Self, ni: usize, actions: []Action, action_count: *usize, captured: []const ItemValue, source: t.EventSource, reason: ?t.Reason) void {
            const uid = node_uids[ni];
            const n = &self.nodes[ni];
            const active = n.permitted and (uid == topology.program or self.blocks[blockIndex(uid) catch unreachable].value == .running) and !(uid == topology.program and (self.authority.read(.{ .item = self.config.shutdown_item }) catch unreachable).valueOr(false));
            var actual: [block_count]t.BlockState = undefined;
            var summaries: [block_count]bool = @splat(true);
            for (self.blocks, 0..) |b, bi| {
                actual[bi] = b.value;
                if (nodeIndex(registry.blocks[bi].uid)) |child_node| summaries[bi] = self.nodes[child_node].drained;
            }
            var should_run: u64 = 0;
            for (metadata, 0..) |m, mi| {
                if (m.node == ni and m.decides != null and active and self.conditions[mi].value) should_run |= @as(u64, 1) << m.bit;
            }
            n.sequence += 1;
            const current_items = self.current_items[key_offsets[ni]..key_offsets[ni + 1]];
            @memcpy(current_items, captured);
            const current_reason_start = ni * self.config.reason_text_capacity;
            const current_reason_buffer = self.current_reason_bytes[current_reason_start..][0..self.config.reason_text_capacity];
            self.current_decisions[ni] = .{
                .sequence = n.sequence,
                .node = uid,
                .should_run = should_run,
                .source = source,
                .reason = if (reason) |why| copyReason(current_reason_buffer, why) else null,
                .items = current_items,
            };
            if (self.config.history_capacity > 0) {
                const slot = ni * self.config.history_capacity + @as(usize, @intCast((n.sequence - 1) % self.config.history_capacity));
                const count = captured.len;
                const item_start = self.config.history_capacity * key_offsets[ni] + @as(usize, @intCast((n.sequence - 1) % self.config.history_capacity)) * count;
                const items = self.history_items[item_start..][0..count];
                @memcpy(items, captured);
                const reason_start = slot * self.config.reason_text_capacity;
                self.decisions[slot] = .{ .sequence = n.sequence, .node = uid, .should_run = should_run, .source = source, .reason = if (reason) |why| copyReason(self.history_reason_bytes[reason_start..][0..self.config.reason_text_capacity], why) else null, .items = items };
                n.retained = @min(n.retained + 1, self.config.history_capacity);
            }
            var acted = false;
            for (registry.blocks, 0..) |child, bi| {
                if (!tr.isChild(topology, uid, child.uid) or self.wanted(ni, bi, active)) continue;
                var intent_changed = false;
                const branch = nodeIndex(child.uid);
                if (branch) |child_node| {
                    intent_changed = self.setPermitLocked(child_node, false, reason);
                    acted = acted or intent_changed;
                }
                if (actual[bi] == .stopped) continue;
                if (branch != null and (intent_changed or !summaries[bi])) continue;
                if (actual[bi] != .stopping) {
                    actions[action_count.*] = .{ .transition = self.transitionLocked(bi, .stopping) };
                    action_count.* += 1;
                    actions[action_count.*] = .{ .stop = .{ .block = child.uid, .run = self.blocks[bi].run } };
                    action_count.* += 1;
                    acted = true;
                }
            }
            for (registry.blocks, 0..) |child, bi| {
                if (!tr.isChild(topology, uid, child.uid) or !self.wanted(ni, bi, active)) continue;
                const branch = nodeIndex(child.uid);
                if (actual[bi] == .stopped) {
                    if (branch) |child_node| {
                        const changed_permit = self.setPermitLocked(child_node, false, reason);
                        acted = acted or changed_permit;
                        if (changed_permit or !summaries[bi]) continue;
                    }
                }
                if (branch) |child_node| {
                    acted = self.setPermitLocked(child_node, true, reason) or acted;
                }
                if (actual[bi] == .stopped) {
                    self.blocks[bi].run += 1;
                    actions[action_count.*] = .{ .transition = self.transitionLocked(bi, .starting) };
                    action_count.* += 1;
                    actions[action_count.*] = .{ .start = .{ .block = child.uid, .run = self.blocks[bi].run } };
                    action_count.* += 1;
                    acted = true;
                }
            }
            var drained = true;
            for (registry.blocks, 0..) |child, bi| {
                if (!tr.isChild(topology, uid, child.uid)) continue;
                if (self.blocks[bi].value != .stopped) drained = false;
                if (nodeIndex(child.uid)) |child_node| {
                    if (self.nodes[child_node].permitted or !self.nodes[child_node].drained) drained = false;
                }
            }
            if (n.drained != drained) {
                n.drained = drained;
                if (uid != topology.program) self.wakeSourceLocked(parentNode(blockIndex(uid) catch unreachable), 0, true, .{ .cause = .branch_summary, .uid = uid }, reason);
            }
            if (acted) {
                actions[action_count.*] = .{ .info = .{ .uid = uid, .wanted = should_run, .source = source, .kind = if (reason) |why| why.kind else null, .plan = if (reason) |why| why.plan_version else null } };
                action_count.* += 1;
            }
        }
        fn workerMain(self: *Self, worker_index: usize) void {
            const worker = &self.workers[worker_index];
            while (true) {
                self.mutex.lock();
                while (self.queue_len == 0 and !self.stopping) self.changed.wait(&self.mutex);
                if (self.stopping) {
                    self.mutex.unlock();
                    return;
                }
                const ni = self.queue[self.queue_head];
                self.queue_head = (self.queue_head + 1) % node_count;
                self.queue_len -= 1;
                const node = &self.nodes[ni];
                node.queued = false;
                node.busy = true;
                var marks = node.marks;
                node.marks = 0;
                var needs_align = node.needs_align;
                node.needs_align = false;
                const round = node.requested;

                // A worker's bounded arena is reset for each round. It never grows.
                var arena = std.heap.FixedBufferAllocator.init(worker.buffer);
                const actions = arena.allocator().alloc(Action, block_count * 3 + instance_count + 1) catch unreachable;
                const captured = arena.allocator().alloc(ItemValue, key_offsets[ni + 1] - key_offsets[ni]) catch unreachable;
                for (node_items[key_offsets[ni]..key_offsets[ni + 1]], captured) |entry, *value| value.* = .{ .key = entry.key, .value = self.authority.read(entry.key) catch unreachable };
                // Items and child states share one locked round basis. Compute
                // reads use this capture; alignment still reads live states.
                const captured_children = arena.allocator().alloc(t.BlockState, block_count) catch unreachable;
                for (self.blocks, captured_children) |block, *value| value.* = block.value;
                const source = node.source;
                const reason_buffer = arena.allocator().alloc(u8, self.config.reason_text_capacity) catch unreachable;
                const reason: ?t.Reason = if (node.reason) |why| copyReason(reason_buffer, why) else null;
                self.mutex.unlock();
                var action_count: usize = 0;
                for (metadata, 0..) |m, mi| {
                    if (m.node != ni or marks & (@as(u64, 1) << m.bit) == 0) continue;
                    var evaluation: Evaluation = .{ .self = self, .instance = mi, .captured = captured, .captured_children = captured_children };
                    var input: t.Input = .{ .context = &evaluation, .block = m.block, .read_fn = inputRead, .condition_fn = inputCondition, .child_fn = inputChild };
                    const c = registry.conditions[m.declaration];
                    var function: ?*const fn (*t.Input) anyerror!bool = null;
                    for (topology.computes) |compute| {
                        if (compute.condition == c.uid) function = compute.function;
                    }
                    const value = function.?(&input) catch |err| {
                        self.mutex.lock();
                        self.conditions[mi].last_error = err;
                        self.mutex.unlock();
                        actions[action_count] = .{ .failure = .{ .uid = c.uid, .err = err } };
                        action_count += 1;
                        continue;
                    };
                    self.mutex.lock();
                    const changed_value = self.conditions[mi].value != value;
                    self.conditions[mi] = .{ .value = value };
                    self.mutex.unlock();
                    if (!changed_value) continue;
                    if (m.decides != null) needs_align = true;
                    for (metadata) |reader| {
                        if (reader.node != ni) continue;
                        for (registry.conditions[reader.declaration].conditions) |lower| {
                            if (lower == c.uid) marks |= @as(u64, 1) << reader.bit;
                        }
                    }
                }
                self.mutex.lock();
                if (needs_align) self.alignLocked(ni, actions, &action_count, captured, source, reason);
                self.mutex.unlock();
                // No controller lock crosses any host call, including reentrant reports.
                for (actions[0..action_count]) |action| switch (action) {
                    .transition => |row| self.logTransition(row),
                    .start => |a| self.host.start(self.host.context, a.block, a.run),
                    .stop => |a| self.host.stop(self.host.context, a.block, a.run),
                    .info => |info| {
                        const max_name = comptime blk: {
                            var max: usize = 0;
                            for (registry.blocks) |block| {
                                if (block.name) |name| max = @max(max, name.len);
                            }
                            break :blk max;
                        };
                        var children_buffer: [64 * 11]u8 = undefined;
                        var children_text = @import("json.zig").Writer.init(&children_buffer);
                        for (metadata) |m| {
                            if (m.node != ni or m.decides == null or info.wanted & (@as(u64, 1) << m.bit) == 0) continue;
                            if (children_text.len > 0) children_text.raw(",");
                            children_text.number(registry.blocks[m.decides.?].uid);
                        }
                        var buffer: [max_name + 1024]u8 = undefined;
                        const name = registry.blocks[blockIndex(info.uid) catch unreachable].name;
                        const text = std.fmt.bufPrint(&buffer, "Business block decision {d}{s}{s}{s}: source={s}/{d}/{?d}; kind={?d}; plan={?d}; children={s}", .{ info.uid, if (name != null) " (" else "", name orelse "", if (name != null) ")" else "", @tagName(info.source.cause), info.source.uid, info.source.block, info.kind, info.plan, children_buffer[0..children_text.len] }) catch unreachable;
                        self.host.log(self.host.context, 2, text.ptr, text.len);
                    },
                    .failure => |a| {
                        const max_name = comptime blk: {
                            var max: usize = 0;
                            for (registry.conditions) |condition| {
                                if (condition.name) |name| max = @max(max, name.len);
                            }
                            break :blk max;
                        };
                        var buffer: [max_name + 256]u8 = undefined;
                        const name = registry.conditions[tr.conditionIndex(registry, a.uid).?].name;
                        const text = std.fmt.bufPrint(&buffer, "Condition {d}{s}{s}{s}: {s}", .{ a.uid, if (name != null) " (" else "", name orelse "", if (name != null) ")" else "", @errorName(a.err) }) catch unreachable;
                        self.host.log(self.host.context, 4, text.ptr, text.len);
                    },
                };
                self.mutex.lock();
                node.completed = round;
                node.busy = false;
                if (node.requested > round) self.enqueueLocked(ni);
                self.changed.broadcast();
                self.mutex.unlock();
                self.completeReady();
            }
        }
        pub fn waitIdle(self: *Self) void {
            self.mutex.lock();
            defer self.mutex.unlock();
            while (true) {
                var idle = self.queue_len == 0 and self.callbacks == 0;
                for (self.nodes) |node| {
                    if (node.busy) idle = false;
                }
                for (self.completions) |c| {
                    if (c.used) idle = false;
                }
                if (idle) return;
                self.changed.wait(&self.mutex);
            }
        }
        pub fn transitionCount(self: *Self) usize {
            self.mutex.lock();
            defer self.mutex.unlock();
            return self.transition_retained;
        }
        pub fn decisionCount(self: *Self, uid: u32) usize {
            self.mutex.lock();
            defer self.mutex.unlock();
            return self.nodes[nodeIndex(uid) orelse return 0].retained;
        }
        pub fn decisionSequence(self: *Self, uid: u32) u64 {
            self.mutex.lock();
            defer self.mutex.unlock();
            return self.nodes[nodeIndex(uid) orelse return 0].sequence;
        }
        pub fn destroy(self: *Self) !void {
            self.mutex.lock();
            for (self.blocks) |b| {
                if (b.value != .stopped) {
                    self.mutex.unlock();
                    return error.RunsActive;
                }
            }
            if (!(self.authority.read(.{ .item = self.config.shutdown_item }) catch unreachable).valueOr(false)) {
                self.mutex.unlock();
                return error.NotShuttingDown;
            }
            self.mutex.unlock();
            self.waitIdle();
            self.mutex.lock();
            self.stopping = true;
            self.changed.broadcast();
            self.mutex.unlock();
            for (self.workers) |worker| {
                worker.thread.?.join();
                self.allocator.free(worker.buffer);
            }
            self.allocator.free(self.workers);
            self.allocator.free(self.transitions);
            self.allocator.free(self.decisions);
            self.allocator.free(self.history_items);
            self.allocator.free(self.current_items);
            self.allocator.free(self.pending_reason_bytes);
            self.allocator.free(self.history_reason_bytes);
            self.allocator.free(self.current_reason_bytes);
            self.allocator.free(self.completions);
            self.readers.deinit();
            self.authority.deinit();
            self.demands.deinit();
            const allocator = self.allocator;
            allocator.destroy(self);
        }
        const Json = @import("json.zig").Writer;
        fn jsonRead(w: *Json, value: t.Read) void {
            w.raw("\"value\":");
            switch (value) {
                .value => |v| w.boolean(v),
                else => w.raw("null"),
            }
            w.raw(",\"noValueReason\":");
            switch (value) {
                .value => w.raw("null"),
                else => w.string(@tagName(value)),
            }
        }
        fn jsonName(w: *Json, name: ?[]const u8) void {
            if (name) |n| {
                w.raw(",\"name\":");
                w.string(n);
            }
        }
        fn jsonDecision(w: *Json, row: Decision) void {
            w.raw("{\"sequence\":");
            w.number(row.sequence);
            w.raw(",\"node\":");
            w.number(row.node);
            w.raw(",\"shouldRun\":[");
            var first_child = true;
            for (metadata) |m| {
                if (m.node == nodeIndex(row.node).? and m.decides != null and row.should_run & (@as(u64, 1) << m.bit) != 0) {
                    if (!first_child) w.raw(",");
                    first_child = false;
                    w.number(registry.blocks[m.decides.?].uid);
                }
            }
            w.raw("],\"source\":{\"cause\":");
            w.string(@tagName(row.source.cause));
            w.raw(",\"uid\":");
            w.number(row.source.uid);
            if (row.source.block) |block| {
                w.raw(",\"blockUid\":");
                w.number(block);
            }
            w.raw("},\"reason\":");
            if (row.reason) |why| {
                w.raw("{\"kind\":");
                w.number(why.kind);
                w.raw(",\"owner\":");
                w.string(why.owner);
                w.raw(",\"cause\":");
                w.string(why.cause);
                w.raw(",\"planVersion\":");
                w.number(why.plan_version);
                w.raw("}");
            } else w.raw("null");
            w.raw(",\"items\":[");
            for (row.items, 0..) |item, item_index| {
                if (item_index > 0) w.raw(",");
                w.raw("{\"uid\":");
                w.number(item.key.item);
                jsonName(w, registry.items[tr.itemIndex(registry, item.key.item).?].name);
                if (item.key.block) |block| {
                    w.raw(",\"blockUid\":");
                    w.number(block);
                }
                w.raw(",");
                jsonRead(w, item.value);
                w.raw("}");
            }
            w.raw("]}");
        }
        pub fn snapshot(self: *Self, buffer: []u8) !usize {
            self.mutex.lock();
            defer self.mutex.unlock();
            var w = Json.init(buffer);
            w.raw("{\"registryVersion\":");
            w.string(registry.version);
            w.raw(",\"program\":");
            w.number(topology.program);
            w.raw(",\"shuttingDown\":");
            w.boolean((self.authority.read(.{ .item = self.config.shutdown_item }) catch unreachable).valueOr(false));
            w.raw(",\"blocks\":[");
            for (registry.blocks, 0..) |b, bi| {
                if (bi > 0) w.raw(",");
                w.raw("{\"uid\":");
                w.number(b.uid);
                jsonName(&w, b.name);
                w.raw(",\"state\":");
                w.string(@tagName(self.blocks[bi].value));
                w.raw(",\"run\":");
                w.number(self.blocks[bi].run);
                const tb = topology.blocks[tr.treeBlockIndex(topology, b.uid).?];
                w.raw(",\"parent\":");
                if (tb.parent) |p| w.number(p) else w.raw("null");
                w.raw(",\"inputs\":[");
                for (tb.inputs, 0..) |input, ii| {
                    if (ii > 0) w.raw(",");
                    w.number(input);
                }
                w.raw("]}");
            }
            w.raw("],\"transitions\":[");
            for (0..self.transition_retained) |index| {
                const sequence = self.transition_sequence - self.transition_retained + index + 1;
                const row = self.transitions[@intCast((sequence - 1) % self.transitions.len)];
                if (index > 0) w.raw(",");
                w.raw("{\"sequence\":");
                w.number(row.sequence);
                w.raw(",\"uid\":");
                w.number(row.block);
                w.raw(",\"run\":");
                w.number(row.run);
                w.raw(",\"from\":");
                w.string(@tagName(row.from));
                w.raw(",\"to\":");
                w.string(@tagName(row.to));
                w.raw("}");
            }
            w.raw("],\"demands\":[");
            var first = true;
            for (self.demands.entries) |entry| {
                if (!entry.used) continue;
                if (!first) w.raw(",");
                first = false;
                w.raw("{\"key\":");
                w.string(entry.key);
                w.raw(",\"blockUid\":");
                w.number(entry.block);
                w.raw(",\"kind\":");
                w.number(entry.reason.kind);
                w.raw(",\"owner\":");
                w.string(entry.reason.owner);
                w.raw(",\"cause\":");
                w.string(entry.reason.cause);
                w.raw(",\"planVersion\":");
                w.number(entry.reason.plan_version);
                w.raw("}");
            }
            w.raw("],\"readers\":[");
            first = true;
            for (self.readers.entries) |entry| {
                if (!entry.used) continue;
                if (!first) w.raw(",");
                first = false;
                w.raw("{\"id\":");
                w.number(entry.id);
                w.raw(",\"itemUid\":");
                w.number(entry.key.item);
                if (entry.key.block) |block| {
                    w.raw(",\"blockUid\":");
                    w.number(block);
                }
                w.raw(",\"holder\":");
                w.string(entry.holder);
                w.raw("}");
            }
            w.raw("],\"items\":[");
            for (self.authority.entries, 0..) |*entry, index| {
                if (index > 0) w.raw(",");
                w.raw("{\"uid\":");
                w.number(entry.key.item);
                jsonName(&w, entry.declaration.name);
                if (entry.key.block) |block| {
                    w.raw(",\"blockUid\":");
                    w.number(block);
                }
                w.raw(",");
                if (entry.declaration.value_type == .host) {
                    const publication = entry.readCurrent();
                    w.raw("\"hasValue\":");
                    w.boolean(publication == .value);
                    w.raw(",\"noValueReason\":");
                    if (publication == .value) w.raw("null") else w.string(@tagName(publication));
                } else jsonRead(&w, entry.readCurrent());
                w.raw("}");
            }
            w.raw("],\"nodes\":[");
            for (node_uids, 0..) |uid, ni| {
                if (ni > 0) w.raw(",");
                w.raw("{\"uid\":");
                w.number(uid);
                w.raw(",\"permitted\":");
                w.boolean(self.nodes[ni].permitted);
                w.raw(",\"drained\":");
                w.boolean(self.nodes[ni].drained);
                w.raw(",\"conditions\":[");
                first = true;
                for (metadata, 0..) |m, mi| {
                    if (m.node != ni) continue;
                    if (!first) w.raw(",");
                    first = false;
                    const c = registry.conditions[m.declaration];
                    w.raw("{\"uid\":");
                    w.number(c.uid);
                    jsonName(&w, c.name);
                    if (m.block) |block| {
                        w.raw(",\"blockUid\":");
                        w.number(block);
                    }
                    w.raw(",\"value\":");
                    w.boolean(self.conditions[mi].value);
                    w.raw(",\"lastError\":");
                    if (self.conditions[mi].last_error) |err| w.string(@errorName(err)) else w.raw("null");
                    w.raw("}");
                }
                w.raw("]}");
            }
            w.raw("],\"decisions\":[");
            first = true;
            for (self.nodes, 0..) |node, ni| {
                for (0..node.retained) |index| {
                    const sequence = node.sequence - node.retained + index + 1;
                    const row = self.decisions[ni * self.config.history_capacity + @as(usize, @intCast((sequence - 1) % self.config.history_capacity))];
                    if (!first) w.raw(",");
                    first = false;
                    jsonDecision(&w, row);
                }
            }
            w.raw("],\"currentDecisions\":[");
            first = true;
            for (self.current_decisions) |maybe_row| {
                const row = maybe_row orelse continue;
                if (!first) w.raw(",");
                first = false;
                jsonDecision(&w, row);
            }
            w.raw("],\"allocator\":{\"current\":");
            w.number(self.demands.current());
            w.raw(",\"peak\":");
            w.number(self.demands.peak);
            w.raw(",\"capacity\":");
            w.number(self.demands.capacity());
            w.raw("},\"readerAllocator\":{\"current\":");
            w.number(self.readers.current());
            w.raw(",\"peak\":");
            w.number(self.readers.peak);
            w.raw(",\"capacity\":");
            w.number(self.readers.capacity());
            w.raw("}}");
            return w.len;
        }
    };
}
