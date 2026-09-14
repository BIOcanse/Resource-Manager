const std = @import("std");
const protocol = @import("protocol.zig");
const bank_module = @import("bank.zig");
const ResultCode = @import("../../common/result_codes.zig").ResultCode;

const Bank = bank_module.Bank;
const EntrySlot = bank_module.EntrySlot;

pub const MatchResult = struct {
    code: ResultCode,
    output: ?protocol.MatchOutput = null,
};

const Accumulator = struct {
    first_entry_index: ?u32 = null,
    distinct_entry_count: u32 = 0,
    matched_fact_count: u32 = 0,
    evidence_mask: u64 = 0,
    executable_entry_index: ?u32 = null,
    executable_conflict: bool = false,
    product_entry_index: ?u32 = null,
    product_conflict: bool = false,
    saw_product_fact: bool = false,
    matched_product_fact: bool = false,

    fn observe(
        self: *Accumulator,
        kind: protocol.AliasKind,
        entry_index: ?u32,
        query_epoch: u64,
        candidate_marks: []u64,
    ) void {
        if (kind == .product_name) self.saw_product_fact = true;
        const index = entry_index orelse return;
        self.matched_fact_count += 1;
        self.evidence_mask |= protocol.evidenceFor(kind);
        if (candidate_marks[index] != query_epoch) {
            candidate_marks[index] = query_epoch;
            self.distinct_entry_count += 1;
            if (self.first_entry_index == null) self.first_entry_index = index;
        }
        switch (kind) {
            .executable_name => {
                if (self.executable_entry_index) |existing| {
                    if (existing != index) self.executable_conflict = true;
                } else self.executable_entry_index = index;
            },
            .product_name => {
                self.matched_product_fact = true;
                if (self.product_entry_index) |existing| {
                    if (existing != index) self.product_conflict = true;
                } else self.product_entry_index = index;
            },
            else => {},
        }
    }

    fn decide(self: *const Accumulator, bank: *const Bank, mode: protocol.MatchMode) Decision {
        if (mode != .portable_process) {
            if (self.distinct_entry_count == 0) return .noMatch(self);
            if (self.distinct_entry_count != 1) return .conflict(self);
            return .matched(self, self.first_entry_index.?, .confirmed);
        }

        const executable_index = self.executable_entry_index orelse return .noMatch(self);
        if (self.executable_conflict or self.product_conflict or
            (self.product_entry_index != null and self.product_entry_index.? != executable_index))
        {
            return .conflict(self);
        }
        if (self.matched_product_fact) return .matched(self, executable_index, .confirmed);
        if (self.saw_product_fact and bank.entries[executable_index].product_alias_count != 0) {
            return .noMatch(self);
        }
        return .matched(self, executable_index, .candidate);
    }
};

const Decision = struct {
    status: protocol.MatchStatus,
    confidence: protocol.MatchConfidence,
    entry_index: ?u32,
    evidence_mask: u64,
    conflict_count: u32,
    matched_fact_count: u32,

    fn noMatch(accumulator: *const Accumulator) Decision {
        return .{
            .status = .no_match,
            .confidence = .none,
            .entry_index = null,
            .evidence_mask = accumulator.evidence_mask,
            .conflict_count = 0,
            .matched_fact_count = accumulator.matched_fact_count,
        };
    }

    fn conflict(accumulator: *const Accumulator) Decision {
        return .{
            .status = .conflict,
            .confidence = .none,
            .entry_index = null,
            .evidence_mask = accumulator.evidence_mask,
            .conflict_count = accumulator.distinct_entry_count,
            .matched_fact_count = accumulator.matched_fact_count,
        };
    }

    fn matched(
        accumulator: *const Accumulator,
        entry_index: u32,
        confidence: protocol.MatchConfidence,
    ) Decision {
        return .{
            .status = .matched,
            .confidence = confidence,
            .entry_index = entry_index,
            .evidence_mask = accumulator.evidence_mask,
            .conflict_count = 0,
            .matched_fact_count = accumulator.matched_fact_count,
        };
    }

    fn toOutput(
        self: *const Decision,
        config: *const protocol.Config,
        bank: *const Bank,
        catalog_generation: u64,
        query_epoch: u64,
    ) protocol.MatchOutput {
        const entry = if (self.entry_index) |index| bank.entries[index] else EntrySlot{};
        return .{
            .abi_version = protocol.abi_version,
            .struct_size = @sizeOf(protocol.MatchOutput),
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
            .status = @intFromEnum(self.status),
            .confidence = @intFromEnum(self.confidence),
            .evidence_mask = self.evidence_mask,
            .conflict_count = self.conflict_count,
            .matched_fact_count = self.matched_fact_count,
            .flags = 0,
            .reserved = .{ 0, 0 },
        };
    }
};

pub fn match(
    config: *const protocol.Config,
    bank: *const Bank,
    catalog_generation: u64,
    input: *const protocol.QueryInput,
    facts: []const protocol.FactInput,
    key_bytes: []const u8,
    candidate_marks: []u64,
) MatchResult {
    const mode = protocol.matchMode(input.mode) orelse return .{ .code = .invalid_argument };
    const validation = validateFacts(facts, key_bytes, mode);
    if (validation != .ok) return .{ .code = validation };

    var accumulator = Accumulator{};
    for (facts) |fact| {
        const kind = protocol.aliasKind(fact.kind) orelse return .{ .code = .invalid_argument };
        const key = if (kind == .steam_app_id)
            &.{}
        else
            protocol.keySlice(key_bytes, fact.key_offset, fact.key_length) orelse
                return .{ .code = .invalid_argument };
        accumulator.observe(
            kind,
            bank.findAlias(kind, fact.numeric_value, key),
            input.query_epoch,
            candidate_marks,
        );
    }

    const decision = accumulator.decide(bank, mode);
    return .{
        .code = .ok,
        .output = decision.toOutput(config, bank, catalog_generation, input.query_epoch),
    };
}

fn validateFacts(
    facts: []const protocol.FactInput,
    key_bytes: []const u8,
    mode: protocol.MatchMode,
) ResultCode {
    var expected_key_offset: u32 = 0;
    var previous: ?protocol.FactInput = null;
    for (facts) |fact| {
        if (!protocol.validFact(&fact, key_bytes, mode)) return .invalid_argument;
        const kind = protocol.aliasKind(fact.kind) orelse return .invalid_argument;
        if (kind != .steam_app_id) {
            if (fact.key_offset != expected_key_offset) return .invalid_argument;
            expected_key_offset = std.math.add(u32, expected_key_offset, fact.key_length) catch
                return .invalid_argument;
        }
        if (previous) |prior| {
            if (!factLessThan(&prior, &fact, key_bytes)) return .invalid_argument;
        }
        previous = fact;
    }
    if (expected_key_offset != key_bytes.len) return .invalid_argument;
    return .ok;
}

fn factLessThan(
    left: *const protocol.FactInput,
    right: *const protocol.FactInput,
    key_bytes: []const u8,
) bool {
    if (left.kind != right.kind) return left.kind < right.kind;
    const kind = protocol.aliasKind(left.kind) orelse return false;
    if (kind == .steam_app_id) return left.numeric_value < right.numeric_value;
    const left_key = protocol.keySlice(key_bytes, left.key_offset, left.key_length) orelse return false;
    const right_key = protocol.keySlice(key_bytes, right.key_offset, right.key_length) orelse return false;
    return std.mem.order(u8, left_key, right_key) == .lt;
}
