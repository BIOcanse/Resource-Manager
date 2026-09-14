const std = @import("std");
const protocol = @import("protocol.zig");
const bank_module = @import("bank.zig");
const ResultCode = @import("../../common/result_codes.zig").ResultCode;

const Bank = bank_module.Bank;
const EntrySlot = bank_module.EntrySlot;
const RootSlot = bank_module.RootSlot;
const Fingerprint = bank_module.Fingerprint;

const Context = struct {
    signals: []const protocol.KnownSignalInput,
    key_bytes: []const u8,
    path: []const u8,
    query_launcher: bool = false,

    fn signalKey(self: *const Context, signal: *const protocol.KnownSignalInput) []const u8 {
        return protocol.keySlice(self.key_bytes, signal.key_offset, signal.key_length).?;
    }
};

const Decision = struct {
    status: protocol.MatchStatus = .no_match,
    mode: protocol.KnownMatchMode = .none,
    entry_index: ?u32 = null,
    score: i32 = 0,
    conflict_count: u32 = 0,
    root_index: ?u32 = null,
    derived_root_byte_length: u32 = 0,
    derived_fingerprint: Fingerprint = .{},
    evidence_mask: u64 = 0,
    root_hit: bool = false,
};

const ScoredEntry = struct {
    score: i32,
    evidence_mask: u64,
    root_hit: bool,
};

pub const MatchResult = struct {
    code: ResultCode,
    decision: Decision = .{},
    query_launcher: bool = false,

    pub fn toOutput(
        self: *const MatchResult,
        config: *const protocol.Config,
        bank: *const Bank,
        catalog_generation: u64,
        query_epoch: u64,
    ) protocol.KnownMatchOutput {
        const entry = if (self.decision.entry_index) |index| bank.entries[index] else EntrySlot{};
        const root = if (self.decision.root_index) |index| bank.roots[index] else RootSlot{};
        var flags: u64 = 0;
        if (self.query_launcher) flags |= protocol.KnownOutputFlags.query_launcher;
        if (entry.is_launcher) flags |= protocol.KnownOutputFlags.entry_launcher;
        if (self.decision.root_hit) flags |= protocol.KnownOutputFlags.root_hit;
        return .{
            .abi_version = protocol.abi_version,
            .struct_size = @sizeOf(protocol.KnownMatchOutput),
            .configuration_generation = config.generation,
            .catalog_generation = catalog_generation,
            .catalog_fingerprint_low = bank.fingerprint.low,
            .catalog_fingerprint_high = bank.fingerprint.high,
            .query_epoch = query_epoch,
            .entry_handle = entry.entry_handle,
            .attribution_id_handle = entry.attribution_id_handle,
            .display_name_handle = entry.display_name_handle,
            .source = entry.source,
            .software_kind = entry.software_kind,
            .status = @intFromEnum(self.decision.status),
            .mode = @intFromEnum(self.decision.mode),
            .score = self.decision.score,
            .conflict_count = self.decision.conflict_count,
            .matched_root_handle = root.root_handle,
            .derived_root_byte_length = self.decision.derived_root_byte_length,
            .reserved_u32 = 0,
            .derived_identity_fingerprint_low = self.decision.derived_fingerprint.low,
            .derived_identity_fingerprint_high = self.decision.derived_fingerprint.high,
            .evidence_mask = self.decision.evidence_mask,
            .flags = flags,
            .reserved = .{ 0, 0, 0 },
        };
    }
};

pub fn match(
    config: *const protocol.Config,
    bank: *const Bank,
    input: *const protocol.KnownQueryInput,
    signals: []const protocol.KnownSignalInput,
    key_bytes: []const u8,
) MatchResult {
    var context = Context{
        .signals = signals,
        .key_bytes = key_bytes,
        .path = &.{},
    };
    const validation = validateQuery(input, &context);
    if (validation != .ok) return .{ .code = validation };
    context.query_launcher = queryIsLauncher(bank, &context);

    var decision = decideIdentity(config, bank, &context);
    if (decision.status == .no_match) decision = decideRoot(config, bank, &context);
    if (decision.status == .no_match) decision = decideManagedChild(bank, &context);
    return .{
        .code = .ok,
        .decision = decision,
        .query_launcher = context.query_launcher,
    };
}

