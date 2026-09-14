const std = @import("std");
const protocol = @import("protocol.zig");
const bank_module = @import("bank.zig");
const exact_matcher = @import("exact_matcher.zig");
const known_matcher = @import("known_matcher.zig");
const ResultCode = @import("../../common/result_codes.zig").ResultCode;

const Bank = bank_module.Bank;

pub const Session = struct {
    allocator: std.mem.Allocator,
    mutex: std.atomic.Mutex = .unlocked,
    config: protocol.Config,
    active: Bank,
    staging: Bank,
    candidate_marks: []u64,
    resident_byte_count: u64,
    catalog_generation: u64 = 0,
    last_operation_epoch: u64 = 0,
    last_query_epoch: u64 = 0,
    state_revision: u64 = 1,

    pub fn create(config: *const protocol.Config) !*Session {
        if (!protocol.validConfig(config)) return error.InvalidConfiguration;
        const resident_byte_count = residentByteCount(config) catch return error.InvalidConfiguration;
        if (resident_byte_count > config.resident_byte_budget) return error.InvalidConfiguration;
        const allocator = std.heap.page_allocator;
        const self = try allocator.create(Session);
        errdefer allocator.destroy(self);
        var active = try Bank.init(allocator, config);
        errdefer active.deinit(allocator);
        var staging = try Bank.init(allocator, config);
        errdefer staging.deinit(allocator);
        const candidate_marks = try allocator.alloc(u64, config.maximum_entry_count);
        errdefer allocator.free(candidate_marks);
        @memset(candidate_marks, 0);
        self.* = .{
            .allocator = allocator,
            .config = config.*,
            .active = active,
            .staging = staging,
            .candidate_marks = candidate_marks,
            .resident_byte_count = resident_byte_count,
        };
        return self;
    }

    pub fn destroy(self: *Session) void {
        const allocator = self.allocator;
        allocator.free(self.candidate_marks);
        self.staging.deinit(allocator);
        self.active.deinit(allocator);
        allocator.destroy(self);
    }

    pub fn reconfigure(self: *Session, config: *const protocol.Config) ResultCode {
        lockMutex(&self.mutex);
        defer self.mutex.unlock();
        if (!protocol.validConfig(config)) return .abi_mismatch;
        if (config.generation <= self.config.generation) return .stale_frame;
        if (!sameCapacities(config, &self.config) or self.resident_byte_count > config.resident_byte_budget) {
            return .invalid_argument;
        }
        if (self.catalog_generation != 0 and
            (self.active.prohibited_alias_count != config.required_prohibited_alias_count or
                self.active.launcher_token_count != config.required_launcher_token_count or
                self.active.managed_child_rule_count != config.required_managed_child_rule_count))
        {
            return .invalid_argument;
        }
        self.config = config.*;
        self.advanceRevision();
        return .ok;
    }

    pub fn capacity(self: *const Session) protocol.Capacity {
        return .{
            .struct_size = @sizeOf(protocol.Capacity),
            .entry_capacity = self.config.maximum_entry_count,
            .alias_capacity = self.config.maximum_alias_count,
            .root_capacity = self.config.maximum_root_count,
            .catalog_key_byte_capacity = self.config.maximum_catalog_key_byte_count,
            .query_fact_capacity = self.config.maximum_query_fact_count,
            .query_signal_capacity = self.config.maximum_query_signal_count,
            .query_key_byte_capacity = self.config.maximum_query_key_byte_count,
            .entry_index_capacity = @intCast(self.active.entry_index.len),
            .alias_index_capacity = @intCast(self.active.alias_index.len),
            .identity_index_capacity = @intCast(self.active.identity_index.len),
            .root_index_capacity = @intCast(self.active.root_index.len),
            .resident_byte_count = self.resident_byte_count,
            .reserved = .{ 0, 0, 0 },
        };
    }

    pub fn replace(
        self: *Session,
        input: *const protocol.ReplaceInput,
        entries: []const protocol.EntryInput,
        aliases: []const protocol.AliasInput,
        roots: []const protocol.RootInput,
        key_bytes: []const u8,
    ) ResultCode {
        lockMutex(&self.mutex);
        defer self.mutex.unlock();
        if (!protocol.validReplace(input, &self.config)) return .abi_mismatch;
        if (entries.len != input.entry_count or aliases.len != input.alias_count or
            roots.len != input.root_count or key_bytes.len != input.key_byte_count)
        {
            return .invalid_argument;
        }
        if (input.catalog_generation <= self.catalog_generation or input.operation_epoch <= self.last_operation_epoch) {
            return .stale_frame;
        }
        const load_result = self.staging.load(
            entries,
            aliases,
            roots,
            key_bytes,
            input.prohibited_alias_count,
            input.launcher_token_count,
            input.managed_child_rule_count,
        );
        if (load_result != .ok) return load_result;

        const previous = self.active;
        self.active = self.staging;
        self.staging = previous;
        self.catalog_generation = input.catalog_generation;
        self.last_operation_epoch = input.operation_epoch;
        @memset(self.candidate_marks, 0);
        self.advanceRevision();
        return .ok;
    }

    pub fn match(
        self: *Session,
        input: *const protocol.QueryInput,
        facts: []const protocol.FactInput,
        key_bytes: []const u8,
        output: *protocol.MatchOutput,
    ) ResultCode {
        lockMutex(&self.mutex);
        defer self.mutex.unlock();
        if (!protocol.validQuery(input, &self.config, self.catalog_generation)) return .abi_mismatch;
        if (facts.len != input.fact_count or key_bytes.len != input.key_byte_count) return .invalid_argument;
        if (!protocol.emptyMatchOutput(output)) return .abi_mismatch;
        if (input.query_epoch <= self.last_query_epoch) return .stale_frame;

        const result = exact_matcher.match(
            &self.config,
            &self.active,
            self.catalog_generation,
            input,
            facts,
            key_bytes,
            self.candidate_marks,
        );
        if (result.code != .ok) return result.code;
        output.* = result.output.?;
        self.last_query_epoch = input.query_epoch;
        self.advanceRevision();
        return .ok;
    }

    pub fn matchKnown(
        self: *Session,
        input: *const protocol.KnownQueryInput,
        signals: []const protocol.KnownSignalInput,
        key_bytes: []const u8,
        output: *protocol.KnownMatchOutput,
    ) ResultCode {
        lockMutex(&self.mutex);
        defer self.mutex.unlock();
        if (!protocol.validKnownQuery(input, &self.config, self.catalog_generation)) return .abi_mismatch;
        if (signals.len != input.signal_count or key_bytes.len != input.key_byte_count) return .invalid_argument;
        if (!protocol.emptyKnownMatchOutput(output)) return .abi_mismatch;
        if (input.query_epoch <= self.last_query_epoch) return .stale_frame;

        const result = known_matcher.match(&self.config, &self.active, input, signals, key_bytes);
        if (result.code != .ok) return result.code;
        output.* = result.toOutput(&self.config, &self.active, self.catalog_generation, input.query_epoch);
        self.last_query_epoch = input.query_epoch;
        self.advanceRevision();
        return .ok;
    }

    pub fn snapshot(self: *Session, output: *protocol.CatalogSummary) ResultCode {
        lockMutex(&self.mutex);
        defer self.mutex.unlock();
        if (!protocol.emptySummary(output)) return .abi_mismatch;
        output.* = .{
            .abi_version = protocol.abi_version,
            .struct_size = @sizeOf(protocol.CatalogSummary),
            .configuration_generation = self.config.generation,
            .catalog_generation = self.catalog_generation,
            .state_revision = self.state_revision,
            .last_operation_epoch = self.last_operation_epoch,
            .last_query_epoch = self.last_query_epoch,
            .entry_count = self.active.entry_count,
            .alias_count = self.active.alias_count,
            .root_count = self.active.root_count,
            .key_byte_count = self.active.key_byte_count,
            .prohibited_alias_count = self.active.prohibited_alias_count,
            .launcher_token_count = self.active.launcher_token_count,
            .managed_child_rule_count = self.active.managed_child_rule_count,
            .flags = 0,
            .catalog_fingerprint_low = self.active.fingerprint.low,
            .catalog_fingerprint_high = self.active.fingerprint.high,
            .resident_byte_count = self.resident_byte_count,
            .reserved = .{ 0, 0, 0 },
        };
        return .ok;
    }

    fn advanceRevision(self: *Session) void {
        self.state_revision +%= 1;
        if (self.state_revision == 0) self.state_revision = 1;
    }
};

