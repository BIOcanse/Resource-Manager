const std = @import("std");
const transaction = @import("index_transactions.zig");

pub const CandidateKind = enum {
    b2_ordered,
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

// The separate million-resource owner profile uses 2R buckets in both
// candidates; the original maximum identity/profile gates are retained.
pub const max_indexed_nodes: usize = 1_048_576;
pub const max_buckets: usize = 2_097_152;
pub const no_bucket: u32 = std.math.maxInt(u32);

const LocationType = Location;
const max_indexed_nodes_limit = max_indexed_nodes;
const max_buckets_limit = max_buckets;
const no_bucket_value = no_bucket;

/// The caller-owned bucket closure is sorted and deduplicated in place before
/// it is locked. Sorting uses no allocator and only the supplied slice.
pub fn normalizeClosure(indices: []u32) usize {
    if (indices.len < 2) return indices.len;

    var root = indices.len / 2;
    while (root != 0) {
        root -= 1;
        siftDown(indices, root, indices.len);
    }

    var end = indices.len;
    while (end > 1) {
        end -= 1;
        swap(&indices[0], &indices[end]);
        siftDown(indices, 0, end);
    }

    var unique: usize = 1;
    for (indices[1..]) |index| {
        if (index == indices[unique - 1]) continue;
        indices[unique] = index;
        unique += 1;
    }
    return unique;
}

fn siftDown(items: []u32, initial_root: usize, end: usize) void {
    var root = initial_root;
    while (true) {
        const left = root * 2 + 1;
        if (left >= end) return;
        var larger = left;
        const right = left + 1;
        if (right < end and items[left] < items[right]) larger = right;
        if (items[root] >= items[larger]) return;
        swap(&items[root], &items[larger]);
        root = larger;
    }
}

fn swap(left: *u32, right: *u32) void {
    const value = left.*;
    left.* = right.*;
    right.* = value;
}

/// B2 candidate: one globally keyed UID map using ordered open addressing.
///
/// The owner supplies stable node and bucket arrays plus its own transaction
/// workspace. There is no allocation, second mapping, rebuild, or hidden
/// scratch. A bucket's index is also its publication home. Writers collect the
/// exact contiguous bucket closure(s) they may rewrite, normalize the union,
/// lock those buckets in ascending order, revalidate, preflight every version,
/// publish all homes odd, drain readers, and then commit without failure.
///
/// `find` pins only its consecutive probe prefix and holds every pin through its
/// result copy. If a later bucket is odd, it drops the entire prefix before
/// retrying; it never waits on an odd version while retaining an earlier pin.
pub fn Candidate(comptime Uid: type, comptime kind: CandidateKind) type {
    if (kind != .b2_ordered) @compileError("unsupported B2 candidate kind");
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
    const AtomicReaders = std.atomic.Value(u32);
    const Mutex = std.atomic.Mutex;

    return struct {
        const Self = @This();
        pub const Location = LocationType;
        pub const max_indexed_nodes = max_indexed_nodes_limit;
        pub const max_buckets = max_buckets_limit;
        pub const max_homes = max_buckets_limit;
        pub const no_bucket = no_bucket_value;
        pub const Transactions = transaction.B2(Self);
        const Relative = struct {
            phase: u8,
            offset: usize,
            key: Uid,
        };

        pub const Node = struct {
            uid_bytes: [uid_byte_count]AtomicByte,
            bucket_index: AtomicIndex,
            linked: AtomicBool,
            location_kind: AtomicByte,
            location_table: AtomicIndex,
            location_slot: AtomicIndex,
        };

        /// The `node_id` is the stable owner-metadata identity. Bucket contents
        /// store only this identity; UID and location remain in the node array.
        pub const Bucket = struct {
            node_id: AtomicIndex,
            version: AtomicVersion,
            readers: AtomicReaders,
            writer: Mutex,
        };

        /// In B2 each bucket is also its independently published home.
        pub const Home = Bucket;

        pub const InsertPlan = struct {
            node_id: u32,
            at: u32,
            empty: u32,
            uid: Uid,
            location: LocationType,
        };

        pub const RemovePlan = struct {
            node_id: u32,
        };

        pub const Entry = struct {
            uid: Uid,
            location: LocationType,
        };

        pub const Prefix = struct {
            start: u32,
            count: usize,
        };

        pub const ExpansionSample = struct {
            prefix: Prefix,
            bucket_visits: usize,
        };

        pub const BatchPreflightStats = struct {
            closure_validation_visits: usize = 0,
            removal_shift_prefix_visits: usize = 0,
        };

        pub const AuditStats = struct {
            home_body: usize = 0,
            bucket_body: usize = 0,
            node_body: usize = 0,
            neighbor_payload_loads: usize = 0,
        };

        pub const uid_type = Uid;

        /// Descriptor for one contiguous circular probe closure. The interval
        /// is acquired in numeric bucket order without a caller scratch array.
        const Closure = Prefix;

        nodes: []Node,
        buckets: []Bucket,
        /// Alias for owner composition: every B2 bucket is also a publication
        /// home, and both slices refer to the same caller-owned array.
        homes: []Home,

        fn loadUid(node: *const Node) Uid {
            var uid: Uid = undefined;
            for (&uid.bytes, 0..) |*byte, i| byte.* = node.uid_bytes[i].load(.seq_cst);
            return uid;
        }

        fn storeUid(node: *Node, uid: Uid) void {
            for (uid.bytes, 0..) |byte, i| node.uid_bytes[i].store(byte, .seq_cst);
        }

        fn loadLocation(node: *const Node) !LocationType {
            return .{
                .kind = switch (node.location_kind.load(.seq_cst)) {
                    @intFromEnum(LocationKind.resource) => .resource,
                    @intFromEnum(LocationKind.partition) => .partition,
                    else => return error.InvalidIndexState,
                },
                .table = node.location_table.load(.seq_cst),
                .slot = node.location_slot.load(.seq_cst),
            };
        }

        /// Call before publishing the candidate to any reader or writer.
        pub fn init(nodes: []Node, buckets: []Bucket) Self {
            for (nodes) |*node| {
                for (&node.uid_bytes) |*byte| byte.* = AtomicByte.init(0);
                node.bucket_index = AtomicIndex.init(no_bucket_value);
                node.linked = AtomicBool.init(false);
                node.location_kind = AtomicByte.init(0);
                node.location_table = AtomicIndex.init(0);
                node.location_slot = AtomicIndex.init(0);
            }
            for (buckets) |*bucket| {
                bucket.node_id = AtomicIndex.init(no_bucket_value);
                bucket.version = AtomicVersion.init(0);
                bucket.readers = AtomicReaders.init(0);
                bucket.writer = .unlocked;
            }
            return .{ .nodes = nodes, .buckets = buckets, .homes = buckets };
        }

        pub fn homeFor(self: *const Self, uid: Uid) !u32 {
            try self.checkCapacity();
            return @intCast(uid.hash() % @as(u64, @intCast(self.buckets.len)));
        }

        /// Returns one stable node's full UID and location. The caller must
        /// provide a writer-quiesced view; structural checks remain in audit.
        pub fn entryAt(self: *const Self, node_id: u32) !?Entry {
            if (@as(usize, node_id) >= self.nodes.len) return error.InvalidNode;
            const node = &self.nodes[node_id];
            if (!node.linked.load(.seq_cst)) return null;
            return .{ .uid = loadUid(node), .location = try loadLocation(node) };
        }

        /// Owner audits acquire every home in ascending order. In B2 a home is
        /// exactly one bucket, so these hooks acquire that bucket's writer lock.
        pub fn lockHome(self: *Self, home_id: u32) !void {
            try self.checkCapacity();
            if (@as(usize, home_id) >= self.homes.len) return error.InvalidHome;
            lock(&self.buckets[home_id].writer);
        }

        pub fn unlockHome(self: *Self, home_id: u32) void {
            std.debug.assert(@as(usize, home_id) < self.homes.len);
            self.buckets[home_id].writer.unlock();
        }

        /// Returns the opaque key at one stable owner node without interpreting
        /// its location fields. The caller supplies a writer-quiesced view.
        pub fn uidAt(self: *const Self, node_id: u32) !?Uid {
            if (@as(usize, node_id) >= self.nodes.len) return error.InvalidNode;
            const node = &self.nodes[node_id];
            if (!node.linked.load(.seq_cst)) return null;
            return loadUid(node);
        }

        /// Checks the bucket/node representation and B2 local-distance order
        /// in exactly one bucket pass and one node pass. Callers must exclude
        /// writers for the whole audit. The local-distance induction is the
        /// proof obligation for every query's early-stop order. In a run whose
        /// first bucket after an empty has offset j=0, let d be the key's probe
        /// distance to its bucket. The first edge sets d=0. Same-home edges
        /// advance d by one and UID order; different-home edges cannot increase
        /// d. Thus d<=j and j-d is the nondecreasing home offset from run start;
        /// equal offsets have one home and strictly increasing UIDs. A query
        /// starting at run offset k sees this same order as its relative tuple:
        /// homes before k, at k, and after k become phases 0, 1, and 2. This
        /// proves the early-stop ordering without another traversal. The node
        /// pass then proves bucket/node bijection by count and backlinks.
        pub fn audit(self: *const Self) !AuditStats {
            try self.checkCapacity();
            var stats: AuditStats = .{};
            var saw_empty = false;
            var occupied_count: usize = 0;

            for (self.buckets, 0..) |*bucket, bucket_index| {
                stats.home_body += 1;
                stats.bucket_body += 1;
                const version = bucket.version.load(.seq_cst);
                if ((version & 1) != 0) return error.InvalidIndexState;
                const node_id = bucket.node_id.load(.seq_cst);
                if (node_id == no_bucket_value) {
                    saw_empty = true;
                    continue;
                }
                occupied_count += 1;

                if (@as(usize, node_id) >= self.nodes.len) return error.InvalidIndexState;
                const node = &self.nodes[node_id];
                if (!node.linked.load(.seq_cst) or node.bucket_index.load(.seq_cst) != bucket_index)
                    return error.InvalidIndexState;
                const uid = loadUid(node);
                if (homeUnchecked(self, uid) >= self.buckets.len or
                    node.location_kind.load(.seq_cst) > @intFromEnum(LocationKind.partition) or
                    node.location_table.load(.seq_cst) >= 64 or
                    @as(usize, node.location_slot.load(.seq_cst)) >= max_indexed_nodes_limit)
                {
                    return error.InvalidIndexState;
                }

                const home = homeUnchecked(self, uid);
                const current_distance = self.distance(home, @intCast(bucket_index));
                const previous_bucket = &self.buckets[self.previousBucket(@intCast(bucket_index))];
                const previous_id = previous_bucket.node_id.load(.seq_cst);
                if (previous_id == no_bucket_value) {
                    if (current_distance != 0) return error.InvalidIndexState;
                } else {
                    if (@as(usize, previous_id) >= self.nodes.len) return error.InvalidIndexState;
                    const previous_node = &self.nodes[previous_id];
                    const previous_position = self.previousBucket(@intCast(bucket_index));
                    if (!previous_node.linked.load(.seq_cst) or
                        previous_node.bucket_index.load(.seq_cst) != previous_position)
                    {
                        return error.InvalidIndexState;
                    }
                    const previous_uid = loadUid(previous_node);
                    stats.neighbor_payload_loads += 1;
                    const previous_home = homeUnchecked(self, previous_uid);
                    const previous_distance = self.distance(previous_home, previous_position);
                    if (home == previous_home) {
                        if (previous_distance >= self.buckets.len - 1 or
                            current_distance != previous_distance + 1 or
                            previous_uid.order(uid) != .lt)
                        {
                            return error.InvalidIndexState;
                        }
                    } else if (current_distance > previous_distance) {
                        return error.InvalidIndexState;
                    }
                }
            }
            if (!saw_empty) return error.InvalidIndexState;

            var linked_count: usize = 0;
            for (self.nodes, 0..) |*node, node_index| {
                stats.node_body += 1;
                const node_id: u32 = @intCast(node_index);
                const linked = node.linked.load(.seq_cst);
                const bucket_index = node.bucket_index.load(.seq_cst);
                const location_kind = node.location_kind.load(.seq_cst);
                const location_table = node.location_table.load(.seq_cst);
                const location_slot = node.location_slot.load(.seq_cst);
                if (!linked) {
                    if (bucket_index != no_bucket_value or location_kind != 0 or location_table != 0 or location_slot != 0)
                        return error.InvalidIndexState;
                    continue;
                }
                linked_count += 1;
                if (@as(usize, bucket_index) >= self.buckets.len or
                    self.buckets[bucket_index].node_id.load(.seq_cst) != node_id or
                    location_kind > @intFromEnum(LocationKind.partition) or location_table >= 64 or
                    @as(usize, location_slot) >= max_indexed_nodes_limit)
                {
                    return error.InvalidIndexState;
                }
            }
            if (occupied_count != linked_count) return error.InvalidIndexState;
            return stats;
        }

        /// Return the complete resource-or-partition location, or null. Every
        /// key byte and location field is atomic; the bucket pin set prevents
        /// removal, movement, and node reuse until the result has been copied.
        pub fn find(self: *const Self, uid: Uid) !?LocationType {
            try self.checkCapacity();
            const home = self.homeUnchecked(uid);

            while (true) {
                var pinned: usize = 0;
                var previous: ?Relative = null;
                var cursor = home;
                var retry = false;
                var result: ?LocationType = null;
                var failure: ?anyerror = null;

                while (pinned < self.buckets.len) {
                    if (!self.pinBucket(cursor)) {
                        retry = true;
                        break;
                    }
                    pinned += 1;

                    const node_id = self.buckets[cursor].node_id.load(.seq_cst);
                    if (node_id == no_bucket_value) break;
                    if (@as(usize, node_id) >= self.nodes.len) {
                        failure = error.InvalidIndexState;
                        break;
                    }

                    const node = &self.nodes[node_id];
                    if (!node.linked.load(.seq_cst) or node.bucket_index.load(.seq_cst) != cursor) {
                        failure = error.InvalidIndexState;
                        break;
                    }
                    const current_uid = loadUid(node);
                    const current = self.relative(home, cursor, current_uid);
                    if (previous) |prior| {
                        if (order(prior, current) != .lt) {
                            failure = error.InvalidIndexState;
                            break;
                        }
                    }
                    previous = current;

                    const target = Relative{ .phase = 1, .offset = 0, .key = uid };
                    switch (order(current, target)) {
                        .eq => {
                            result = loadLocation(node) catch |err| {
                                failure = err;
                                break;
                            };
                            break;
                        },
                        .gt => break,
                        .lt => cursor = self.next(cursor),
                    }
                } else {
                    failure = error.InvalidIndexState;
                }

                self.unpinRange(home, pinned);
                if (retry) {
                    std.atomic.spinLoopHint();
                    continue;
                }
                if (failure) |err| return err;
                return result;
            }
        }

        /// Single-node insert for owner paths which have no per-call index
        /// workspace. Its temporary closure is a constant-size circular
        /// interval descriptor; all metadata remains in the owner's arrays.
        pub fn insert(self: *Self, node_id: u32, uid: Uid, location: LocationType) !void {
            try self.checkCapacity();
            if (@as(usize, node_id) >= self.nodes.len) return error.InvalidNode;

            while (true) {
                const home = self.homeUnchecked(uid);
                const closure = try self.sampleClosure(home, null);
                self.lockDescriptor(closure);
                var locked = true;
                defer if (locked) self.unlockDescriptor(closure);

                const used = self.validateDescriptor(closure, null) catch |err| {
                    if (err == error.ClosureChanged) continue;
                    return err;
                };
                const plan = self.prepareInsertDescriptor(node_id, uid, location, used) catch |err| {
                    if (err == error.ClosureChanged) continue;
                    return err;
                };

                self.beginDescriptor(closure, used);
                self.waitDescriptor(closure, used);
                self.commitInsertLocked(plan);
                self.finishDescriptor(closure, used);
                locked = false;
                self.unlockDescriptor(closure);
                return;
            }
        }

        /// Single-node removal for owner paths which have no per-call index
        /// workspace. A concurrent relocation restarts closure discovery.
        pub fn remove(self: *Self, node_id: u32) !void {
            try self.checkCapacity();
            if (@as(usize, node_id) >= self.nodes.len) return error.InvalidNode;

            while (true) {
                const node = &self.nodes[node_id];
                if (!node.linked.load(.seq_cst)) return error.NodeNotIndexed;
                const start = node.bucket_index.load(.seq_cst);
                if (@as(usize, start) >= self.buckets.len) return error.InvalidIndexState;
                const closure = self.sampleClosure(start, node_id) catch |err| {
                    if (err == error.StaleNodePosition or !node.linked.load(.seq_cst)) continue;
                    return err;
                };
                self.lockDescriptor(closure);
                var locked = true;
                defer if (locked) self.unlockDescriptor(closure);

                const used = self.validateDescriptor(closure, node_id) catch |err| {
                    if (err == error.ClosureChanged) continue;
                    return err;
                };
                self.validateRemoveDescriptor(node_id, closure, used) catch |err| {
                    if (err == error.ClosureChanged or err == error.StaleNodePosition) continue;
                    return err;
                };

                self.beginDescriptor(closure, used);
                self.waitDescriptor(closure, used);
                self.commitRemoveLocked(.{ .node_id = node_id });
                self.finishDescriptor(closure, used);
                locked = false;
                self.unlockDescriptor(closure);
                return;
            }
        }

        /// Sample the insertion probe closure into caller-owned scratch. The
        /// returned slice contains every bucket from the UID home through the
        /// first empty endpoint (or just the home when it is empty). A writer
        /// must normalize the union with any other closures, lock it, and call
        /// prepareInsertLocked to revalidate it before publication.
        pub fn collectInsertClosure(self: *const Self, uid: Uid, scratch: []u32) !usize {
            try self.checkCapacity();
            return self.collectRun(self.homeUnchecked(uid), null, scratch);
        }

        /// Sample the target's current bucket through the first empty endpoint.
        /// The target backlink is rechecked under reader pins; a concurrent
        /// relocation restarts the sample instead of returning a stale closure.
        pub fn collectRemoveClosure(self: *const Self, node_id: u32, scratch: []u32) !usize {
            try self.checkCapacity();
            if (@as(usize, node_id) >= self.nodes.len) return error.InvalidNode;

            while (true) {
                const node = &self.nodes[node_id];
                if (!node.linked.load(.seq_cst)) return error.NodeNotIndexed;
                const start = node.bucket_index.load(.seq_cst);
                if (@as(usize, start) >= self.buckets.len) return error.InvalidIndexState;

                const count = self.collectRun(start, node_id, scratch) catch |err| {
                    if (err == error.StaleNodePosition or !self.nodes[node_id].linked.load(.seq_cst)) continue;
                    return err;
                };
                return count;
            }
        }

        /// Samples one circular prefix without allocating a bucket list. The
        /// optional first node guards a remove-start against a stale backlink.
        pub fn samplePrefix(self: *const Self, start: u32, expected_first: ?u32) !Prefix {
            try self.checkCapacity();
            if (@as(usize, start) >= self.buckets.len) return error.InvalidIndexState;
            return self.sampleClosure(start, expected_first);
        }

        /// Samples from a probe bucket until the first empty bucket in the
        /// post-removal/prior-put simulation. Already-covered buckets use the
        /// caller's simulated contents; buckets beyond the current closure are
        /// read under one-bucket reader pins. The caller releases its closure
        /// locks before invoking this helper, then rebuilds and revalidates.
        pub fn samplePostSimulationPrefix(
            self: *const Self,
            start: u32,
            indices: []const u32,
            simulation_nodes: []const u32,
        ) !ExpansionSample {
            try self.checkCapacity();
            if (@as(usize, start) >= self.buckets.len) return error.InvalidIndexState;
            if (simulation_nodes.len < indices.len) return error.WorkspaceTooSmall;

            var total_visits: usize = 0;
            while (true) {
                var cursor = start;
                var count: usize = 0;
                var retry = false;

                while (count < self.buckets.len) {
                    total_visits += 1;
                    var node_id: u32 = undefined;
                    if (closurePosition(indices, cursor)) |position| {
                        node_id = simulation_nodes[position];
                    } else {
                        if (!self.pinBucket(cursor)) {
                            retry = true;
                            break;
                        }
                        const sampled_node = self.buckets[cursor].node_id.load(.seq_cst);
                        if (sampled_node != no_bucket_value) {
                            if (@as(usize, sampled_node) >= self.nodes.len) {
                                self.unpinRange(cursor, 1);
                                return error.InvalidIndexState;
                            }
                            const node = &self.nodes[sampled_node];
                            const valid = node.linked.load(.seq_cst) and
                                node.bucket_index.load(.seq_cst) == cursor;
                            self.unpinRange(cursor, 1);
                            if (!valid) return error.InvalidIndexState;
                        } else {
                            self.unpinRange(cursor, 1);
                        }
                        node_id = sampled_node;
                    }
                    count += 1;
                    if (node_id == no_bucket_value) {
                        return .{
                            .prefix = .{ .start = start, .count = count },
                            .bucket_visits = total_visits,
                        };
                    }
                    if (@as(usize, node_id) >= self.nodes.len) return error.InvalidIndexState;
                    cursor = self.next(cursor);
                }

                if (!retry) return error.IndexFull;
                std.atomic.spinLoopHint();
            }
        }

        /// Reads a node's bucket backlink while pinning that bucket. A
        /// concurrent shift is retried; a stable backlink/content mismatch is
        /// structural corruption.
        pub fn positionForNode(self: *const Self, node_id: u32) !u32 {
            try self.checkCapacity();
            if (@as(usize, node_id) >= self.nodes.len) return error.InvalidNode;
            const node = &self.nodes[node_id];

            while (true) {
                if (!node.linked.load(.seq_cst)) return error.NodeNotIndexed;
                const start = node.bucket_index.load(.seq_cst);
                if (@as(usize, start) >= self.buckets.len) return error.InvalidIndexState;
                if (!self.pinBucket(start)) {
                    std.atomic.spinLoopHint();
                    continue;
                }

                const bucket_node = self.buckets[start].node_id.load(.seq_cst);
                const still_linked = node.linked.load(.seq_cst);
                const current_start = node.bucket_index.load(.seq_cst);
                self.unpinRange(start, 1);

                if (!still_linked) return error.NodeNotIndexed;
                if (current_start != start) {
                    std.atomic.spinLoopHint();
                    continue;
                }
                if (bucket_node != node_id) return error.InvalidIndexState;
                return start;
            }
        }

        /// Samples a removal prefix from its current backlink and retries if
        /// another writer moved the node between the backlink and first pin.
        pub fn sampleRemovePrefix(self: *const Self, node_id: u32) !Prefix {
            if (@as(usize, node_id) >= self.nodes.len) return error.InvalidNode;
            while (true) {
                const start = try self.positionForNode(node_id);
                const prefix = self.sampleClosure(start, node_id) catch |err| {
                    if (err == error.StaleNodePosition or err == error.NodeNotIndexed) {
                        std.atomic.spinLoopHint();
                        continue;
                    }
                    return err;
                };
                return prefix;
            }
        }

        /// Acquire a normalized closure in physical bucket order. The caller
        /// must keep it locked through preflight, preparation, publication,
        /// commit, and finish. Same-node insert/remove serialization belongs to
        /// the owner metadata lock, as it does for C2.
        pub fn lockClosure(self: *Self, indices: []const u32) !void {
            try self.checkCapacity();
            try self.validateClosure(indices);
            for (indices) |index| lock(&self.buckets[index].writer);
        }

        pub fn unlockClosure(self: *Self, indices: []const u32) void {
            self.validateClosure(indices) catch unreachable;
            self.unlockClosurePrepared(indices);
        }

        /// Releases a closure whose ordering and bounds were checked before
        /// acquisition. Prepared transactions use this path so cleanup does
        /// not repeat a whole-closure validation pass.
        pub fn unlockClosurePrepared(self: *Self, indices: []const u32) void {
            var cursor = indices.len;
            while (cursor != 0) {
                cursor -= 1;
                self.buckets[indices[cursor]].writer.unlock();
            }
        }

        /// Fill caller-owned versions after checking every bucket before any
        /// publication. The `indices` and `versions` slices are parallel.
        pub fn preflightPublishLocked(
            self: *const Self,
            indices: []const u32,
            versions: []u64,
        ) !void {
            try self.validateClosure(indices);
            if (versions.len < indices.len) return error.WorkspaceTooSmall;
            for (indices, 0..) |index, i| {
                const version = self.buckets[index].version.load(.seq_cst);
                if ((version & 1) != 0) return error.InvalidIndexState;
                if (version > std.math.maxInt(u64) - 2) return error.VersionExhausted;
                versions[i] = version;
            }
        }

        /// No-fail publication hooks. Keep every bucket lock until finish.
        pub fn beginPublishLocked(self: *Self, indices: []const u32, versions: []const u64) void {
            std.debug.assert(indices.len <= versions.len);
            for (indices, 0..) |index, i| {
                const bucket = &self.buckets[index];
                std.debug.assert(bucket.version.load(.seq_cst) == versions[i]);
                bucket.version.store(versions[i] + 1, .seq_cst);
            }
        }

        pub fn waitReadersLocked(self: *const Self, indices: []const u32) void {
            for (indices) |index| {
                while (self.buckets[index].readers.load(.seq_cst) != 0) std.atomic.spinLoopHint();
            }
        }

        pub fn finishPublishLocked(self: *Self, indices: []const u32, versions: []const u64) void {
            std.debug.assert(indices.len <= versions.len);
            for (indices, 0..) |index, i| {
                const bucket = &self.buckets[index];
                std.debug.assert(bucket.version.load(.seq_cst) == versions[i] + 1);
                bucket.version.store(versions[i] + 2, .seq_cst);
            }
        }

        /// Prepare against a locked closure. If a sampled operation grew beyond
        /// the union, return ClosureChanged before any write; the owner releases
        /// all locks, resamples, and retries. Duplicate full UIDs are reported.
        pub fn prepareInsertLocked(
            self: *const Self,
            node_id: u32,
            uid: Uid,
            location: LocationType,
            locked: []const u32,
        ) !InsertPlan {
            if (@as(usize, node_id) >= self.nodes.len) return error.InvalidNode;
            try self.validateClosure(locked);
            const node = &self.nodes[node_id];
            if (node.linked.load(.seq_cst) or node.bucket_index.load(.seq_cst) != no_bucket_value) {
                return error.NodeAlreadyIndexed;
            }

            const home = self.homeUnchecked(uid);
            const target = Relative{ .phase = 1, .offset = 0, .key = uid };
            var cursor = home;
            var previous: ?Relative = null;
            var insertion_at: ?u32 = null;

            for (0..self.buckets.len) |_| {
                if (!contains(locked, cursor)) return error.ClosureChanged;
                const current_node_id = self.buckets[cursor].node_id.load(.seq_cst);
                if (current_node_id == no_bucket_value) {
                    return .{
                        .node_id = node_id,
                        .at = insertion_at orelse cursor,
                        .empty = cursor,
                        .uid = uid,
                        .location = location,
                    };
                }
                if (@as(usize, current_node_id) >= self.nodes.len) return error.InvalidIndexState;

                const current_node = &self.nodes[current_node_id];
                if (!current_node.linked.load(.seq_cst) or
                    current_node.bucket_index.load(.seq_cst) != cursor)
                {
                    return error.InvalidIndexState;
                }
                const current_uid = loadUid(current_node);
                if (current_uid.eql(uid)) return error.DuplicateUid;
                const current = self.relative(home, cursor, current_uid);
                if (previous) |prior| if (order(prior, current) != .lt) return error.InvalidIndexState;
                if (insertion_at == null and order(current, target) == .gt) insertion_at = cursor;
                previous = current;
                cursor = self.next(cursor);
            }
            return error.IndexFull;
        }

        /// Commit only after every affected bucket version is odd and every
        /// reader pin has drained. Every destination is inside `locked`, so this
        /// routine has no error path or allocation.
        pub fn commitInsertLocked(self: *Self, plan: InsertPlan) void {
            var destination = plan.empty;
            while (destination != plan.at) {
                const source = self.previousBucket(destination);
                const moved = self.buckets[source].node_id.load(.seq_cst);
                std.debug.assert(moved != no_bucket_value and @as(usize, moved) < self.nodes.len);
                self.buckets[destination].node_id.store(moved, .seq_cst);
                self.nodes[moved].bucket_index.store(destination, .seq_cst);
                destination = source;
            }

            const node = &self.nodes[plan.node_id];
            storeUid(node, plan.uid);
            node.location_kind.store(@intFromEnum(plan.location.kind), .seq_cst);
            node.location_table.store(plan.location.table, .seq_cst);
            node.location_slot.store(plan.location.slot, .seq_cst);
            node.bucket_index.store(plan.at, .seq_cst);
            self.buckets[plan.at].node_id.store(plan.node_id, .seq_cst);
            node.linked.store(true, .seq_cst);
        }

        /// Batch publication hook. Earlier removals and inserts may have moved
        /// the cluster after preflight, so resolve the target and first empty
        /// bucket again inside the locked closure. `prepare` simulates this
        /// same sequence against caller-owned scratch before it returns; this
        /// commit therefore has no recoverable error path.
        pub fn commitInsertRecomputedLocked(
            self: *Self,
            plan: InsertPlan,
            locked: []const u32,
        ) void {
            const node = &self.nodes[plan.node_id];
            std.debug.assert(!node.linked.load(.seq_cst) and
                node.bucket_index.load(.seq_cst) == no_bucket_value);

            const home = self.homeUnchecked(plan.uid);
            const target = Relative{ .phase = 1, .offset = 0, .key = plan.uid };
            var cursor = home;
            var previous: ?Relative = null;
            var insertion_at: ?u32 = null;
            var found_empty: ?u32 = null;
            for (0..self.buckets.len) |_| {
                std.debug.assert(contains(locked, cursor));
                const current_node_id = self.buckets[cursor].node_id.load(.seq_cst);
                if (current_node_id == no_bucket_value) {
                    found_empty = cursor;
                    break;
                }
                std.debug.assert(@as(usize, current_node_id) < self.nodes.len);
                const current_node = &self.nodes[current_node_id];
                std.debug.assert(current_node.linked.load(.seq_cst) and
                    current_node.bucket_index.load(.seq_cst) == cursor);
                const current_uid = loadUid(current_node);
                std.debug.assert(!current_uid.eql(plan.uid));
                const current = relative(self, home, cursor, current_uid);
                if (previous) |prior| std.debug.assert(order(prior, current) == .lt);
                if (insertion_at == null and order(current, target) == .gt) insertion_at = cursor;
                previous = current;
                cursor = self.next(cursor);
            }
            const empty = found_empty orelse unreachable;
            const at = insertion_at orelse empty;

            var destination = empty;
            while (destination != at) {
                const source = self.previousBucket(destination);
                std.debug.assert(contains(locked, source));
                const moved = self.buckets[source].node_id.load(.seq_cst);
                std.debug.assert(moved != no_bucket_value and @as(usize, moved) < self.nodes.len);
                self.buckets[destination].node_id.store(moved, .seq_cst);
                self.nodes[moved].bucket_index.store(destination, .seq_cst);
                destination = source;
            }

            storeUid(node, plan.uid);
            node.location_kind.store(@intFromEnum(plan.location.kind), .seq_cst);
            node.location_table.store(plan.location.table, .seq_cst);
            node.location_slot.store(plan.location.slot, .seq_cst);
            node.bucket_index.store(at, .seq_cst);
            self.buckets[at].node_id.store(plan.node_id, .seq_cst);
            node.linked.store(true, .seq_cst);
        }

        /// Batch callers must deduplicate node IDs before preparing plans.
        /// Preparation takes no per-node lock and stores no stale bucket slot;
        /// same-node exclusion is supplied by the owner metadata transaction.
        pub fn prepareRemoveLocked(self: *const Self, node_id: u32, locked: []const u32) !RemovePlan {
            if (@as(usize, node_id) >= self.nodes.len) return error.InvalidNode;
            try self.validateClosure(locked);
            return self.prepareRemoveValidatedLocked(node_id, locked, false, {});
        }

        /// Validate the supplied closure once for a batch, then validate each
        /// target and only its affected forward prefix. This avoids repeating
        /// the O(K) sorted-closure check for every removal in an O(k) tree
        /// replacement.
        pub fn prepareRemoveBatchLocked(self: *const Self, plans: []RemovePlan, locked: []const u32) !void {
            return self.prepareRemoveBatchImpl(false, plans, locked, {});
        }

        pub fn prepareRemoveBatchWithStatsLocked(
            self: *const Self,
            plans: []RemovePlan,
            locked: []const u32,
            stats: *BatchPreflightStats,
        ) !void {
            stats.* = .{};
            return self.prepareRemoveBatchImpl(true, plans, locked, stats);
        }

        fn prepareRemoveBatchImpl(
            self: *const Self,
            comptime track_stats: bool,
            plans: []RemovePlan,
            locked: []const u32,
            stats: if (track_stats) *BatchPreflightStats else void,
        ) !void {
            if (comptime track_stats) {
                try self.validateClosureCounted(true, locked, &stats.closure_validation_visits);
            } else {
                try self.validateClosure(locked);
            }
            for (plans) |*plan| {
                if (comptime track_stats) {
                    plan.* = try self.prepareRemoveValidatedCounted(
                        plan.node_id,
                        locked,
                        &stats.removal_shift_prefix_visits,
                    );
                } else {
                    plan.* = try self.prepareRemoveValidatedLocked(plan.node_id, locked, false, {});
                }
            }
        }

        fn prepareRemoveValidatedCounted(
            self: *const Self,
            node_id: u32,
            locked: []const u32,
            visits: *usize,
        ) !RemovePlan {
            return self.prepareRemoveValidatedImpl(true, node_id, locked, visits);
        }

        fn prepareRemoveValidatedLocked(
            self: *const Self,
            node_id: u32,
            locked: []const u32,
            comptime track_stats: bool,
            visits: if (track_stats) *usize else void,
        ) !RemovePlan {
            return self.prepareRemoveValidatedImpl(track_stats, node_id, locked, visits);
        }

        fn prepareRemoveValidatedImpl(
            self: *const Self,
            comptime track_stats: bool,
            node_id: u32,
            locked: []const u32,
            visits: if (track_stats) *usize else void,
        ) !RemovePlan {
            if (@as(usize, node_id) >= self.nodes.len) return error.InvalidNode;
            const node = &self.nodes[node_id];
            if (!node.linked.load(.seq_cst)) return error.NodeNotIndexed;
            const start = node.bucket_index.load(.seq_cst);
            if (@as(usize, start) >= self.buckets.len) return error.InvalidIndexState;
            if (self.buckets[start].node_id.load(.seq_cst) != node_id) return error.InvalidIndexState;

            var cursor = start;
            for (0..self.buckets.len) |_| {
                if (comptime track_stats) visits.* += 1;
                if (!contains(locked, cursor)) return error.ClosureChanged;
                const current_node_id = self.buckets[cursor].node_id.load(.seq_cst);
                if (current_node_id == no_bucket_value) {
                    if (cursor == start) return error.InvalidIndexState;
                    return .{ .node_id = node_id };
                }
                if (@as(usize, current_node_id) >= self.nodes.len) return error.InvalidIndexState;
                const current_node = &self.nodes[current_node_id];
                if (!current_node.linked.load(.seq_cst) or current_node.bucket_index.load(.seq_cst) != cursor) {
                    return error.InvalidIndexState;
                }
                cursor = self.next(cursor);
            }
            return error.InvalidIndexState;
        }

        /// A previously prepared distinct node may have shifted because an
        /// earlier removal in the same publication committed. Resolve its
        /// current backlink again and splice its current ordered cluster.
        pub fn commitRemoveLocked(self: *Self, plan: RemovePlan) void {
            const node = &self.nodes[plan.node_id];
            const target = node.bucket_index.load(.seq_cst);
            std.debug.assert(node.linked.load(.seq_cst));
            std.debug.assert(@as(usize, target) < self.buckets.len);
            std.debug.assert(self.buckets[target].node_id.load(.seq_cst) == plan.node_id);

            var hole = target;
            var source = self.next(hole);
            var reached_stop = false;
            for (0..self.buckets.len) |_| {
                const moved = self.buckets[source].node_id.load(.seq_cst);
                if (moved == no_bucket_value) {
                    reached_stop = true;
                    break;
                }
                std.debug.assert(@as(usize, moved) < self.nodes.len);
                const moved_node = &self.nodes[moved];
                std.debug.assert(moved_node.linked.load(.seq_cst));
                std.debug.assert(moved_node.bucket_index.load(.seq_cst) == source);
                const moved_uid = loadUid(moved_node);
                if (self.homeUnchecked(moved_uid) == source) {
                    reached_stop = true;
                    break;
                }

                self.buckets[hole].node_id.store(moved, .seq_cst);
                moved_node.bucket_index.store(hole, .seq_cst);
                hole = source;
                source = self.next(source);
            }
            std.debug.assert(reached_stop);

            self.buckets[hole].node_id.store(no_bucket_value, .seq_cst);
            node.bucket_index.store(no_bucket_value, .seq_cst);
            node.linked.store(false, .seq_cst);
            node.location_kind.store(0, .seq_cst);
            node.location_table.store(0, .seq_cst);
            node.location_slot.store(0, .seq_cst);
        }

        fn sampleClosure(self: *const Self, start: u32, expected_first: ?u32) !Closure {
            while (true) {
                var cursor = start;
                var count: usize = 0;
                var retry = false;

                while (count < self.buckets.len) {
                    if (!self.pinBucket(cursor)) {
                        retry = true;
                        break;
                    }
                    const node_id = self.buckets[cursor].node_id.load(.seq_cst);
                    self.unpinRange(cursor, 1);

                    if (count == 0) {
                        if (expected_first) |expected| {
                            if (node_id != expected) return error.StaleNodePosition;
                        }
                    }
                    count += 1;
                    if (node_id == no_bucket_value) return .{ .start = start, .count = count };
                    if (@as(usize, node_id) >= self.nodes.len) return error.InvalidIndexState;
                    const node = &self.nodes[node_id];
                    if (!node.linked.load(.seq_cst) or node.bucket_index.load(.seq_cst) != cursor)
                        return error.InvalidIndexState;
                    cursor = self.next(cursor);
                }

                if (!retry) return error.IndexFull;
                std.atomic.spinLoopHint();
            }
        }

        fn lockDescriptor(self: *Self, closure: Closure) void {
            const end = @as(usize, closure.start) + closure.count;
            if (end <= self.buckets.len) {
                for (closure.start..end) |bucket_id| lock(&self.buckets[bucket_id].writer);
                return;
            }
            const wrapped_end = end - self.buckets.len;
            for (0..wrapped_end) |bucket_id| lock(&self.buckets[bucket_id].writer);
            for (closure.start..self.buckets.len) |bucket_id| lock(&self.buckets[bucket_id].writer);
        }

        fn unlockDescriptor(self: *Self, closure: Closure) void {
            const end = @as(usize, closure.start) + closure.count;
            if (end <= self.buckets.len) {
                var cursor = end;
                while (cursor > closure.start) {
                    cursor -= 1;
                    self.buckets[cursor].writer.unlock();
                }
                return;
            }
            var cursor = self.buckets.len;
            while (cursor > closure.start) {
                cursor -= 1;
                self.buckets[cursor].writer.unlock();
            }
            cursor = end - self.buckets.len;
            while (cursor != 0) {
                cursor -= 1;
                self.buckets[cursor].writer.unlock();
            }
        }

        /// Revalidates the sampled interval after locking. If another writer
        /// changed its endpoint, the caller drops all locks and samples again.
        /// Returns the currently needed prefix through its first empty bucket.
        fn validateDescriptor(self: *const Self, closure: Closure, expected_first: ?u32) !usize {
            if (closure.count == 0 or closure.count > self.buckets.len or
                @as(usize, closure.start) >= self.buckets.len) return error.InvalidClosure;

            var cursor = closure.start;
            for (0..closure.count) |offset| {
                const bucket = &self.buckets[cursor];
                const version = bucket.version.load(.seq_cst);
                if ((version & 1) != 0) return error.InvalidIndexState;
                if (version > std.math.maxInt(u64) - 2) return error.VersionExhausted;
                const node_id = bucket.node_id.load(.seq_cst);
                if (offset == 0) {
                    if (expected_first) |expected| {
                        if (node_id != expected) return error.ClosureChanged;
                    }
                }
                if (node_id == no_bucket_value) return offset + 1;
                if (@as(usize, node_id) >= self.nodes.len) return error.InvalidIndexState;
                const node = &self.nodes[node_id];
                if (!node.linked.load(.seq_cst) or node.bucket_index.load(.seq_cst) != cursor)
                    return error.InvalidIndexState;
                if (homeUnchecked(self, loadUid(node)) >= self.buckets.len) return error.InvalidIndexState;
                cursor = self.next(cursor);
            }
            return error.ClosureChanged;
        }

        fn beginDescriptor(self: *Self, closure: Closure, used: usize) void {
            var cursor = closure.start;
            for (0..used) |_| {
                const bucket = &self.buckets[cursor];
                const version = bucket.version.load(.seq_cst);
                std.debug.assert((version & 1) == 0 and version <= std.math.maxInt(u64) - 2);
                bucket.version.store(version + 1, .seq_cst);
                cursor = self.next(cursor);
            }
        }

        fn waitDescriptor(self: *const Self, closure: Closure, used: usize) void {
            var cursor = closure.start;
            for (0..used) |_| {
                while (self.buckets[cursor].readers.load(.seq_cst) != 0) std.atomic.spinLoopHint();
                cursor = self.next(cursor);
            }
        }

        fn finishDescriptor(self: *Self, closure: Closure, used: usize) void {
            var cursor = closure.start;
            for (0..used) |_| {
                const bucket = &self.buckets[cursor];
                const version = bucket.version.load(.seq_cst);
                std.debug.assert((version & 1) == 1);
                bucket.version.store(version + 1, .seq_cst);
                cursor = self.next(cursor);
            }
        }

        fn prepareInsertDescriptor(
            self: *const Self,
            node_id: u32,
            uid: Uid,
            location: LocationType,
            used: usize,
        ) !InsertPlan {
            const node = &self.nodes[node_id];
            if (node.linked.load(.seq_cst) or node.bucket_index.load(.seq_cst) != no_bucket_value)
                return error.NodeAlreadyIndexed;

            const home = self.homeUnchecked(uid);
            const target = Relative{ .phase = 1, .offset = 0, .key = uid };
            var cursor = home;
            var previous: ?Relative = null;
            var insertion_at: ?u32 = null;
            for (0..used) |_| {
                const current_node_id = self.buckets[cursor].node_id.load(.seq_cst);
                if (current_node_id == no_bucket_value) {
                    return .{
                        .node_id = node_id,
                        .at = insertion_at orelse cursor,
                        .empty = cursor,
                        .uid = uid,
                        .location = location,
                    };
                }
                if (@as(usize, current_node_id) >= self.nodes.len) return error.InvalidIndexState;
                const current_node = &self.nodes[current_node_id];
                if (!current_node.linked.load(.seq_cst) or current_node.bucket_index.load(.seq_cst) != cursor)
                    return error.InvalidIndexState;
                const current_uid = loadUid(current_node);
                if (current_uid.eql(uid)) return error.DuplicateUid;
                const current = relative(self, home, cursor, current_uid);
                if (previous) |prior| if (order(prior, current) != .lt) return error.InvalidIndexState;
                if (insertion_at == null and order(current, target) == .gt) insertion_at = cursor;
                previous = current;
                cursor = self.next(cursor);
            }
            return error.ClosureChanged;
        }

        fn validateRemoveDescriptor(self: *const Self, node_id: u32, closure: Closure, used: usize) !void {
            if (self.buckets[closure.start].node_id.load(.seq_cst) != node_id)
                return error.StaleNodePosition;
            var cursor = closure.start;
            for (0..used) |offset| {
                const current = self.buckets[cursor].node_id.load(.seq_cst);
                if (current == no_bucket_value) {
                    if (offset == 0) return error.InvalidIndexState;
                    return;
                }
                if (@as(usize, current) >= self.nodes.len) return error.InvalidIndexState;
                const node = &self.nodes[current];
                if (!node.linked.load(.seq_cst) or node.bucket_index.load(.seq_cst) != cursor)
                    return error.InvalidIndexState;
                cursor = self.next(cursor);
            }
            return error.ClosureChanged;
        }

        fn collectRun(self: *const Self, start: u32, expected_node: ?u32, scratch: []u32) !usize {
            while (true) {
                var pinned: usize = 0;
                var cursor = start;
                var retry = false;
                var failure: ?anyerror = null;
                var completed = false;

                while (pinned < self.buckets.len) {
                    if (pinned == scratch.len) {
                        failure = error.WorkspaceTooSmall;
                        break;
                    }
                    if (!self.pinBucket(cursor)) {
                        retry = true;
                        break;
                    }
                    scratch[pinned] = cursor;
                    pinned += 1;

                    const current_node_id = self.buckets[cursor].node_id.load(.seq_cst);
                    if (pinned == 1) {
                        if (expected_node) |node_id| {
                            if (current_node_id != node_id) {
                                failure = error.StaleNodePosition;
                                break;
                            }
                        }
                    }
                    if (current_node_id == no_bucket_value) {
                        completed = true;
                        break;
                    }
                    if (@as(usize, current_node_id) >= self.nodes.len) {
                        failure = error.InvalidIndexState;
                        break;
                    }
                    const node = &self.nodes[current_node_id];
                    if (!node.linked.load(.seq_cst) or node.bucket_index.load(.seq_cst) != cursor) {
                        failure = error.InvalidIndexState;
                        break;
                    }
                    cursor = self.next(cursor);
                }

                self.unpinRange(start, pinned);
                if (retry) {
                    std.atomic.spinLoopHint();
                    continue;
                }
                if (failure) |err| return err;
                if (!completed) return error.IndexFull;
                return pinned;
            }
        }

        fn pinBucket(self: *const Self, bucket_id: u32) bool {
            const bucket = &self.buckets[bucket_id];
            const before = bucket.version.load(.seq_cst);
            if ((before & 1) != 0) return false;

            while (true) {
                const readers = bucket.readers.load(.seq_cst);
                if (readers == std.math.maxInt(u32)) {
                    std.atomic.spinLoopHint();
                    continue;
                }
                if (bucket.readers.cmpxchgWeak(readers, readers + 1, .seq_cst, .seq_cst) == null) break;
            }

            const after = bucket.version.load(.seq_cst);
            if (after == before and (after & 1) == 0) return true;
            const previous_readers = bucket.readers.fetchSub(1, .seq_cst);
            std.debug.assert(previous_readers != 0);
            return false;
        }

        fn unpinRange(self: *const Self, start: u32, count: usize) void {
            var cursor = start;
            for (0..count) |_| {
                const previous_readers = self.buckets[cursor].readers.fetchSub(1, .seq_cst);
                std.debug.assert(previous_readers != 0);
                cursor = self.next(cursor);
            }
        }

        fn validateClosure(self: *const Self, indices: []const u32) !void {
            return self.validateClosureCounted(false, indices, {});
        }

        fn validateClosureCounted(
            self: *const Self,
            comptime track_stats: bool,
            indices: []const u32,
            visits: if (track_stats) *usize else void,
        ) !void {
            if (indices.len == 0) return error.EmptyClosure;
            var previous: ?u32 = null;
            for (indices) |index| {
                if (comptime track_stats) visits.* += 1;
                if (@as(usize, index) >= self.buckets.len) return error.InvalidClosure;
                if (previous) |prior| if (prior >= index) return error.InvalidClosure;
                previous = index;
            }
        }

        fn checkCapacity(self: *const Self) !void {
            if (self.nodes.len > max_indexed_nodes_limit or self.buckets.len < 2 or
                self.buckets.len > max_buckets_limit or self.nodes.len >= self.buckets.len)
            {
                return error.InvalidCapacity;
            }
        }

        fn homeUnchecked(self: *const Self, uid: Uid) u32 {
            return @intCast(uid.hash() % @as(u64, @intCast(self.buckets.len)));
        }

        fn next(self: *const Self, index: u32) u32 {
            return if (@as(usize, index) + 1 == self.buckets.len) 0 else index + 1;
        }

        fn previousBucket(self: *const Self, index: u32) u32 {
            return if (index == 0) @intCast(self.buckets.len - 1) else index - 1;
        }

        fn distance(self: *const Self, from: u32, to: u32) usize {
            if (to >= from) return @as(usize, to - from);
            return self.buckets.len - @as(usize, from) + @as(usize, to);
        }

        fn relative(self: *const Self, start: u32, position: u32, key: Uid) Relative {
            const home = self.homeUnchecked(key);
            const from_home = self.distance(home, position);
            const from_start = self.distance(start, position);
            return .{
                .phase = if (from_home > from_start) 0 else if (from_home == from_start) 1 else 2,
                .offset = self.distance(start, home),
                .key = key,
            };
        }
    };
}

fn order(left: anytype, right: @TypeOf(left)) std.math.Order {
    if (left.phase != right.phase) return std.math.order(left.phase, right.phase);
    if (left.offset != right.offset) return std.math.order(left.offset, right.offset);
    return left.key.order(right.key);
}

fn contains(sorted: []const u32, wanted: u32) bool {
    return closurePosition(sorted, wanted) != null;
}

fn closurePosition(sorted: []const u32, wanted: u32) ?usize {
    var low: usize = 0;
    var high = sorted.len;
    while (low < high) {
        const middle = low + (high - low) / 2;
        if (sorted[middle] < wanted) {
            low = middle + 1;
        } else {
            high = middle;
        }
    }
    return if (low < sorted.len and sorted[low] == wanted) low else null;
}

fn lock(mutex: *std.atomic.Mutex) void {
    while (!mutex.tryLock()) std.atomic.spinLoopHint();
}

const uid_types = @import("../uid.zig");
const Uid128 = uid_types.Identifier(128);
const Uid256 = uid_types.Identifier(256);
const Candidate128 = Candidate(Uid128, .b2_ordered);
const Candidate256 = Candidate(Uid256, .b2_ordered);

fn uid128(value: u16) Uid128 {
    var uid: Uid128 = .{ .bytes = @splat(0) };
    uid.bytes[0] = @intCast(value >> 8);
    uid.bytes[1] = @intCast(value & 0xff);
    return uid;
}

fn resourceLocation(table: u32, slot: u32) Location {
    return .{ .kind = .resource, .table = table, .slot = slot };
}

fn partitionLocation(table: u32, slot: u32) Location {
    return .{ .kind = .partition, .table = table, .slot = slot };
}

fn collectCollisions(map: *const Candidate128, output: []Uid128) !void {
    const selected_home = try map.homeFor(uid128(0));
    var count: usize = 0;
    for (0..65_536) |value| {
        const key = uid128(@intCast(value));
        if (try map.homeFor(key) != selected_home) continue;
        output[count] = key;
        count += 1;
        if (count == output.len) return;
    }
    return error.NotEnoughCollisions;
}

fn uid128ForHome(map: *const Candidate128, target: u32, start: usize, used: []const Uid128) !Uid128 {
    var candidate = start;
    while (candidate <= std.math.maxInt(u16)) : (candidate += 1) {
        const key = uid128(@intCast(candidate));
        if (try map.homeFor(key) != target) continue;
        var duplicate = false;
        for (used) |prior| duplicate = duplicate or prior.eql(key);
        if (!duplicate) return key;
    }
    return error.NotEnoughCollisions;
}

fn publishInsert(
    map: *Candidate128,
    node_id: u32,
    key: Uid128,
    location: Location,
    scratch: []u32,
    versions: []u64,
) !void {
    const collected = try map.collectInsertClosure(key, scratch);
    const count = normalizeClosure(scratch[0..collected]);
    const closure = scratch[0..count];
    try map.lockClosure(closure);
    defer map.unlockClosure(closure);

    try map.preflightPublishLocked(closure, versions);
    const plan = try map.prepareInsertLocked(node_id, key, location, closure);
    map.beginPublishLocked(closure, versions);
    map.waitReadersLocked(closure);
    map.commitInsertLocked(plan);
    map.finishPublishLocked(closure, versions);
}

fn publishRemove(
    map: *Candidate128,
    node_id: u32,
    scratch: []u32,
    versions: []u64,
) !void {
    const collected = try map.collectRemoveClosure(node_id, scratch);
    const count = normalizeClosure(scratch[0..collected]);
    const closure = scratch[0..count];
    try map.lockClosure(closure);
    defer map.unlockClosure(closure);

    try map.preflightPublishLocked(closure, versions);
    const plan = try map.prepareRemoveLocked(node_id, closure);
    map.beginPublishLocked(closure, versions);
    map.waitReadersLocked(closure);
    map.commitRemoveLocked(plan);
    map.finishPublishLocked(closure, versions);
}

fn publishInsert256(
    map: *Candidate256,
    node_id: u32,
    key: Uid256,
    location: Location,
    scratch: []u32,
    versions: []u64,
) !void {
    const collected = try map.collectInsertClosure(key, scratch);
    const count = normalizeClosure(scratch[0..collected]);
    const closure = scratch[0..count];
    try map.lockClosure(closure);
    defer map.unlockClosure(closure);

    try map.preflightPublishLocked(closure, versions);
    const plan = try map.prepareInsertLocked(node_id, key, location, closure);
    map.beginPublishLocked(closure, versions);
    map.waitReadersLocked(closure);
    map.commitInsertLocked(plan);
    map.finishPublishLocked(closure, versions);
}

test "B2 accepts zero and full-width UIDs through a sub-2x table" {
    try std.testing.expectEqual(@as(usize, 1_048_576), max_indexed_nodes);
    try std.testing.expectEqual(@as(usize, 2_097_152), max_buckets);

    var nodes: [3]Candidate128.Node = undefined;
    var buckets: [4]Candidate128.Bucket = undefined;
    var map = Candidate128.init(&nodes, &buckets);
    var scratch: [4]u32 = undefined;
    var versions: [4]u64 = undefined;

    const zero: Uid128 = .{ .bytes = @splat(0) };
    try publishInsert(&map, 0, zero, resourceLocation(0, 7), &scratch, &versions);
    try publishInsert(&map, 1, uid128(0x1234), partitionLocation(2, 8), &scratch, &versions);
    try publishInsert(&map, 2, uid128(0xabcd), resourceLocation(3, 9), &scratch, &versions);

    try std.testing.expectEqual(resourceLocation(0, 7), (try map.find(zero)).?);
    try std.testing.expectEqual(partitionLocation(2, 8), (try map.find(uid128(0x1234))).?);
    try std.testing.expectEqual(resourceLocation(3, 9), (try map.find(uid128(0xabcd))).?);
    const stats128 = try map.audit();
    try std.testing.expectEqual(@as(usize, buckets.len), stats128.bucket_body);
    try std.testing.expectEqual(@as(usize, nodes.len), stats128.node_body);

    var wide_nodes: [2]Candidate256.Node = undefined;
    var wide_buckets: [3]Candidate256.Bucket = undefined;
    var wide_map = Candidate256.init(&wide_nodes, &wide_buckets);
    var wide_scratch: [3]u32 = undefined;
    var wide_versions: [3]u64 = undefined;
    var tail_a: Uid256 = .{ .bytes = @splat(0) };
    var tail_b = tail_a;
    tail_a.bytes[31] = 0x11;
    tail_b.bytes[31] = 0x12;
    try publishInsert256(&wide_map, 0, tail_a, resourceLocation(7, 3), &wide_scratch, &wide_versions);
    try publishInsert256(&wide_map, 1, tail_b, partitionLocation(8, 4), &wide_scratch, &wide_versions);
    try std.testing.expectEqual(resourceLocation(7, 3), (try wide_map.find(tail_a)).?);
    try std.testing.expectEqual(partitionLocation(8, 4), (try wide_map.find(tail_b)).?);
    try std.testing.expect(!tail_a.eql(tail_b));
    const stats256 = try wide_map.audit();
    try std.testing.expectEqual(@as(usize, wide_buckets.len), stats256.bucket_body);
    try std.testing.expectEqual(@as(usize, wide_nodes.len), stats256.node_body);
}

test "B2 audit rejects a locally reachable but UID-reversed same-home cluster" {
    var nodes: [2]Candidate128.Node = undefined;
    var buckets: [5]Candidate128.Bucket = undefined;
    var map = Candidate128.init(&nodes, &buckets);
    var scratch: [5]u32 = undefined;
    var versions: [5]u64 = undefined;
    var keys: [2]Uid128 = undefined;
    try collectCollisions(&map, &keys);
    try std.testing.expect(keys[0].order(keys[1]) == .lt);

    try publishInsert(&map, 0, keys[1], resourceLocation(1, 0), &scratch, &versions);
    try publishInsert(&map, 1, keys[0], resourceLocation(1, 1), &scratch, &versions);
    const home = try map.homeFor(keys[0]);
    const next = if (@as(usize, home) + 1 == buckets.len) 0 else home + 1;
    try std.testing.expectEqual(@as(u32, 1), buckets[home].node_id.load(.seq_cst));
    try std.testing.expectEqual(@as(u32, 0), buckets[next].node_id.load(.seq_cst));
    const valid_stats = try map.audit();
    try std.testing.expectEqual(@as(usize, buckets.len), valid_stats.bucket_body);
    try std.testing.expectEqual(@as(usize, nodes.len), valid_stats.node_body);
    try std.testing.expectEqual(@as(usize, 1), valid_stats.neighbor_payload_loads);

    buckets[home].node_id.store(0, .seq_cst);
    buckets[next].node_id.store(1, .seq_cst);
    nodes[0].bucket_index.store(home, .seq_cst);
    nodes[1].bucket_index.store(next, .seq_cst);
    try std.testing.expectError(error.InvalidIndexState, map.audit());
}

test "B2 collision shifts and prepared removals resolve moved node backlinks" {
    var nodes: [5]Candidate128.Node = undefined;
    var buckets: [8]Candidate128.Bucket = undefined;
    var map = Candidate128.init(&nodes, &buckets);
    var scratch: [8]u32 = undefined;
    var versions: [8]u64 = undefined;
    var keys: [4]Uid128 = undefined;
    try collectCollisions(&map, &keys);

    // Insert in reverse order to exercise the candidate's ordered shifts.
    var i = keys.len;
    while (i != 0) {
        i -= 1;
        try publishInsert(&map, @intCast(i), keys[i], resourceLocation(1, @intCast(i)), &scratch, &versions);
    }
    try std.testing.expectEqual(resourceLocation(1, 0), (try map.find(keys[0])).?);
    try std.testing.expectEqual(resourceLocation(1, 3), (try map.find(keys[3])).?);

    // Sample every closure before publication, then remove two distinct nodes
    // in one no-fail phase. Removing the first shifts the second, whose commit
    // must resolve its current atomic backlink instead of using a stale slot.
    var union_workspace: [32]u32 = undefined;
    var union_len: usize = 0;
    for ([_]u32{ 0, 1 }) |node_id| {
        var one: [8]u32 = undefined;
        const one_len = try map.collectRemoveClosure(node_id, &one);
        for (one[0..one_len]) |bucket_id| {
            union_workspace[union_len] = bucket_id;
            union_len += 1;
        }
    }
    const closed_len = normalizeClosure(union_workspace[0..union_len]);
    const closure = union_workspace[0..closed_len];
    try map.lockClosure(closure);
    defer map.unlockClosure(closure);

    try map.preflightPublishLocked(closure, &versions);
    const first = try map.prepareRemoveLocked(0, closure);
    const second = try map.prepareRemoveLocked(1, closure);
    map.beginPublishLocked(closure, &versions);
    map.waitReadersLocked(closure);
    map.commitRemoveLocked(first);
    map.commitRemoveLocked(second);
    map.finishPublishLocked(closure, &versions);

    try std.testing.expectEqual(@as(?Location, null), try map.find(keys[0]));
    try std.testing.expectEqual(@as(?Location, null), try map.find(keys[1]));
    try std.testing.expectEqual(resourceLocation(1, 2), (try map.find(keys[2])).?);
    try std.testing.expectEqual(resourceLocation(1, 3), (try map.find(keys[3])).?);
}

test "B2 workspace and version exhaustion failures precede mutation" {
    var nodes: [2]Candidate128.Node = undefined;
    var buckets: [3]Candidate128.Bucket = undefined;
    var map = Candidate128.init(&nodes, &buckets);
    var scratch: [3]u32 = undefined;
    var versions: [3]u64 = undefined;
    var empty_scratch: [0]u32 = .{};
    const key = uid128(0x9001);

    try std.testing.expectError(error.WorkspaceTooSmall, map.collectInsertClosure(key, &empty_scratch));
    try publishInsert(&map, 0, key, partitionLocation(5, 13), &scratch, &versions);

    const sampled = try map.collectRemoveClosure(0, &scratch);
    try std.testing.expect(sampled >= 2);
    const exhausted_bucket = scratch[sampled - 1];
    const node_bucket_before = map.nodes[0].bucket_index.load(.seq_cst);
    const bucket_node_before = map.buckets[exhausted_bucket].node_id.load(.seq_cst);
    map.buckets[exhausted_bucket].version.store(std.math.maxInt(u64) - 1, .seq_cst);
    try std.testing.expectError(error.VersionExhausted, publishRemove(&map, 0, &scratch, &versions));

    try std.testing.expect(map.nodes[0].linked.load(.seq_cst));
    try std.testing.expectEqual(node_bucket_before, map.nodes[0].bucket_index.load(.seq_cst));
    try std.testing.expectEqual(bucket_node_before, map.buckets[exhausted_bucket].node_id.load(.seq_cst));
    try std.testing.expectEqual(partitionLocation(5, 13), (try map.find(key)).?);
}

const ChurnContext = struct {
    map: *Candidate128,
    key: Uid128,
    start: *std.atomic.Value(bool),
    succeeded: *std.atomic.Value(bool),
};

fn churnOtherNode(context: *ChurnContext) void {
    while (!context.start.load(.seq_cst)) std.atomic.spinLoopHint();
    var scratch: [8]u32 = undefined;
    var versions: [8]u64 = undefined;
    for (0..2_000) |_| {
        publishInsert(context.map, 1, context.key, partitionLocation(4, 6), &scratch, &versions) catch {
            context.succeeded.store(false, .seq_cst);
            return;
        };
        publishRemove(context.map, 1, &scratch, &versions) catch {
            context.succeeded.store(false, .seq_cst);
            return;
        };
    }
}

test "B2 readers do not miss or tear a stable key while its cluster shifts" {
    var nodes: [3]Candidate128.Node = undefined;
    var buckets: [8]Candidate128.Bucket = undefined;
    var map = Candidate128.init(&nodes, &buckets);
    var scratch: [8]u32 = undefined;
    var versions: [8]u64 = undefined;
    var keys: [2]Uid128 = undefined;
    try collectCollisions(&map, &keys);
    try std.testing.expect(keys[0].order(keys[1]) == .lt);

    try publishInsert(&map, 0, keys[1], resourceLocation(9, 12), &scratch, &versions);
    var start = std.atomic.Value(bool).init(false);
    var succeeded = std.atomic.Value(bool).init(true);
    var context = ChurnContext{
        .map = &map,
        .key = keys[0],
        .start = &start,
        .succeeded = &succeeded,
    };
    var writer = try std.Thread.spawn(.{}, churnOtherNode, .{&context});
    start.store(true, .seq_cst);
    for (0..4_000) |_| {
        try std.testing.expectEqual(resourceLocation(9, 12), (try map.find(keys[1])).?);
    }
    writer.join();
    try std.testing.expect(succeeded.load(.seq_cst));
}

test "B2 workspace-free single insert remove and stable entry access" {
    var nodes: [3]Candidate128.Node = undefined;
    var buckets: [8]Candidate128.Bucket = undefined;
    var map = Candidate128.init(&nodes, &buckets);
    const first = uid128(0x4100);
    const second = uid128(0x4200);

    try map.insert(0, first, resourceLocation(3, 10));
    try map.insert(1, second, partitionLocation(4, 11));
    try std.testing.expectError(error.DuplicateUid, map.insert(2, first, resourceLocation(7, 2)));
    try std.testing.expectEqual(
        Candidate128.Entry{ .uid = second, .location = partitionLocation(4, 11) },
        (try map.entryAt(1)).?,
    );
    try map.remove(0);
    try std.testing.expectEqual(@as(?Location, null), try map.find(first));
    try std.testing.expectEqual(partitionLocation(4, 11), (try map.find(second)).?);
    try map.remove(1);
    try std.testing.expectEqual(@as(?Candidate128.Entry, null), try map.entryAt(1));

    const stats = try map.audit();
    try std.testing.expectEqual(@as(usize, buckets.len), stats.bucket_body);
    try std.testing.expectEqual(@as(usize, nodes.len), stats.node_body);
    try std.testing.expectEqual(@as(usize, 0), stats.neighbor_payload_loads);
}

test "B2 workspace-free single removal preflights version exhaustion" {
    var nodes: [2]Candidate128.Node = undefined;
    var buckets: [5]Candidate128.Bucket = undefined;
    var map = Candidate128.init(&nodes, &buckets);
    const key = uid128(0x5300);
    try map.insert(0, key, resourceLocation(0, 1));
    const bucket_id = nodes[0].bucket_index.load(.seq_cst);
    buckets[bucket_id].version.store(std.math.maxInt(u64) - 1, .seq_cst);

    try std.testing.expectError(error.VersionExhausted, map.remove(0));
    try std.testing.expectEqual(resourceLocation(0, 1), (try map.find(key)).?);
    try std.testing.expect(nodes[0].linked.load(.seq_cst));
}

test "B2 transaction intervals merge wrapping, adjacent, and gapped closures" {
    {
        var nodes: [3]Candidate128.Node = undefined;
        var buckets: [7]Candidate128.Bucket = undefined;
        var map = Candidate128.init(&nodes, &buckets);
        var keys: [3]Uid128 = undefined;
        keys[0] = try uid128ForHome(&map, 5, 0, &.{});
        keys[1] = try uid128ForHome(&map, 5, 1, keys[0..1]);
        keys[2] = try uid128ForHome(&map, 5, 2, keys[0..2]);
        for (keys, 0..) |key, i| try map.insert(@intCast(i), key, resourceLocation(0, @intCast(i)));

        var operations: [2]Candidate128.Transactions.Operation = undefined;
        var segments: [4]Candidate128.Transactions.Segment = undefined;
        var indices: [4]u32 = undefined;
        var versions: [4]u64 = undefined;
        var removals: [2]Candidate128.RemovePlan = undefined;
        var simulation_nodes: [4]u32 = undefined;
        var simulation_uids: [4]Uid128 = undefined;
        var simulation_node_ids: [4]u32 = undefined;
        var simulation_backlinks: [4]u32 = undefined;
        var workspace: Candidate128.Transactions.Workspace = .{
            .operations = &operations,
            .segments = &segments,
            .indices = &indices,
            .versions = &versions,
            .removals = &removals,
            .simulation_nodes = &simulation_nodes,
            .simulation_uids = &simulation_uids,
            .simulation_node_ids = &simulation_node_ids,
            .simulation_backlinks = &simulation_backlinks,
        };
        const removal_ids = [_]u32{ 0, 1 };
        var stats: Candidate128.Transactions.PrepareStats = .{};
        var prepared = try Candidate128.Transactions.prepareWithStats(
            &map,
            &removal_ids,
            &.{},
            &workspace,
            &stats,
        );
        defer prepared.abort();

        try std.testing.expectEqual(@as(u32, 5), nodes[0].bucket_index.load(.seq_cst));
        try std.testing.expectEqual(@as(u32, 6), nodes[1].bucket_index.load(.seq_cst));
        try std.testing.expectEqual(@as(u32, 0), nodes[2].bucket_index.load(.seq_cst));
        try std.testing.expectEqualSlices(u32, &.{ 0, 1, 5, 6 }, prepared.indices);
        // Both removals share the same sampled run prefix.
        try std.testing.expectEqual(@as(usize, 1), stats.operation_segments);
        try std.testing.expectEqual(@as(usize, 2), stats.merged_segments);
        try std.testing.expectEqual(@as(usize, 4), stats.closure_buckets_enumerated);
        try std.testing.expectEqual(@as(usize, 3), stats.closure_validation_passes);
        try std.testing.expectEqual(@as(usize, 12), stats.closure_validation_visits);
        try std.testing.expectEqual(@as(usize, 7), stats.removal_shift_prefix_visits);
    }

    {
        var nodes: [2]Candidate128.Node = undefined;
        var buckets: [7]Candidate128.Bucket = undefined;
        var map = Candidate128.init(&nodes, &buckets);
        const first = try uid128ForHome(&map, 1, 0, &.{});
        const second = try uid128ForHome(&map, 3, 0, &.{first});
        try map.insert(0, first, resourceLocation(0, 0));
        try map.insert(1, second, resourceLocation(0, 1));

        var operations: [2]Candidate128.Transactions.Operation = undefined;
        var segments: [4]Candidate128.Transactions.Segment = undefined;
        var indices: [4]u32 = undefined;
        var versions: [4]u64 = undefined;
        var removals: [2]Candidate128.RemovePlan = undefined;
        var simulation_nodes: [4]u32 = undefined;
        var simulation_uids: [4]Uid128 = undefined;
        var simulation_node_ids: [4]u32 = undefined;
        var simulation_backlinks: [4]u32 = undefined;
        var workspace: Candidate128.Transactions.Workspace = .{
            .operations = &operations,
            .segments = &segments,
            .indices = &indices,
            .versions = &versions,
            .removals = &removals,
            .simulation_nodes = &simulation_nodes,
            .simulation_uids = &simulation_uids,
            .simulation_node_ids = &simulation_node_ids,
            .simulation_backlinks = &simulation_backlinks,
        };
        var stats: Candidate128.Transactions.PrepareStats = .{};
        var prepared = try Candidate128.Transactions.prepareWithStats(&map, &.{ 0, 1 }, &.{}, &workspace, &stats);
        defer prepared.abort();
        try std.testing.expectEqualSlices(u32, &.{ 1, 2, 3, 4 }, prepared.indices);
        try std.testing.expectEqual(@as(usize, 1), stats.merged_segments);
    }

    {
        var nodes: [2]Candidate128.Node = undefined;
        var buckets: [7]Candidate128.Bucket = undefined;
        var map = Candidate128.init(&nodes, &buckets);
        const first = try uid128ForHome(&map, 1, 0, &.{});
        const second = try uid128ForHome(&map, 4, 0, &.{first});
        try map.insert(0, first, resourceLocation(0, 0));
        try map.insert(1, second, resourceLocation(0, 1));

        var operations: [2]Candidate128.Transactions.Operation = undefined;
        var segments: [4]Candidate128.Transactions.Segment = undefined;
        var indices: [4]u32 = undefined;
        var versions: [4]u64 = undefined;
        var removals: [2]Candidate128.RemovePlan = undefined;
        var simulation_nodes: [4]u32 = undefined;
        var simulation_uids: [4]Uid128 = undefined;
        var simulation_node_ids: [4]u32 = undefined;
        var simulation_backlinks: [4]u32 = undefined;
        var workspace: Candidate128.Transactions.Workspace = .{
            .operations = &operations,
            .segments = &segments,
            .indices = &indices,
            .versions = &versions,
            .removals = &removals,
            .simulation_nodes = &simulation_nodes,
            .simulation_uids = &simulation_uids,
            .simulation_node_ids = &simulation_node_ids,
            .simulation_backlinks = &simulation_backlinks,
        };
        var stats: Candidate128.Transactions.PrepareStats = .{};
        var prepared = try Candidate128.Transactions.prepareWithStats(&map, &.{ 0, 1 }, &.{}, &workspace, &stats);
        defer prepared.abort();
        try std.testing.expectEqualSlices(u32, &.{ 1, 2, 4, 5 }, prepared.indices);
        try std.testing.expectEqual(@as(usize, 2), stats.merged_segments);
    }
}

test "B2 batch removal rejects a corrupted shifted neighbor before publication" {
    var nodes: [3]Candidate128.Node = undefined;
    var buckets: [7]Candidate128.Bucket = undefined;
    var map = Candidate128.init(&nodes, &buckets);
    var keys: [3]Uid128 = undefined;
    keys[0] = try uid128ForHome(&map, 2, 0, &.{});
    keys[1] = try uid128ForHome(&map, 2, 1, keys[0..1]);
    keys[2] = try uid128ForHome(&map, 2, 2, keys[0..2]);
    for (keys, 0..) |key, i| try map.insert(@intCast(i), key, resourceLocation(1, @intCast(i)));

    var bucket_snapshot: [7]u32 = undefined;
    var version_snapshot: [7]u64 = undefined;
    for (&buckets, 0..) |*bucket, i| {
        bucket_snapshot[i] = bucket.node_id.load(.seq_cst);
        version_snapshot[i] = bucket.version.load(.seq_cst);
    }
    nodes[1].bucket_index.store(5, .seq_cst);

    var operations: [1]Candidate128.Transactions.Operation = undefined;
    var segments: [2]Candidate128.Transactions.Segment = undefined;
    var indices: [4]u32 = undefined;
    var versions: [4]u64 = undefined;
    var removals: [1]Candidate128.RemovePlan = undefined;
    var simulation_nodes: [4]u32 = undefined;
    var simulation_uids: [4]Uid128 = undefined;
    var simulation_node_ids: [4]u32 = undefined;
    var simulation_backlinks: [4]u32 = undefined;
    var workspace: Candidate128.Transactions.Workspace = .{
        .operations = &operations,
        .segments = &segments,
        .indices = &indices,
        .versions = &versions,
        .removals = &removals,
        .simulation_nodes = &simulation_nodes,
        .simulation_uids = &simulation_uids,
        .simulation_node_ids = &simulation_node_ids,
        .simulation_backlinks = &simulation_backlinks,
    };
    try expectB2PreparedError(
        Candidate128.Transactions.prepare(&map, &.{0}, &.{}, &workspace),
        error.InvalidIndexState,
    );

    for (&buckets, 0..) |*bucket, i| {
        try std.testing.expectEqual(bucket_snapshot[i], bucket.node_id.load(.seq_cst));
        try std.testing.expectEqual(version_snapshot[i], bucket.version.load(.seq_cst));
    }
    try std.testing.expect(nodes[0].linked.load(.seq_cst));
    try std.testing.expect(nodes[1].linked.load(.seq_cst));
    try std.testing.expect(nodes[2].linked.load(.seq_cst));
}

test "B2 batch transaction removes and reuses nodes across homes" {
    var nodes: [4]Candidate128.Node = undefined;
    var buckets: [11]Candidate128.Bucket = undefined;
    var map = Candidate128.init(&nodes, &buckets);
    const old_a = uid128(0x0101);
    const home_a = try map.homeFor(old_a);
    var old_b = uid128(0x0102);
    var candidate: u16 = 0x0103;
    while (try map.homeFor(old_b) == home_a) : (candidate += 1) old_b = uid128(candidate);
    const new_a = uid128(0x7001);
    const new_b = uid128(0x9002);

    try map.insert(0, old_a, resourceLocation(1, 10));
    try map.insert(1, old_b, partitionLocation(2, 11));

    var operations: [4]Candidate128.Transactions.Operation = undefined;
    var segments: [8]Candidate128.Transactions.Segment = undefined;
    var indices: [11]u32 = undefined;
    var versions: [11]u64 = undefined;
    var removals: [2]Candidate128.RemovePlan = undefined;
    var inserts: [2]Candidate128.InsertPlan = undefined;
    var simulation_nodes: [11]u32 = undefined;
    var simulation_uids: [11]Uid128 = undefined;
    var simulation_node_ids: [13]u32 = undefined;
    var simulation_backlinks: [13]u32 = undefined;
    var workspace: Candidate128.Transactions.Workspace = .{
        .operations = &operations,
        .segments = &segments,
        .indices = &indices,
        .versions = &versions,
        .removals = &removals,
        .inserts = &inserts,
        .simulation_nodes = &simulation_nodes,
        .simulation_uids = &simulation_uids,
        .simulation_node_ids = &simulation_node_ids,
        .simulation_backlinks = &simulation_backlinks,
    };
    const puts = [_]Candidate128.Transactions.Put{
        .{ .node_id = 0, .uid = new_a, .location = resourceLocation(3, 12) },
        .{ .node_id = 1, .uid = new_b, .location = partitionLocation(4, 13) },
    };
    var prepared = try Candidate128.Transactions.prepare(&map, &.{ 0, 1 }, &puts, &workspace);
    defer prepared.abort();
    prepared.commit();

    try std.testing.expectEqual(@as(?Location, null), try map.find(old_a));
    try std.testing.expectEqual(@as(?Location, null), try map.find(old_b));
    try std.testing.expectEqual(resourceLocation(3, 12), (try map.find(new_a)).?);
    try std.testing.expectEqual(partitionLocation(4, 13), (try map.find(new_b)).?);
    _ = try map.audit();
}

test "B2 batch version exhaustion leaves all removals and insertions untouched" {
    var nodes: [2]Candidate128.Node = undefined;
    var buckets: [7]Candidate128.Bucket = undefined;
    var map = Candidate128.init(&nodes, &buckets);
    const old_uid = uid128(0x1101);
    const new_uid = uid128(0xa102);
    try map.insert(0, old_uid, resourceLocation(1, 1));

    var operations: [2]Candidate128.Transactions.Operation = undefined;
    var segments: [4]Candidate128.Transactions.Segment = undefined;
    var indices: [7]u32 = undefined;
    var versions: [7]u64 = undefined;
    var removals: [1]Candidate128.RemovePlan = undefined;
    var inserts: [1]Candidate128.InsertPlan = undefined;
    var simulation_nodes: [7]u32 = undefined;
    var simulation_uids: [7]Uid128 = undefined;
    var simulation_node_ids: [8]u32 = undefined;
    var simulation_backlinks: [8]u32 = undefined;
    var workspace: Candidate128.Transactions.Workspace = .{
        .operations = &operations,
        .segments = &segments,
        .indices = &indices,
        .versions = &versions,
        .removals = &removals,
        .inserts = &inserts,
        .simulation_nodes = &simulation_nodes,
        .simulation_uids = &simulation_uids,
        .simulation_node_ids = &simulation_node_ids,
        .simulation_backlinks = &simulation_backlinks,
    };
    const bad_bucket = try map.homeFor(new_uid);
    buckets[bad_bucket].version.store(std.math.maxInt(u64) - 1, .seq_cst);
    const puts = [_]Candidate128.Transactions.Put{
        .{ .node_id = 0, .uid = new_uid, .location = partitionLocation(2, 2) },
    };
    try expectB2PreparedError(
        Candidate128.Transactions.prepare(&map, &.{0}, &puts, &workspace),
        error.VersionExhausted,
    );

    try std.testing.expectEqual(resourceLocation(1, 1), (try map.find(old_uid)).?);
    try std.testing.expectEqual(@as(?Location, null), try map.find(new_uid));
    try std.testing.expect(nodes[0].linked.load(.seq_cst));
    try std.testing.expect((try map.entryAt(0)).?.uid.eql(old_uid));
}

test "B2 transaction scratch scales with a small closure on large backing arrays" {
    var nodes: [1024]Candidate128.Node = undefined;
    var buckets: [2053]Candidate128.Bucket = undefined;
    var map = Candidate128.init(&nodes, &buckets);
    const uid = uid128(0x6a31);
    const puts = [_]Candidate128.Transactions.Put{
        .{ .node_id = 0, .uid = uid, .location = resourceLocation(2, 7) },
    };
    const no_removals: [0]u32 = .{};

    var short_operations: [1]Candidate128.Transactions.Operation = undefined;
    var short_segments: [2]Candidate128.Transactions.Segment = undefined;
    var no_indices: [0]u32 = .{};
    var exact_indices: [1]u32 = undefined;
    var short_versions: [1]u64 = undefined;
    var short_removals: [0]Candidate128.RemovePlan = .{};
    var short_inserts: [1]Candidate128.InsertPlan = undefined;
    var short_simulation_nodes: [1]u32 = undefined;
    var short_simulation_uids: [1]Uid128 = undefined;
    var short_simulation_node_ids: [2]u32 = undefined;
    var short_simulation_backlinks: [2]u32 = undefined;
    var short_workspace: Candidate128.Transactions.Workspace = .{
        .operations = &short_operations,
        .segments = &short_segments,
        .indices = &no_indices,
        .versions = &short_versions,
        .removals = &short_removals,
        .inserts = &short_inserts,
        .simulation_nodes = &short_simulation_nodes,
        .simulation_uids = &short_simulation_uids,
        .simulation_node_ids = &short_simulation_node_ids,
        .simulation_backlinks = &short_simulation_backlinks,
    };

    // An empty home needs exactly one closure bucket. No index slots fail
    // before publication; one exact slot is sufficient.
    try expectB2PreparedError(
        Candidate128.Transactions.prepare(&map, &no_removals, &puts, &short_workspace),
        error.WorkspaceTooSmall,
    );
    short_workspace.indices = &exact_indices;
    var exact_prepared = try Candidate128.Transactions.prepare(
        &map,
        &no_removals,
        &puts,
        &short_workspace,
    );
    defer exact_prepared.abort();
    try std.testing.expectEqual(@as(usize, 1), exact_prepared.indices.len);
    try std.testing.expectEqual(try map.homeFor(uid), exact_prepared.indices[0]);
    exact_prepared.abort();
    try std.testing.expect(!nodes[0].linked.load(.seq_cst));
    for (&buckets) |*bucket| {
        try std.testing.expectEqual(Candidate128.no_bucket, bucket.node_id.load(.seq_cst));
        try std.testing.expectEqual(@as(u64, 0), bucket.version.load(.seq_cst));
    }

    var operations: [1]Candidate128.Transactions.Operation = undefined;
    var segments: [2]Candidate128.Transactions.Segment = undefined;
    var indices: [4]u32 = undefined;
    var versions: [4]u64 = undefined;
    var removals: [0]Candidate128.RemovePlan = .{};
    var inserts: [1]Candidate128.InsertPlan = undefined;
    var simulation_nodes: [4]u32 = undefined;
    var simulation_uids: [4]Uid128 = undefined;
    var undersized_node_ids: [1]u32 = undefined;
    var undersized_backlinks: [1]u32 = undefined;
    var undersized_workspace: Candidate128.Transactions.Workspace = .{
        .operations = &operations,
        .segments = &segments,
        .indices = &indices,
        .versions = &versions,
        .removals = &removals,
        .inserts = &inserts,
        .simulation_nodes = &simulation_nodes,
        .simulation_uids = &simulation_uids,
        .simulation_node_ids = &undersized_node_ids,
        .simulation_backlinks = &undersized_backlinks,
    };

    // One closure bucket plus the new node requires two simulation node slots.
    try expectB2PreparedError(
        Candidate128.Transactions.prepare(&map, &no_removals, &puts, &undersized_workspace),
        error.WorkspaceTooSmall,
    );
    try std.testing.expect(!nodes[0].linked.load(.seq_cst));
    for (&buckets) |*bucket| {
        try std.testing.expectEqual(Candidate128.no_bucket, bucket.node_id.load(.seq_cst));
        try std.testing.expectEqual(@as(u64, 0), bucket.version.load(.seq_cst));
    }

    var simulation_node_ids: [4]u32 = undefined;
    var simulation_backlinks: [4]u32 = undefined;
    var workspace: Candidate128.Transactions.Workspace = .{
        .operations = &operations,
        .segments = &segments,
        .indices = &indices,
        .versions = &versions,
        .removals = &removals,
        .inserts = &inserts,
        .simulation_nodes = &simulation_nodes,
        .simulation_uids = &simulation_uids,
        .simulation_node_ids = &simulation_node_ids,
        .simulation_backlinks = &simulation_backlinks,
    };
    var prepared = try Candidate128.Transactions.prepare(&map, &no_removals, &puts, &workspace);
    defer prepared.abort();
    prepared.commit();

    try std.testing.expectEqual(resourceLocation(2, 7), (try map.find(uid)).?);
    const stats = try map.audit();
    try std.testing.expectEqual(@as(usize, buckets.len), stats.bucket_body);
    try std.testing.expectEqual(@as(usize, nodes.len), stats.node_body);
}

fn uid128Number(uid: Uid128) usize {
    return (@as(usize, uid.bytes[0]) << 8) | uid.bytes[1];
}

fn expectB2PreparedError(result: anytype, expected: anyerror) !void {
    if (result) |unexpected| {
        var prepared = unexpected;
        prepared.abort();
        return error.TestExpectedError;
    } else |actual| {
        try std.testing.expectEqual(expected, actual);
    }
}

fn runB2TwoInsertGapExpansion(start: u32, downstream: u32, expected: []const u32) !void {
    var nodes: [7]Candidate128.Node = undefined;
    var buckets: [11]Candidate128.Bucket = undefined;
    var map = Candidate128.init(&nodes, &buckets);

    var used: [7]Uid128 = undefined;
    var used_count: usize = 0;
    var start_candidate: usize = 0;
    for (0..4) |_| {
        const key = try uid128ForHome(&map, start, start_candidate, used[0..used_count]);
        used[used_count] = key;
        used_count += 1;
        start_candidate = uid128Number(key) + 1;
    }
    var downstream_candidate: usize = 0;
    for (0..3) |_| {
        const key = try uid128ForHome(&map, downstream, downstream_candidate, used[0..used_count]);
        used[used_count] = key;
        used_count += 1;
        downstream_candidate = uid128Number(key) + 1;
    }

    // The first two start-home keys occupy start/start+1. The downstream keys
    // occupy start+3 through start+5, leaving one empty bucket between runs.
    try map.insert(0, used[0], resourceLocation(0, 0));
    try map.insert(1, used[1], resourceLocation(0, 1));
    try map.insert(2, used[4], resourceLocation(1, 0));
    try map.insert(3, used[5], resourceLocation(1, 1));
    try map.insert(4, used[6], resourceLocation(1, 2));

    const puts = [_]Candidate128.Transactions.Put{
        .{ .node_id = 5, .uid = used[2], .location = partitionLocation(2, 0) },
        .{ .node_id = 6, .uid = used[3], .location = partitionLocation(2, 1) },
    };
    const no_removals: [0]u32 = .{};
    var operations: [2]Candidate128.Transactions.Operation = undefined;
    var segments: [4]Candidate128.Transactions.Segment = undefined;
    var indices: [7]u32 = undefined;
    var short_indices: [6]u32 = undefined;
    var versions: [7]u64 = undefined;
    var removals: [0]Candidate128.RemovePlan = .{};
    var inserts: [2]Candidate128.InsertPlan = undefined;
    var simulation_nodes: [7]u32 = undefined;
    var simulation_uids: [7]Uid128 = undefined;
    var simulation_node_ids: [9]u32 = undefined;
    var simulation_backlinks: [9]u32 = undefined;
    var workspace: Candidate128.Transactions.Workspace = .{
        .operations = &operations,
        .segments = &segments,
        .indices = &indices,
        .versions = &versions,
        .removals = &removals,
        .inserts = &inserts,
        .simulation_nodes = &simulation_nodes,
        .simulation_uids = &simulation_uids,
        .simulation_node_ids = &simulation_node_ids,
        .simulation_backlinks = &simulation_backlinks,
    };

    var short_workspace = workspace;
    short_workspace.indices = &short_indices;
    var versions_before: [11]u64 = undefined;
    for (&buckets, 0..) |*bucket, i| versions_before[i] = bucket.version.load(.seq_cst);

    // Initial coverage includes the small gap, but the second put reaches the
    // downstream cluster. Six slots cannot hold the expanded seven-bucket
    // closure, so the transaction must release locks without publishing.
    try expectB2PreparedError(
        Candidate128.Transactions.prepare(&map, &no_removals, &puts, &short_workspace),
        error.WorkspaceTooSmall,
    );
    for (&buckets, 0..) |*bucket, i| {
        try std.testing.expectEqual(versions_before[i], bucket.version.load(.seq_cst));
    }
    try std.testing.expectEqual(@as(?Location, null), try map.find(used[2]));
    try std.testing.expectEqual(@as(?Location, null), try map.find(used[3]));
    try std.testing.expectEqual(resourceLocation(0, 0), (try map.find(used[0])).?);

    var stats: Candidate128.Transactions.PrepareStats = .{};
    var prepared = try Candidate128.Transactions.prepareWithStats(
        &map,
        &no_removals,
        &puts,
        &workspace,
        &stats,
    );
    defer prepared.abort();
    try std.testing.expectEqualSlices(u32, expected, prepared.indices);
    try std.testing.expectEqual(@as(usize, expected.len), stats.closure_buckets_enumerated);
    try std.testing.expectEqual(@as(usize, 1), stats.closure_expansion_rounds);
    try std.testing.expect(stats.closure_expansion_bucket_visits >= 1);
    try std.testing.expectEqual(expected.len, stats.closure_rebuild_bucket_visits);
    prepared.commit();

    try std.testing.expectEqual(partitionLocation(2, 0), (try map.find(used[2])).?);
    try std.testing.expectEqual(partitionLocation(2, 1), (try map.find(used[3])).?);
    const audit = try map.audit();
    try std.testing.expectEqual(@as(usize, 11), audit.bucket_body);
    try std.testing.expectEqual(@as(usize, nodes.len), audit.node_body);
}

test "B2 expands two inserts across a small gap into a downstream cluster" {
    const expected = [_]u32{ 1, 2, 3, 4, 5, 6, 7 };
    try runB2TwoInsertGapExpansion(1, 4, &expected);
}

test "B2 expands two inserts across a wrapped gap into a downstream cluster" {
    const expected = [_]u32{ 0, 1, 2, 3, 4, 9, 10 };
    try runB2TwoInsertGapExpansion(9, 1, &expected);
}
