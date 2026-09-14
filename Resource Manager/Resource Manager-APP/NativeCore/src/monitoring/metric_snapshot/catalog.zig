const std = @import("std");
const protocol = @import("protocol.zig");

pub const no_index: u32 = std.math.maxInt(u32);

pub const SourceRecord = struct {
    active: bool = false,
    definition: protocol.SourcePolicyInput = std.mem.zeroes(protocol.SourcePolicyInput),
    source_incarnation: u64 = 0,
    source_generation: u64 = 0,
    last_observation_sequence: u64 = 0,
    capability_generation: u64 = 0,
    last_attempt_at_milliseconds: u64 = 0,
    last_current_at_milliseconds: u64 = 0,
    requested_count: u32 = 0,
    observed_count: u32 = 0,
    skipped_count: u32 = 0,
    overflow_count: u32 = 0,
    gpu_inventory_count: u32 = 0,
    gpu_retained_count: u32 = 0,
    reset_count: u32 = 0,
    last_reset_reason_mask: u32 = 0,
    status: protocol.SourceStatus = .unavailable,
    cpu_baseline_valid: bool = false,
    cpu_idle_ticks: u64 = 0,
    cpu_kernel_ticks: u64 = 0,
    cpu_user_ticks: u64 = 0,
    cpu_monotonic_ticks: u64 = 0,
    cpu_monotonic_ticks_per_second: u64 = 0,
    cpu_counter_contract_version: u32 = 0,
};

pub const RuleRecord = struct {
    active: bool = false,
    definition: protocol.MetricDefinitionInput =
        std.mem.zeroes(protocol.MetricDefinitionInput),
    source_generation: u64 = 0,
    observed_at_milliseconds: u64 = 0,
    value_bits: u64 = 0,
    capability_mask: u64 = 0,
    quality: u32 = 0,
    sample_duration_milliseconds: u64 = 0,
    capability_generation: u64 = 0,
    unsupported_until_capability_generation: u64 = 0,
    status: protocol.MetricStatus = .unavailable,
    has_last_good: bool = false,
};

pub const MetricRecord = struct {
    active: bool = false,
    metric_handle: u64 = 0,
    first_rule_index: u32 = 0,
    rule_count: u32 = 0,
    output: protocol.MetricOutput = std.mem.zeroes(protocol.MetricOutput),
};

pub const Bank = struct {
    sources: []SourceRecord,
    rules: []RuleRecord,
    metrics: []MetricRecord,
    source_index: []u32,
    rule_index: []u32,
    metric_index: []u32,
    source_count: u32 = 0,
    rule_count: u32 = 0,
    metric_count: u32 = 0,
    gpu_inventory_source_count: u32 = 0,

    pub fn clear(self: *Bank) void {
        @memset(self.sources, .{});
        @memset(self.rules, .{});
        @memset(self.metrics, .{});
        @memset(self.source_index, no_index);
        @memset(self.rule_index, no_index);
        @memset(self.metric_index, no_index);
        self.source_count = 0;
        self.rule_count = 0;
        self.metric_count = 0;
        self.gpu_inventory_source_count = 0;
    }

    pub fn findSource(self: *const Bank, handle: u64) ?u32 {
        return findIndex(SourceRecord, self.source_index, self.sources, handle, sourceHandle);
    }

    pub fn findRule(self: *const Bank, handle: u64) ?u32 {
        return findIndex(RuleRecord, self.rule_index, self.rules, handle, ruleHandle);
    }

    pub fn findMetric(self: *const Bank, handle: u64) ?u32 {
        return findIndex(MetricRecord, self.metric_index, self.metrics, handle, metricHandle);
    }

    pub fn build(
        self: *Bank,
        active: *const Bank,
        header: *const protocol.CatalogReplaceInput,
        sources: []const protocol.SourcePolicyInput,
        rules: []const protocol.MetricDefinitionInput,
    ) bool {
        self.clear();
        if (sources.len != header.source_count or rules.len != header.rule_count) return false;
        if (sources.len > self.sources.len or rules.len > self.rules.len) return false;
        if (header.metric_count > self.metrics.len) return false;

        var previous_source_handle: u64 = 0;
        for (sources, 0..) |definition, ordinal| {
            if (!protocol.validSourcePolicy(&definition)) return false;
            if (definition.source_handle <= previous_source_handle) return false;
            previous_source_handle = definition.source_handle;
            const index: u32 = @intCast(ordinal);
            var record = SourceRecord{
                .active = true,
                .definition = definition,
            };
            if (active.findSource(definition.source_handle)) |old_index| {
                const old = active.sources[old_index];
                if (sameSourceDefinition(&old.definition, &definition)) record = old;
                record.definition = definition;
                record.active = true;
            }
            self.sources[ordinal] = record;
            if (!insertIndex(
                SourceRecord,
                self.source_index,
                self.sources,
                definition.source_handle,
                index,
                sourceHandle,
            )) return false;
            if (@as(protocol.SourceRole, @enumFromInt(definition.source_role)) == .gpu_inventory) {
                self.gpu_inventory_source_count += 1;
            }
        }
        if (self.gpu_inventory_source_count != header.gpu_inventory_source_count or
            self.gpu_inventory_source_count > 1)
        {
            return false;
        }
        self.source_count = @intCast(sources.len);

        var previous_metric_handle: u64 = 0;
        var previous_priority: u32 = 0;
        var previous_rule_handle: u64 = 0;
        var metric_ordinal: u32 = 0;
        for (rules, 0..) |definition, ordinal| {
            if (!protocol.validMetricDefinition(&definition)) return false;
            const source_index = self.findSource(definition.source_handle) orelse return false;
            if (self.sources[source_index].definition.source_role !=
                @intFromEnum(protocol.SourceRole.metrics))
            {
                return false;
            }
            if ((definition.capability_mask &
                ~self.sources[source_index].definition.capability_mask) != 0)
            {
                return false;
            }
            if (self.sources[source_index].definition.priority != definition.source_priority) {
                return false;
            }
            if (definition.metric_handle < previous_metric_handle) return false;
            if (definition.metric_handle == previous_metric_handle) {
                if (definition.source_priority < previous_priority or
                    (definition.source_priority == previous_priority and
                        definition.rule_handle <= previous_rule_handle))
                {
                    return false;
                }
                const metric = &self.metrics[metric_ordinal - 1];
                if (!sameMetricIdentity(
                    &self.rules[metric.first_rule_index].definition,
                    &definition,
                )) return false;
                metric.rule_count += 1;
            } else {
                if (metric_ordinal >= header.metric_count) return false;
                previous_priority = 0;
                previous_rule_handle = 0;
                const metric = &self.metrics[metric_ordinal];
                metric.* = .{
                    .active = true,
                    .metric_handle = definition.metric_handle,
                    .first_rule_index = @intCast(ordinal),
                    .rule_count = 1,
                };
                if (!insertIndex(
                    MetricRecord,
                    self.metric_index,
                    self.metrics,
                    definition.metric_handle,
                    metric_ordinal,
                    metricHandle,
                )) return false;
                metric_ordinal += 1;
            }
            previous_metric_handle = definition.metric_handle;
            previous_priority = definition.source_priority;
            previous_rule_handle = definition.rule_handle;

            var record = RuleRecord{
                .active = true,
                .definition = definition,
            };
            if (active.findRule(definition.rule_handle)) |old_index| {
                const old = active.rules[old_index];
                if (sameRuleDefinition(&old.definition, &definition)) record = old;
                record.definition = definition;
                record.active = true;
            }
            self.rules[ordinal] = record;
            if (!insertIndex(
                RuleRecord,
                self.rule_index,
                self.rules,
                definition.rule_handle,
                @intCast(ordinal),
                ruleHandle,
            )) return false;
        }
        if (metric_ordinal != header.metric_count) return false;
        self.rule_count = @intCast(rules.len);
        self.metric_count = metric_ordinal;
        return true;
    }
};