fn decideIdentity(config: *const protocol.Config, bank: *const Bank, context: *const Context) Decision {
    var best_score: i32 = std.math.minInt(i32);
    var best_entry: ?u32 = null;
    var best_evidence: u64 = 0;
    var best_root_hit = false;
    var conflict_count: u32 = 0;
    for (bank.entries[0..bank.entry_count], 0..) |entry, entry_index| {
        const scored = scoreIdentity(config, bank, @intCast(entry_index), &entry, context) orelse continue;
        if (scored.score > best_score) {
            best_score = scored.score;
            best_entry = @intCast(entry_index);
            best_evidence = scored.evidence_mask;
            best_root_hit = scored.root_hit;
            conflict_count = 1;
        } else if (scored.score == best_score) {
            conflict_count += 1;
            best_evidence |= scored.evidence_mask;
        }
    }
    if (best_entry == null) return .{};
    if (conflict_count > 1) {
        return .{
            .status = .conflict,
            .mode = .identity,
            .score = best_score,
            .conflict_count = conflict_count,
            .evidence_mask = best_evidence,
        };
    }
    return .{
        .status = .matched,
        .mode = .identity,
        .entry_index = best_entry,
        .score = best_score,
        .evidence_mask = best_evidence,
        .root_hit = best_root_hit,
    };
}

fn scoreIdentity(
    config: *const protocol.Config,
    bank: *const Bank,
    entry_index: u32,
    entry: *const EntrySlot,
    context: *const Context,
) ?ScoredEntry {
    if (entry.is_launcher and !context.query_launcher and
        entryManagedChild(bank, entry_index, context.path) != null)
    {
        return null;
    }
    const primary_name = protocol.keySlice(bank.key_bytes, entry.primary_name_offset, entry.primary_name_length).?;
    const root_hit = entryRootHit(bank, entry_index, context.path);
    var best_strong: i32 = 0;
    var best_auxiliary: i32 = 0;
    var evidence_mask: u64 = 0;
    for (context.signals) |*signal| {
        const kind = protocol.signalKind(signal.kind).?;
        const base = textMatchScore(config, primary_name, context.signalKey(signal));
        if (base == 0) continue;
        const bit = @as(u32, 1) << @intCast(@intFromEnum(kind) - 1);
        const score = base + config.signal_weights[@intCast(@intFromEnum(kind) - 1)];
        evidence_mask |= protocol.signalEvidenceFor(kind);
        if ((config.strong_signal_mask & bit) != 0) {
            best_strong = @max(best_strong, score);
        } else {
            best_auxiliary = @max(best_auxiliary, score);
        }
    }
    if (!root_hit and best_strong < config.strong_evidence_minimum_score) return null;
    var total: i64 = @max(best_strong, best_auxiliary);
    if (root_hit) total += config.root_hit_bonus;
    if (context.query_launcher) {
        if (entry.is_launcher) {
            total += config.query_launcher_match_bonus;
        } else {
            if (!root_hit and (config.flags & protocol.ConfigFlags.launcher_non_entry_requires_root) != 0) {
                return null;
            }
            total -= config.query_launcher_nonmatch_penalty;
        }
    }
    total = @max(total, 0);
    if (total < config.identity_minimum_score) return null;
    return .{ .score = @intCast(total), .evidence_mask = evidence_mask, .root_hit = root_hit };
}

fn decideRoot(config: *const protocol.Config, bank: *const Bank, context: *const Context) Decision {
    if (context.path.len == 0) return .{};
    var longest_root_length: u32 = 0;
    for (bank.roots[0..bank.root_count]) |root| {
        const root_key = rootKey(bank, &root);
        if (!isSameOrUnder(context.path, root_key)) continue;
        const entry = &bank.entries[root.entry_index];
        if (entry.is_launcher and !context.query_launcher and managedChildForRoot(bank, &root, context.path) != null) {
            continue;
        }
        longest_root_length = @max(longest_root_length, root.key_length);
    }
    if (longest_root_length == 0) return .{};

    var best_score: i32 = std.math.minInt(i32);
    var best_entry: ?u32 = null;
    var best_root: ?u32 = null;
    var best_evidence: u64 = 0;
    var conflict_count: u32 = 0;
    for (bank.roots[0..bank.root_count], 0..) |root, root_index| {
        if (root.key_length != longest_root_length or !isSameOrUnder(context.path, rootKey(bank, &root))) continue;
        const entry = &bank.entries[root.entry_index];
        if (entry.is_launcher and !context.query_launcher and managedChildForRoot(bank, &root, context.path) != null) {
            continue;
        }
        const scored = scoreRoot(config, bank, entry, context);
        if (scored.score <= config.root_reject_score) continue;
        if (scored.score > best_score) {
            best_score = scored.score;
            best_entry = root.entry_index;
            best_root = @intCast(root_index);
            best_evidence = scored.evidence_mask;
            conflict_count = 1;
        } else if (scored.score == best_score) {
            best_evidence |= scored.evidence_mask;
            if (best_entry.? != root.entry_index) {
                conflict_count += 1;
            } else if (root.root_handle < bank.roots[best_root.?].root_handle) {
                best_root = @intCast(root_index);
            }
        }
    }
    if (best_entry == null) return .{};
    if (conflict_count > 1) {
        return .{
            .status = .conflict,
            .mode = .root,
            .score = best_score,
            .conflict_count = conflict_count,
            .evidence_mask = best_evidence,
        };
    }
    return .{
        .status = .matched,
        .mode = .root,
        .entry_index = best_entry,
        .score = best_score,
        .root_index = best_root,
        .evidence_mask = best_evidence,
        .root_hit = true,
    };
}

