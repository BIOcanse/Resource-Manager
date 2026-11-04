//! Independent ABI. All memory is borrowed; enum numbers match metric_snapshot.
pub const version: u32 = 0x0001_0001;
pub const none: u32 = 0xffff_ffff;
pub const Code = enum(u32) { ok, invalid_contract, stale_plan, stale_identity, capacity_exceeded, workspace_unavailable, payload_capacity, publication_rejected, canceled, shared_section, closed };
pub const Family = enum(u32) { none, sources, rules, metrics, active, work, payload, generation, binding };
pub const Failure = extern struct { code: Code = .ok, family: Family = .none, required: u64 = 0, limit: u64 = 0 };
pub const Config = extern struct {
    abi_version: u32 = version,
    source_capacity: u32,
    rule_capacity: u32,
    metric_capacity: u32,
    max_value_bytes: u32,
    maximum_future_skew_ms: u32 = 30_000,
    active_limit: u64,
    work_limit: u64,
    payload_work_bytes: u64 = 0,
};
pub const Sizes = extern struct { active: u64, work: u64, alignment: u32 = 8, reserved: u32 = 0 };
pub const TableBinding = extern struct {
    region: ?[*]u8,
    byte_length: u64,
    rows_source: u32,
    value_source: u32,
    health_source: u32,
    reserved: u32 = 0,
    positions: [16]u32 = @splat(none),
    provenance_source: u32 = none,
    provenance_position: u32 = none,
};
pub const SourceBinding = extern struct { source_id: u64, access_region: u32, reserved: u32 = 0, table: TableBinding };
pub const ResultRoute = extern struct { group: u32 = none, row: u32 = none, metric_id: u64 = 0, identity: u64 = 0, catalog_generation: u64 = 0 };
pub const SourceColumns = extern struct { count: u32, reserved: u32 = 0, ids: ?[*]const u64, incarnations: ?[*]const u64, capabilities: ?[*]const u64, availability: ?[*]const u32 };
pub const RuleColumns = extern struct {
    count: u32,
    reserved: u32 = 0,
    ids: ?[*]const u64,
    source_ids: ?[*]const u64,
    metric_ids: ?[*]const u64,
    identities: ?[*]const u64,
    kinds: ?[*]const u32,
    flags: ?[*]const u32,
    priorities: ?[*]const u32,
    value_kinds: ?[*]const u32,
    minimum_bits: ?[*]const u64,
    maximum_bits: ?[*]const u64,
    capability_masks: ?[*]const u64,
};
pub const MetricColumns = extern struct { count: u32, reserved: u32 = 0, ids: ?[*]const u64, identities: ?[*]const u64 };
pub const Catalog = extern struct { sources: SourceColumns, rules: RuleColumns, metrics: MetricColumns };
pub const Observations = extern struct {
    capacity: u32 = 0,
    reserved: u32 = 0,
    rule_indices: ?[*]u32 = null,
    status: ?[*]u32 = null,
    kinds: ?[*]u32 = null,
    presence: ?[*]u32 = null,
    reasons: ?[*]u32 = null,
    flags: ?[*]u32 = null,
    low: ?[*]u64 = null,
    high: ?[*]u64 = null,
    reference: ?[*]u64 = null,
    monotonic_ns: ?[*]u64 = null,
    tick_frequency: ?[*]u64 = null,
    duration_ms: ?[*]u64 = null,
    observed_ms: ?[*]i64 = null,
    payload_offsets: ?[*]u32 = null,
    payload_lengths: ?[*]u32 = null,
    capability_masks: ?[*]u64 = null,
    quality: ?[*]u32 = null,
};
pub const Queries = extern struct {
    capacity: u32 = 0,
    reserved: u32 = 0,
    rule_ids: ?[*]const u64 = null,
    source_ids: ?[*]const u64 = null,
    metric_ids: ?[*]const u64 = null,
    identities: ?[*]const u64 = null,
    incarnations: ?[*]const u64 = null,
    capabilities: ?[*]const u64 = null,
    capability_masks: ?[*]const u64 = null,
    value_kinds: ?[*]const u32 = null,
    metric_kinds: ?[*]const u32 = null,
};
pub const Round = extern struct {
    token: u64 = 0,
    catalog_generation: u64 = 0,
    observations: Observations = .{},
    count: u32 = 0,
    reserved: u32 = 0,
    payload: ?[*]u8 = null,
    payload_capacity: u64 = 0,
    queries: Queries = .{},
};
pub const Diagnostics = extern struct {
    catalog_generation: u64,
    round_generation: u64,
    active_bytes: u64,
    work_bytes: u64,
    bytes_written: u64,
    bytes_touched: u64,
    bytes_read: u64,
    payload_written: u64,
    attached: u32,
    phase: u32,
};
pub const Prepared = extern struct { token: u64, catalog_generation: u64, metric_indices: ?[*]const u32, count: u32, reserved: u32 = 0 };
pub const PublicStatus = enum(u32) { current = 1, retained_last_good = 2, unavailable = 3, not_requested = 4, unsupported = 5, partial = 6, warming = 7 };
pub const no_subscription_reason: u32 = 0xffff_ff01;
pub const Receipt = extern struct {
    token: u64 = 0,
    catalog_generation: u64 = 0,
    round_generation: u64 = 0,
    utc_ms: i64 = 0,
    metric_indices: ?[*]const u32 = null,
    metric_ids: ?[*]const u64 = null,
    affected_count: u32 = 0,
    query_count: u32 = 0,
    queries: Queries = .{},
    invalidated_aliases: ?[*]const u32 = null,
    invalidated_alias_count: u32 = 0,
    committed: u32 = 0,
};
comptime {
    const std = @import("std");
    std.debug.assert(@sizeOf(Config) == 48 and @offsetOf(Config, "payload_work_bytes") == 40 and @sizeOf(Sizes) == 24 and @sizeOf(Failure) == 24);
    std.debug.assert(@sizeOf(TableBinding) == 104 and @offsetOf(TableBinding, "positions") == 32);
    std.debug.assert(@offsetOf(TableBinding, "provenance_source") == 96 and @offsetOf(TableBinding, "provenance_position") == 100);
    std.debug.assert(@sizeOf(SourceBinding) == 120 and @offsetOf(SourceBinding, "table") == 16 and @sizeOf(ResultRoute) == 32);
    std.debug.assert(@sizeOf(SourceColumns) == 40 and @sizeOf(RuleColumns) == 96 and @sizeOf(Catalog) == 160 and @sizeOf(Observations) == 144);
    std.debug.assert(@sizeOf(Queries) == 80 and @sizeOf(Round) == 264 and @offsetOf(Round, "payload") == 168 and @offsetOf(Round, "queries") == 184);
    std.debug.assert(@sizeOf(Diagnostics) == 72);
    std.debug.assert(@sizeOf(Prepared) == 32 and @offsetOf(Prepared, "metric_indices") == 16);
    std.debug.assert(@sizeOf(Receipt) == 152 and @offsetOf(Receipt, "queries") == 56 and @offsetOf(Receipt, "committed") == 148);
}