pub fn sameSourceDefinition(
    left: *const protocol.SourcePolicyInput,
    right: *const protocol.SourcePolicyInput,
) bool {
    return std.mem.eql(u8, std.mem.asBytes(left), std.mem.asBytes(right));
}

pub fn sameRuleDefinition(
    left: *const protocol.MetricDefinitionInput,
    right: *const protocol.MetricDefinitionInput,
) bool {
    return std.mem.eql(u8, std.mem.asBytes(left), std.mem.asBytes(right));
}

fn sameMetricIdentity(
    left: *const protocol.MetricDefinitionInput,
    right: *const protocol.MetricDefinitionInput,
) bool {
    return left.metric_handle == right.metric_handle and
        left.scope_handle == right.scope_handle and
        left.metric_kind == right.metric_kind and
        left.scope_kind == right.scope_kind and
        left.value_kind == right.value_kind and
        left.flags == right.flags and
        left.minimum_value_bits == right.minimum_value_bits and
        left.maximum_value_bits == right.maximum_value_bits;
}

fn sourceHandle(record: *const SourceRecord) u64 {
    return record.definition.source_handle;
}

fn ruleHandle(record: *const RuleRecord) u64 {
    return record.definition.rule_handle;
}

fn metricHandle(record: *const MetricRecord) u64 {
    return record.metric_handle;
}

fn findIndex(
    comptime T: type,
    index: []const u32,
    records: []const T,
    handle: u64,
    comptime handleAt: fn (*const T) u64,
) ?u32 {
    if (handle == 0 or index.len == 0) return null;
    const mask = index.len - 1;
    var position = hashHandle(handle) & mask;
    var visited: usize = 0;
    while (visited < index.len) : (visited += 1) {
        const record_index = index[position];
        if (record_index == no_index) return null;
        if (record_index < records.len and handleAt(&records[record_index]) == handle) {
            return record_index;
        }
        position = (position + 1) & mask;
    }
    return null;
}

fn insertIndex(
    comptime T: type,
    index: []u32,
    records: []const T,
    handle: u64,
    record_index: u32,
    comptime handleAt: fn (*const T) u64,
) bool {
    if (handle == 0 or record_index >= records.len or index.len == 0) return false;
    const mask = index.len - 1;
    var position = hashHandle(handle) & mask;
    var visited: usize = 0;
    while (visited < index.len) : (visited += 1) {
        const existing = index[position];
        if (existing == no_index) {
            index[position] = record_index;
            return true;
        }
        if (existing < records.len and handleAt(&records[existing]) == handle) return false;
        position = (position + 1) & mask;
    }
    return false;
}

fn hashHandle(handle: u64) usize {
    return @intCast(std.hash.Wyhash.hash(
        0x726d_6d65_7472_6963,
        std.mem.asBytes(&handle),
    ));
}