fn scoreRoot(
    config: *const protocol.Config,
    bank: *const Bank,
    entry: *const EntrySlot,
    context: *const Context,
) ScoredEntry {
    const primary_name = protocol.keySlice(bank.key_bytes, entry.primary_name_offset, entry.primary_name_length).?;
    var total: i64 = 0;
    var evidence_mask: u64 = 0;
    for (context.signals) |*signal| {
        const kind = protocol.signalKind(signal.kind).?;
        const bit = @as(u32, 1) << @intCast(@intFromEnum(kind) - 1);
        if ((config.root_signal_mask & bit) == 0) continue;
        const base = textMatchScore(config, primary_name, context.signalKey(signal));
        if (base == 0) continue;
        total += base + config.root_signal_weights[@intCast(@intFromEnum(kind) - 1)];
        evidence_mask |= protocol.signalEvidenceFor(kind);
    }
    if (context.query_launcher) {
        if (entry.is_launcher) {
            total += config.root_launcher_match_bonus;
        } else {
            total -= config.root_launcher_nonmatch_penalty;
        }
    }
    return .{ .score = @intCast(total), .evidence_mask = evidence_mask, .root_hit = true };
}

fn decideManagedChild(bank: *const Bank, context: *const Context) Decision {
    if (context.path.len == 0 or context.query_launcher) return .{};
    var longest_launcher_root: u32 = 0;
    var best_entry: ?u32 = null;
    var best_root: ?u32 = null;
    var best_derived_length: u32 = 0;
    var conflict_count: u32 = 0;
    for (bank.roots[0..bank.root_count], 0..) |root, root_index| {
        const entry = &bank.entries[root.entry_index];
        if (!entry.is_launcher) continue;
        const derived_length = managedChildForRoot(bank, &root, context.path) orelse continue;
        if (root.key_length > longest_launcher_root) {
            longest_launcher_root = root.key_length;
            best_entry = root.entry_index;
            best_root = @intCast(root_index);
            best_derived_length = derived_length;
            conflict_count = 1;
        } else if (root.key_length == longest_launcher_root) {
            if (best_entry.? != root.entry_index) {
                conflict_count += 1;
            } else if (root.root_handle < bank.roots[best_root.?].root_handle) {
                best_root = @intCast(root_index);
                best_derived_length = derived_length;
            }
        }
    }
    if (best_entry == null) return .{};
    if (conflict_count > 1) {
        return .{
            .status = .conflict,
            .mode = .launcher_managed_child,
            .conflict_count = conflict_count,
        };
    }
    const entry = &bank.entries[best_entry.?];
    return .{
        .status = .matched,
        .mode = .launcher_managed_child,
        .entry_index = best_entry,
        .root_index = best_root,
        .derived_root_byte_length = best_derived_length,
        .derived_fingerprint = derivedIdentityFingerprint(
            entry.attribution_id_handle,
            context.path[0..best_derived_length],
        ),
        .root_hit = true,
    };
}