fn residentByteCount(config: *const protocol.Config) !u64 {
    var total: u64 = @sizeOf(Session);
    total = try addAllocation(total, config.maximum_entry_count, @sizeOf(u64));
    var bank_total: u64 = 0;
    bank_total = try addAllocation(bank_total, config.maximum_entry_count, @sizeOf(bank_module.EntrySlot));
    bank_total = try addAllocation(bank_total, config.maximum_alias_count, @sizeOf(bank_module.AliasSlot));
    bank_total = try addAllocation(bank_total, config.maximum_root_count, @sizeOf(bank_module.RootSlot));
    bank_total = try addAllocation(bank_total, config.maximum_catalog_key_byte_count, @sizeOf(u8));
    bank_total = try addAllocation(bank_total, config.entry_index_capacity, @sizeOf(u32));
    bank_total = try addAllocation(bank_total, config.identity_index_capacity, @sizeOf(u32));
    bank_total = try addAllocation(bank_total, config.alias_index_capacity, @sizeOf(u32));
    bank_total = try addAllocation(bank_total, config.root_index_capacity, @sizeOf(u32));
    total = try std.math.add(u64, total, try std.math.mul(u64, bank_total, 2));
    return total;
}

fn addAllocation(total: u64, count: u32, comptime item_size: usize) !u64 {
    return std.math.add(u64, total, try std.math.mul(u64, count, item_size));
}

fn sameCapacities(left: *const protocol.Config, right: *const protocol.Config) bool {
    return left.maximum_entry_count == right.maximum_entry_count and
        left.maximum_alias_count == right.maximum_alias_count and
        left.maximum_root_count == right.maximum_root_count and
        left.maximum_catalog_key_byte_count == right.maximum_catalog_key_byte_count and
        left.maximum_query_fact_count == right.maximum_query_fact_count and
        left.maximum_query_signal_count == right.maximum_query_signal_count and
        left.maximum_query_key_byte_count == right.maximum_query_key_byte_count and
        left.entry_index_capacity == right.entry_index_capacity and
        left.alias_index_capacity == right.alias_index_capacity and
        left.identity_index_capacity == right.identity_index_capacity and
        left.root_index_capacity == right.root_index_capacity;
}

pub fn lockMutex(mutex: *std.atomic.Mutex) void {
    while (!mutex.tryLock()) std.atomic.spinLoopHint();
}
