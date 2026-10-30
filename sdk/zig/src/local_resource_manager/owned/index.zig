const std = @import("std");
const transaction = @import("index_transactions.zig");

pub const CandidateKind = enum {
    c2_chain,
};

pub const LocationKind = enum(u8) {
    resource = 0,
    partition = 1,
};

pub const Location = struct {
    kind: LocationKind,
    table: u32,
    slot: u32,
};

// Includes the separately declared million-resource complete-owner timing
// profile. The original 589824-identity maximum combination remains a gate.
pub const max_indexed_nodes: usize = 1_048_576;
pub const max_homes: usize = 2_097_152;
const max_location_tables: u32 = 64;
pub const no_node: u32 = std.math.maxInt(u32);

const LocationType = Location;
const max_indexed_nodes_limit = max_indexed_nodes;
const max_homes_limit = max_homes;
const no_node_value = no_node;

/// The candidate stores one globally keyed UID chain per home. The owner supplies
/// and retains both arrays; this module allocates no memory and keeps no mapping
/// other than the nodes and homes in those arrays.
pub fn Candidate(comptime Uid: type, comptime kind: CandidateKind) type {
    if (kind != .c2_chain) @compileError("this module currently implements the C2 chain candidate only");
    if (@alignOf(Uid) != 1 or @offsetOf(Uid, "bytes") != 0) {
        @compileError("UID must be the contract's byte-aligned opaque value");
    }

    const uid_byte_count = @field(@as(Uid, undefined), "bytes").len;
    if (uid_byte_count != @sizeOf(Uid)) {
        @compileError("UID bytes must cover the complete value without padding");
    }

    const AtomicByte = std.atomic.Value(u8);
    const AtomicBool = std.atomic.Value(bool);
    const AtomicIndex = std.atomic.Value(u32);
    const AtomicVersion = std.atomic.Value(u64);
    const Mutex = std.atomic.Mutex;

    return struct {
        const Self = @This();

        pub const uid_type = Uid;
        pub const Location = LocationType;
        pub const max_indexed_nodes = max_indexed_nodes_limit;
        pub const max_homes = max_homes_limit;
        pub const no_node = no_node_value;
        pub const Transactions = transaction.C2(Self);

        pub const Node = struct {
            uid_bytes: [uid_byte_count]AtomicByte,
            previous: AtomicIndex,
            next: AtomicIndex,
            home_index: AtomicIndex,
            linked: AtomicBool,
            location_kind: AtomicByte,
            location_table: AtomicIndex,
            location_slot: AtomicIndex,
            writer: Mutex,
        };

        pub const Home = struct {
            head: AtomicIndex,
            version: AtomicVersion,
            writer: Mutex,
        };

        /// A removal plan keeps the node writer lock until commit or abort.
        /// The owner can prepare every affected node before publishing any
        /// affected home, then commit the plans without a fallible step.
        pub const RemovePlan = struct {
            node_id: u32,
            home_id: u32,
        };

        /// An insertion staged for a caller-owned multi-home transaction.
        /// Adjacency is recomputed at commit, after every planned removal.
        pub const InsertPlan = struct {
            node_id: u32,
            home_id: u32,
            uid: Uid,
            location: LocationType,
        };

        /// Keep this token in caller-owned workspace for a multi-home commit.
        pub const Publication = struct {
            home_id: u32,
            initial_version: u64,
            has_inserts: bool = false,
        };

        /// Counts the two structural audit passes and complete neighbor UID
        /// snapshots used to check strict ordering.
        pub const AuditStats = struct {
            home_body: usize = 0,
            node_body: usize = 0,
            neighbor_payload_loads: usize = 0,
        };

        pub const Entry = struct {
            uid: Uid,
            location: LocationType,
        };

        nodes: []Node,
        homes: []Home,

        fn loadUid(node: *const Node) Uid {
            var uid: Uid = undefined;
            for (&uid.bytes, 0..) |*byte, index| {
                byte.* = node.uid_bytes[index].load(.seq_cst);
            }
            return uid;
        }

        fn storeUid(node: *Node, uid: Uid) void {
            for (uid.bytes, 0..) |byte, index| {
                node.uid_bytes[index].store(byte, .seq_cst);
            }
        }

        fn loadLocation(node: *const Node) !LocationType {
            const location_tag = node.location_kind.load(.seq_cst);
            return .{
                .kind = switch (location_tag) {
                    @intFromEnum(LocationKind.resource) => .resource,
                    @intFromEnum(LocationKind.partition) => .partition,
                    else => return error.InvalidIndexState,
                },
                .table = node.location_table.load(.seq_cst),
                .slot = node.location_slot.load(.seq_cst),
            };
        }

        /// Initializes caller-owned stable arrays. Call only before publishing
        /// the index to readers or writers.
        pub fn init(nodes: []Node, homes: []Home) Self {
            for (nodes) |*node| {
                for (&node.uid_bytes) |*byte| byte.* = AtomicByte.init(0);
                node.previous = AtomicIndex.init(no_node_value);
                node.next = AtomicIndex.init(no_node_value);
                node.home_index = AtomicIndex.init(no_node_value);
                node.linked = AtomicBool.init(false);
                node.location_kind = AtomicByte.init(0);
                node.location_table = AtomicIndex.init(0);
                node.location_slot = AtomicIndex.init(0);
                node.writer = .unlocked;
            }
            for (homes) |*home| {
                home.head = AtomicIndex.init(no_node_value);
                home.version = AtomicVersion.init(0);
                home.writer = .unlocked;
            }
            return .{ .nodes = nodes, .homes = homes };
        }

        /// Returns the home selected from every UID byte. A zero UID is valid.
        pub fn homeFor(self: *const Self, uid: Uid) !u32 {
            if (self.homes.len == 0 or self.homes.len > max_homes_limit) return error.InvalidCapacity;
            return @intCast(uid.hash() % @as(u64, @intCast(self.homes.len)));
        }

        /// Returns the current home for a linked owner node.
        pub fn homeForNode(self: *const Self, node_id: u32) !?u32 {
            if (@as(usize, node_id) >= self.nodes.len) return error.InvalidNode;
            const node = &self.nodes[node_id];
            if (!node.linked.load(.seq_cst)) return null;
            const home_id = node.home_index.load(.seq_cst);
            if (@as(usize, home_id) >= self.homes.len) return error.InvalidIndexState;
            return home_id;
        }

        /// Returns the opaque UID and location stored at one stable owner node.
        /// The caller supplies a writer-quiesced view; this accessor only checks
        /// the node-array bound and linked bit, leaving structural and owner
        /// mapping checks to their respective audits.
        pub fn entryAt(self: *const Self, node_id: u32) !?Entry {
            if (@as(usize, node_id) >= self.nodes.len) return error.InvalidNode;
            const node = &self.nodes[node_id];
            if (!node.linked.load(.seq_cst)) return null;
            return .{ .uid = loadUid(node), .location = try loadLocation(node) };
        }

        /// Audits a stable, caller-quiesced index using one pass over homes and
        /// one pass over nodes. The local reciprocal links, strict UID order,
        /// and rule that only the recorded home head has no predecessor prove
        /// that every linked component reaches that head: strict order rules
        /// out cycles, so an unlinked-from-head component cannot be orphaned.
        /// This method takes no locks and allocates no memory; the caller must
        /// exclude index writers for the entire audit.
        pub fn audit(self: *const Self) !AuditStats {
            if (self.nodes.len > max_indexed_nodes_limit or self.homes.len == 0 or self.homes.len > max_homes_limit)
                return error.InvalidCapacity;

            var stats: AuditStats = .{};

            for (self.homes, 0..) |*home, home_index| {
                stats.home_body += 1;
                const home_id: u32 = @intCast(home_index);
                const version = home.version.load(.seq_cst);
                if ((version & 1) != 0) return error.InvalidIndexState;

                const head = home.head.load(.seq_cst);
                if (head == no_node_value) continue;
                if (@as(usize, head) >= self.nodes.len) return error.InvalidIndexState;
                const head_node = &self.nodes[head];
                if (!head_node.linked.load(.seq_cst) or
                    head_node.home_index.load(.seq_cst) != home_id or
                    head_node.previous.load(.seq_cst) != no_node_value)
                {
                    return error.InvalidIndexState;
                }
            }

            for (self.nodes, 0..) |*node, node_index| {
                stats.node_body += 1;
                const node_id: u32 = @intCast(node_index);
                const linked = node.linked.load(.seq_cst);
                const previous = node.previous.load(.seq_cst);
                const next = node.next.load(.seq_cst);
                const home_id = node.home_index.load(.seq_cst);
                const location_kind = node.location_kind.load(.seq_cst);
                const location_table = node.location_table.load(.seq_cst);
                const location_slot = node.location_slot.load(.seq_cst);

                if (!linked) {
                    if (previous != no_node_value or next != no_node_value or home_id != no_node_value or
                        location_kind != 0 or location_table != 0 or location_slot != 0)
                    {
                        return error.InvalidIndexState;
                    }
                    continue;
                }

                if (@as(usize, home_id) >= self.homes.len or
                    location_kind > @intFromEnum(LocationKind.partition) or
                    location_table >= max_location_tables or
                    @as(usize, location_slot) >= max_indexed_nodes_limit)
                {
                    return error.InvalidIndexState;
                }

                const uid = loadUid(node);
                if (try self.homeFor(uid) != home_id) return error.InvalidIndexState;

                if (previous == no_node_value) {
                    if (self.homes[home_id].head.load(.seq_cst) != node_id)
                        return error.InvalidIndexState;
                } else {
                    if (@as(usize, previous) >= self.nodes.len or previous == node_id)
                        return error.InvalidIndexState;
                    const previous_node = &self.nodes[previous];
                    if (!previous_node.linked.load(.seq_cst) or
                        previous_node.home_index.load(.seq_cst) != home_id or
                        previous_node.next.load(.seq_cst) != node_id)
                    {
                        return error.InvalidIndexState;
                    }
                    const previous_uid = loadUid(previous_node);
                    stats.neighbor_payload_loads += 1;
                    if (previous_uid.order(uid) != .lt) return error.InvalidIndexState;
                }

                if (next != no_node_value) {
                    if (@as(usize, next) >= self.nodes.len or next == node_id)
                        return error.InvalidIndexState;
                    const next_node = &self.nodes[next];
                    if (!next_node.linked.load(.seq_cst) or
                        next_node.home_index.load(.seq_cst) != home_id or
                        next_node.previous.load(.seq_cst) != node_id)
                    {
                        return error.InvalidIndexState;
                    }
                }
            }

            return stats;
        }

        /// Finds the current complete location. A read that overlaps publication
        /// retries the home; atomic node fields prevent torn reads while a node
        /// is detached and reused.
        pub fn find(self: *const Self, uid: Uid) !?LocationType {
            const home_id = self.homeFor(uid) catch |err| {
                if (self.homes.len == 0) return null;
                return err;
            };
            const home = &self.homes[home_id];

            while (true) {
                const before = home.version.load(.seq_cst);
                if ((before & 1) != 0) {
                    std.atomic.spinLoopHint();
                    continue;
                }

                const found = self.findAtHome(home_id, uid) catch |err| {
                    const after_error = home.version.load(.seq_cst);
                    if (after_error != before or (after_error & 1) != 0) continue;
                    return err;
                };
                const after = home.version.load(.seq_cst);
                if (after != before or (after & 1) != 0) continue;
                return found;
            }
        }

        /// Adds one owner node. Duplicate full UIDs are reported so the owner
        /// can route a repeat push through the existing location returned by
        /// find() instead of creating a second index entry.
        pub fn insert(self: *Self, node_id: u32, uid: Uid, location: LocationType) !void {
            if (@as(usize, node_id) >= self.nodes.len) return error.InvalidNode;
            if (self.nodes.len > max_indexed_nodes_limit) return error.InvalidCapacity;
            const home_id = try self.homeFor(uid);
            const home = &self.homes[home_id];
            lock(&home.writer);
            defer home.writer.unlock();

            const publication = try self.preflightPublishLocked(home_id);
            const plan = try self.prepareInsertLocked(node_id, uid, location, home_id);
            var plan_locked = true;
            defer if (plan_locked) self.abortInsertLocked(plan);

            self.beginPublishLocked(publication);
            self.commitInsertLocked(plan);
            plan_locked = false;
            self.finishPublishLocked(publication);
        }

        /// Removes the current entry for an owner node. A stale home observation
        /// is retried if the same node is concurrently reused in another home.
        pub fn remove(self: *Self, node_id: u32) !void {
            return self.removeAfter(node_id, {}, struct {
                fn run(_: void) !void {}
            }.run);
        }

        /// Prepare the complete publication, then perform the fallible payload
        /// cleanup while the home is excluded. A failed action publishes nothing;
        /// after it succeeds, unlink/epoch publication cannot return an error.
        pub fn removeAfter(self: *Self, node_id: u32, context: anytype, comptime action: anytype) !void {
            if (@as(usize, node_id) >= self.nodes.len) return error.InvalidNode;

            while (true) {
                if (!self.nodes[node_id].linked.load(.seq_cst)) return error.NodeNotIndexed;
                const home_id = self.nodes[node_id].home_index.load(.seq_cst);
                if (@as(usize, home_id) >= self.homes.len) return error.InvalidIndexState;
                const home = &self.homes[home_id];
                lock(&home.writer);

                const publication = self.preflightPublishLocked(home_id) catch |err| {
                    home.writer.unlock();
                    if (self.nodeHomeChanged(node_id, home_id)) continue;
                    return err;
                };
                const plan = self.prepareRemoveLocked(node_id, home_id) catch |err| {
                    home.writer.unlock();
                    if (err == error.StaleHome or self.nodeHomeChanged(node_id, home_id)) continue;
                    return err;
                };

                action(context) catch |err| {
                    self.unlockNode(plan.node_id);
                    home.writer.unlock();
                    return err;
                };
                self.beginPublishLocked(publication);
                self.commitRemoveLocked(plan);
                self.finishPublishLocked(publication);
                home.writer.unlock();
                return;
            }
        }

        /// Home locks are exposed for owner transactions spanning a closed tree.
        /// The caller locks distinct homes in ascending order and releases them
        /// in reverse order.
        pub fn lockHome(self: *Self, home_id: u32) !void {
            if (@as(usize, home_id) >= self.homes.len) return error.InvalidHome;
            lock(&self.homes[home_id].writer);
        }

        pub fn unlockHome(self: *Self, home_id: u32) void {
            std.debug.assert(@as(usize, home_id) < self.homes.len);
            self.homes[home_id].writer.unlock();
        }

        /// Node locks are exposed for callers preparing a transaction that
        /// removes and reuses stable owner nodes. Callers lock distinct node
        /// IDs in ascending order after locking their homes, then release them
        /// in reverse order after publication finishes.
        pub fn lockNode(self: *Self, node_id: u32) !void {
            if (@as(usize, node_id) >= self.nodes.len) return error.InvalidNode;
            lock(&self.nodes[node_id].writer);
        }

        pub fn unlockNode(self: *Self, node_id: u32) void {
            std.debug.assert(@as(usize, node_id) < self.nodes.len);
            self.nodes[node_id].writer.unlock();
        }

        /// Check every affected home while all of its writer locks are held,
        /// before marking any of them odd. Versions never wrap.
        pub fn preflightPublishLocked(self: *const Self, home_id: u32) !Publication {
            if (@as(usize, home_id) >= self.homes.len) return error.InvalidHome;
            const version = self.homes[home_id].version.load(.seq_cst);
            if ((version & 1) != 0) return error.InvalidIndexState;
            if (version > std.math.maxInt(u64) - 2) return error.VersionExhausted;
            return .{ .home_id = home_id, .initial_version = version };
        }

        /// Validates one complete ordered chain while its home writer lock is
        /// held. Multi-home insertion transactions call this once per affected
        /// home before any resource release, so later no-fail insert commits
        /// can find their adjacency after all removals without repeating a
        /// fallible scan for every inserted node.
        pub fn preflightHomeLocked(self: *const Self, home_id: u32) !void {
            if (@as(usize, home_id) >= self.homes.len) return error.InvalidHome;

            var previous: u32 = no_node_value;
            var previous_uid: ?Uid = null;
            var cursor = self.homes[home_id].head.load(.seq_cst);
            var visits: usize = 0;
            while (cursor != no_node_value) {
                if (visits >= self.nodes.len) return error.InvalidIndexState;
                visits += 1;
                if (@as(usize, cursor) >= self.nodes.len) return error.InvalidIndexState;
                const node = &self.nodes[cursor];
                if (!node.linked.load(.seq_cst) or
                    node.home_index.load(.seq_cst) != home_id or
                    node.previous.load(.seq_cst) != previous)
                {
                    return error.InvalidIndexState;
                }
                const uid = loadUid(node);
                if ((try self.homeFor(uid)) != home_id) return error.InvalidIndexState;
                if (previous_uid) |older| {
                    if (older.order(uid) != .lt) return error.InvalidIndexState;
                }
                previous_uid = uid;
                previous = cursor;
                cursor = node.next.load(.seq_cst);
            }
        }

        /// Begin/end a no-fail publication after all prepares succeeded.
        pub fn beginPublishLocked(self: *Self, publication: Publication) void {
            const home = &self.homes[publication.home_id];
            std.debug.assert(home.version.load(.seq_cst) == publication.initial_version);
            home.version.store(publication.initial_version + 1, .seq_cst);
        }

        pub fn finishPublishLocked(self: *Self, publication: Publication) void {
            const home = &self.homes[publication.home_id];
            std.debug.assert(home.version.load(.seq_cst) == publication.initial_version + 1);
            home.version.store(publication.initial_version + 2, .seq_cst);
        }

        /// Prepares one removal while its home lock is held. The returned plan
        /// owns the node lock until commitRemoveLocked or abortRemoveLocked.
        pub fn prepareRemoveLocked(self: *Self, node_id: u32, home_id: u32) !RemovePlan {
            if (@as(usize, node_id) >= self.nodes.len) return error.InvalidNode;
            if (@as(usize, home_id) >= self.homes.len) return error.InvalidHome;
            try self.lockNode(node_id);
            errdefer self.unlockNode(node_id);

            return try self.preflightRemoveNodeLocked(node_id, home_id);
        }

        /// Validates a removal with its node mutex already held. This form is
        /// for caller-owned transactions that lock their complete sorted node
        /// set before preparing any mutation.
        pub fn preflightRemoveNodeLocked(self: *Self, node_id: u32, home_id: u32) !RemovePlan {
            if (@as(usize, node_id) >= self.nodes.len) return error.InvalidNode;
            if (@as(usize, home_id) >= self.homes.len) return error.InvalidHome;
            const node = &self.nodes[node_id];

            if (!node.linked.load(.seq_cst)) return error.NodeNotIndexed;
            if (node.home_index.load(.seq_cst) != home_id) return error.StaleHome;
            const uid = loadUid(node);
            if (try self.homeFor(uid) != home_id) return error.InvalidIndexState;

            const previous = node.previous.load(.seq_cst);
            const next = node.next.load(.seq_cst);
            if (previous == node_id or next == node_id) return error.InvalidIndexState;
            if (previous == no_node_value) {
                if (self.homes[home_id].head.load(.seq_cst) != node_id) return error.InvalidIndexState;
            } else {
                if (@as(usize, previous) >= self.nodes.len) return error.InvalidIndexState;
                const prev_node = &self.nodes[previous];
                if (!prev_node.linked.load(.seq_cst) or
                    prev_node.home_index.load(.seq_cst) != home_id or
                    prev_node.next.load(.seq_cst) != node_id or
                    loadUid(prev_node).order(uid) != .lt)
                {
                    return error.InvalidIndexState;
                }
            }
            if (next != no_node_value) {
                if (@as(usize, next) >= self.nodes.len) return error.InvalidIndexState;
                const next_node = &self.nodes[next];
                if (!next_node.linked.load(.seq_cst) or
                    next_node.home_index.load(.seq_cst) != home_id or
                    next_node.previous.load(.seq_cst) != node_id or
                    uid.order(loadUid(next_node)) != .lt)
                {
                    return error.InvalidIndexState;
                }
            }

            return .{ .node_id = node_id, .home_id = home_id };
        }

        /// Validates an insertion with its node mutex and target home mutex
        /// already held. `removed_from_home` must be null for a free node, or
        /// the old home of a node with a prepared removal in this transaction.
        /// The owner guarantees UID uniqueness across the transaction. The
        /// target chain must have passed preflightHomeLocked before this call.
        pub fn preflightInsertNodeLocked(
            self: *Self,
            node_id: u32,
            uid: Uid,
            location: LocationType,
            home_id: u32,
            removed_from_home: ?u32,
        ) !InsertPlan {
            if (@as(usize, node_id) >= self.nodes.len) return error.InvalidNode;
            if (self.nodes.len > max_indexed_nodes_limit) return error.InvalidCapacity;
            if (@as(usize, home_id) >= self.homes.len) return error.InvalidHome;
            if (try self.homeFor(uid) != home_id) return error.InvalidIndexState;

            const node = &self.nodes[node_id];
            if (removed_from_home) |old_home| {
                if (@as(usize, old_home) >= self.homes.len) return error.InvalidHome;
                if (!node.linked.load(.seq_cst)) return error.NodeNotIndexed;
                if (node.home_index.load(.seq_cst) != old_home) return error.StaleHome;
            } else {
                if (node.linked.load(.seq_cst) or node.home_index.load(.seq_cst) != no_node_value)
                    return error.NodeAlreadyIndexed;
                if (node.previous.load(.seq_cst) != no_node_value or node.next.load(.seq_cst) != no_node_value)
                    return error.InvalidIndexState;
            }

            return .{ .node_id = node_id, .home_id = home_id, .uid = uid, .location = location };
        }

        /// Aborts a prepared removal without changing the index.
        pub fn abortRemoveLocked(self: *Self, plan: RemovePlan) void {
            self.unlockNode(plan.node_id);
        }

        /// Commits a prepared removal without allocation or failure.
        /// Its home must be inside an odd publication and remain writer-locked.
        pub fn commitRemoveLocked(self: *Self, plan: RemovePlan) void {
            self.commitRemoveRetainingNodeLocked(plan);
            self.unlockNode(plan.node_id);
        }

        /// Detaches a prepared removal but retains its node mutex so the same
        /// transaction may reinitialize this stable node for an insertion.
        /// The matching home must be inside an odd publication and locked.
        pub fn commitRemoveRetainingNodeLocked(self: *Self, plan: RemovePlan) void {
            const node = &self.nodes[plan.node_id];
            const previous = node.previous.load(.seq_cst);
            const next = node.next.load(.seq_cst);
            if (previous == no_node_value) {
                self.homes[plan.home_id].head.store(next, .seq_cst);
            } else {
                self.nodes[previous].next.store(next, .seq_cst);
            }
            if (next != no_node_value) self.nodes[next].previous.store(previous, .seq_cst);

            node.previous.store(no_node_value, .seq_cst);
            node.next.store(no_node_value, .seq_cst);
            node.home_index.store(no_node_value, .seq_cst);
            node.linked.store(false, .seq_cst);
            node.location_kind.store(0, .seq_cst);
            node.location_table.store(0, .seq_cst);
            node.location_slot.store(0, .seq_cst);
        }

        /// Inserts a preflighted node by recomputing current adjacency under
        /// the locked, odd target home. This is intentionally no-fail: all
        /// removals and earlier insertions in the transaction are already
        /// reflected in the chain, and owner-level uniqueness is guaranteed.
        /// The node mutex remains held until the caller releases its sorted
        /// node lock set.
        pub fn commitInsertRetainingNodeLocked(self: *Self, plan: InsertPlan) void {
            const node = &self.nodes[plan.node_id];
            var previous: u32 = no_node_value;
            var cursor = self.homes[plan.home_id].head.load(.seq_cst);
            var visits: usize = 0;
            while (cursor != no_node_value) {
                std.debug.assert(visits < self.nodes.len);
                visits += 1;
                const current = &self.nodes[cursor];
                if (loadUid(current).order(plan.uid) != .lt) break;
                previous = cursor;
                cursor = current.next.load(.seq_cst);
            }

            storeUid(node, plan.uid);
            node.previous.store(previous, .seq_cst);
            node.next.store(cursor, .seq_cst);
            node.home_index.store(plan.home_id, .seq_cst);
            node.location_kind.store(@intFromEnum(plan.location.kind), .seq_cst);
            node.location_table.store(plan.location.table, .seq_cst);
            node.location_slot.store(plan.location.slot, .seq_cst);
            if (previous == no_node_value) {
                self.homes[plan.home_id].head.store(plan.node_id, .seq_cst);
            } else {
                self.nodes[previous].next.store(plan.node_id, .seq_cst);
            }
            if (cursor != no_node_value) self.nodes[cursor].previous.store(plan.node_id, .seq_cst);
            node.linked.store(true, .seq_cst);
        }

        fn prepareInsertLocked(
            self: *Self,
            node_id: u32,
            uid: Uid,
            location: LocationType,
            home_id: u32,
        ) !SingleInsertPlan {
            const node = &self.nodes[node_id];
            lock(&node.writer);
            errdefer node.writer.unlock();
            if (node.linked.load(.seq_cst) or node.home_index.load(.seq_cst) != no_node_value) {
                return error.NodeAlreadyIndexed;
            }

            var previous: u32 = no_node_value;
            var cursor = self.homes[home_id].head.load(.seq_cst);
            var previous_uid: ?Uid = null;
            var visits: usize = 0;
            while (cursor != no_node_value) {
                if (visits >= self.nodes.len) return error.InvalidIndexState;
                visits += 1;
                if (@as(usize, cursor) >= self.nodes.len) return error.InvalidIndexState;
                const current = &self.nodes[cursor];
                if (!current.linked.load(.seq_cst) or
                    current.home_index.load(.seq_cst) != home_id or
                    current.previous.load(.seq_cst) != previous)
                {
                    return error.InvalidIndexState;
                }
                const current_uid = loadUid(current);
                if ((try self.homeFor(current_uid)) != home_id) return error.InvalidIndexState;
                if (previous_uid) |older| {
                    if (older.order(current_uid) != .lt) return error.InvalidIndexState;
                }
                switch (current_uid.order(uid)) {
                    .eq => return error.DuplicateUid,
                    .gt => return .{
                        .node_id = node_id,
                        .home_id = home_id,
                        .previous = previous,
                        .next = cursor,
                        .uid = uid,
                        .location = location,
                    },
                    .lt => {
                        previous_uid = current_uid;
                        previous = cursor;
                        cursor = current.next.load(.seq_cst);
                    },
                }
            }
            return .{
                .node_id = node_id,
                .home_id = home_id,
                .previous = previous,
                .next = no_node_value,
                .uid = uid,
                .location = location,
            };
        }

        const SingleInsertPlan = struct {
            node_id: u32,
            home_id: u32,
            previous: u32,
            next: u32,
            uid: Uid,
            location: LocationType,
        };

        fn abortInsertLocked(self: *Self, plan: SingleInsertPlan) void {
            self.nodes[plan.node_id].writer.unlock();
        }

        fn commitInsertLocked(self: *Self, plan: SingleInsertPlan) void {
            const node = &self.nodes[plan.node_id];
            storeUid(node, plan.uid);
            node.previous.store(plan.previous, .seq_cst);
            node.next.store(plan.next, .seq_cst);
            node.home_index.store(plan.home_id, .seq_cst);
            node.location_kind.store(@intFromEnum(plan.location.kind), .seq_cst);
            node.location_table.store(plan.location.table, .seq_cst);
            node.location_slot.store(plan.location.slot, .seq_cst);
            if (plan.previous == no_node_value) {
                self.homes[plan.home_id].head.store(plan.node_id, .seq_cst);
            } else {
                self.nodes[plan.previous].next.store(plan.node_id, .seq_cst);
            }
            if (plan.next != no_node_value) self.nodes[plan.next].previous.store(plan.node_id, .seq_cst);
            node.linked.store(true, .seq_cst);
            node.writer.unlock();
        }

        fn findAtHome(self: *const Self, home_id: u32, uid: Uid) !?LocationType {
            var previous: u32 = no_node_value;
            var cursor = self.homes[home_id].head.load(.seq_cst);
            var previous_uid: ?Uid = null;
            var visits: usize = 0;
            while (cursor != no_node_value) {
                if (visits >= self.nodes.len) return error.InvalidIndexState;
                visits += 1;
                if (@as(usize, cursor) >= self.nodes.len) return error.InvalidIndexState;
                const node = &self.nodes[cursor];
                if (!node.linked.load(.seq_cst) or
                    node.home_index.load(.seq_cst) != home_id or
                    node.previous.load(.seq_cst) != previous)
                {
                    return error.InvalidIndexState;
                }
                const current_uid = loadUid(node);
                if ((try self.homeFor(current_uid)) != home_id) return error.InvalidIndexState;
                if (previous_uid) |older| {
                    if (older.order(current_uid) != .lt) return error.InvalidIndexState;
                }
                switch (current_uid.order(uid)) {
                    .eq => return try loadLocation(node),
                    .gt => return null,
                    .lt => {
                        previous_uid = current_uid;
                        previous = cursor;
                        cursor = node.next.load(.seq_cst);
                    },
                }
            }
            return null;
        }

        fn nodeHomeChanged(self: *const Self, node_id: u32, previous_home: u32) bool {
            const node = &self.nodes[node_id];
            return !node.linked.load(.seq_cst) or node.home_index.load(.seq_cst) != previous_home;
        }
    };
}

fn lock(mutex: *std.atomic.Mutex) void {
    while (!mutex.tryLock()) std.atomic.spinLoopHint();
}

test {
    _ = @import("index_tests.zig");
}