fn validateQuery(input: *const protocol.KnownQueryInput, context: *Context) ResultCode {
    var expected_key_offset: u32 = 0;
    if ((input.valid_mask & protocol.KnownQueryValid.executable_path) != 0) {
        if (input.executable_path_offset != 0 or
            !protocol.validCanonicalPath(context.key_bytes, input.executable_path_offset, input.executable_path_length))
        {
            return .invalid_argument;
        }
        context.path = protocol.keySlice(context.key_bytes, input.executable_path_offset, input.executable_path_length).?;
        expected_key_offset = input.executable_path_length;
    }
    var previous_kind: u32 = 0;
    for (context.signals) |*signal| {
        if (!protocol.validKnownSignal(signal, context.key_bytes) or signal.kind <= previous_kind or
            signal.key_offset != expected_key_offset)
        {
            return .invalid_argument;
        }
        previous_kind = signal.kind;
        expected_key_offset = std.math.add(u32, expected_key_offset, signal.key_length) catch
            return .invalid_argument;
    }
    if (expected_key_offset != context.key_bytes.len) return .invalid_argument;
    return .ok;
}

fn queryIsLauncher(bank: *const Bank, context: *const Context) bool {
    for (context.signals) |*signal| {
        if (bank.matchesRule(.launcher_token, context.signalKey(signal))) return true;
    }
    return false;
}

fn entryRootHit(bank: *const Bank, entry_index: u32, path: []const u8) bool {
    if (path.len == 0) return false;
    for (bank.roots[0..bank.root_count]) |root| {
        if (root.entry_index == entry_index and isSameOrUnder(path, rootKey(bank, &root))) return true;
    }
    return false;
}

fn entryManagedChild(bank: *const Bank, entry_index: u32, path: []const u8) ?u32 {
    if (path.len == 0) return null;
    for (bank.roots[0..bank.root_count]) |root| {
        if (root.entry_index != entry_index) continue;
        if (managedChildForRoot(bank, &root, path)) |length| return length;
    }
    return null;
}

fn managedChildForRoot(bank: *const Bank, root: *const RootSlot, path: []const u8) ?u32 {
    const root_key = rootKey(bank, root);
    if (!isSameOrUnder(path, root_key) or path.len <= root_key.len) return null;
    const relative_start = if (root_key[root_key.len - 1] == '/') root_key.len else root_key.len + 1;
    if (relative_start >= path.len) return null;
    const relative = path[relative_start..];
    const first_separator = std.mem.indexOfScalar(u8, relative, '/') orelse return null;
    if (first_separator == 0 or first_separator + 1 >= relative.len) return null;
    const child_segment = relative[0..first_separator];
    if (!bank.matchesExactRule(.managed_child_segment, child_segment)) return null;
    const after_child = relative[first_separator + 1 ..];
    const second_separator = std.mem.indexOfScalar(u8, after_child, '/') orelse after_child.len;
    if (second_separator == 0) return null;
    return @intCast(relative_start + first_separator + 1 + second_separator);
}

fn rootKey(bank: *const Bank, root: *const RootSlot) []const u8 {
    return protocol.keySlice(bank.key_bytes, root.key_offset, root.key_length).?;
}

fn isSameOrUnder(candidate: []const u8, root: []const u8) bool {
    if (std.mem.eql(u8, candidate, root)) return true;
    if (!std.mem.startsWith(u8, candidate, root) or candidate.len <= root.len) return false;
    return root[root.len - 1] == '/' or candidate[root.len] == '/';
}

fn textMatchScore(config: *const protocol.Config, software_key: []const u8, process_key: []const u8) i32 {
    if (std.mem.eql(u8, software_key, process_key)) return config.exact_text_score;
    if (software_key.len < config.minimum_contains_key_length or
        process_key.len < config.minimum_contains_key_length)
    {
        return 0;
    }
    return if (std.mem.indexOf(u8, software_key, process_key) != null or
        std.mem.indexOf(u8, process_key, software_key) != null)
        config.contains_text_score
    else
        0;
}

fn derivedIdentityFingerprint(attribution_id_handle: u64, root: []const u8) Fingerprint {
    var low: u64 = 0xcbf2_9ce4_8422_2325;
    var high: u64 = 0x6c62_272e_07bb_0142;
    mix(&low, &high, attribution_id_handle);
    for (root) |value| mix(&low, &high, value);
    if (low == 0 and high == 0) high = 1;
    return .{ .low = low, .high = high };
}

fn mix(low: *u64, high: *u64, value: anytype) void {
    const converted: u64 = @intCast(value);
    low.* = (low.* ^ converted) *% 0x0000_0100_0000_01b3;
    high.* = (high.* ^ std.math.rotl(u64, converted +% 0x9e37_79b9_7f4a_7c15, 23)) *%
        0xc2b2_ae3d_27d4_eb4f;
}
