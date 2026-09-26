const std = @import("std");
const protocol = @import("protocol.zig");
const abi = @import("abi.zig");
const wire_contract = @import("wire_contract.zig");
const ResultCode = @import("../../common/result_codes.zig").ResultCode;

test "metric snapshot ABI sizes alignments and offsets are fixed" {
    try std.testing.expectEqual(@as(u32, 0x0003_0000), protocol.abi_version);
    const expected = .{
        .{ protocol.Config, 160 },
        .{ protocol.Capacity, 104 },
        .{ protocol.SourcePolicyInput, 64 },
        .{ protocol.MetricDefinitionInput, 96 },
        .{ protocol.CatalogReplaceInput, 96 },
        .{ protocol.CompletionHeader, 144 },
        .{ protocol.PlanInput, 88 },
        .{ protocol.PlanOutput, 88 },
        .{ protocol.PlanMetricInput, 32 },
        .{ protocol.SourceModeInput, 48 },
        .{ protocol.SourcePlanOutput, 72 },
        .{ protocol.MetricPlanOutput, 72 },
        .{ protocol.RequestedMetricInput, 32 },
        .{ protocol.ObservationInput, 104 },
        .{ protocol.CpuCounterInput, 136 },
        .{ protocol.GpuInventoryInput, 112 },
        .{ protocol.FinalizeInput, 80 },
        .{ protocol.ControlInput, 56 },
        .{ protocol.ReadInput, 72 },
        .{ protocol.SnapshotHeader, 152 },
        .{ protocol.MetricOutput, 112 },
        .{ protocol.SourceOutput, 128 },
        .{ protocol.GpuInventoryOutput, 120 },
        .{ protocol.RuleStateOutput, 120 },
        .{ protocol.SourcePersistenceOutput, 184 },
        .{ protocol.PersistenceInput, 64 },
        .{ protocol.PersistenceHeader, 144 },
    };
    inline for (expected) |item| {
        try std.testing.expectEqual(@as(usize, item[1]), @sizeOf(item[0]));
        try std.testing.expectEqual(@as(usize, 8), @alignOf(item[0]));
    }

    try std.testing.expectEqual(@as(usize, 8), @offsetOf(protocol.Config, "generation"));
    try std.testing.expectEqual(
        @as(usize, 40),
        @offsetOf(protocol.Config, "maximum_persistence_source_count"),
    );
    try std.testing.expectEqual(
        @as(usize, 72),
        @offsetOf(protocol.Config, "maximum_future_skew_milliseconds"),
    );
    try std.testing.expectEqual(
        @as(usize, 80),
        @offsetOf(protocol.Config, "resident_byte_budget"),
    );
    try std.testing.expectEqual(
        @as(usize, 112),
        @offsetOf(protocol.Config, "maximum_plan_metric_count"),
    );
    try std.testing.expectEqual(
        @as(usize, 128),
        @offsetOf(protocol.Config, "gpu_luid_index_capacity"),
    );
    try std.testing.expectEqual(
        @as(usize, 64),
        @offsetOf(protocol.CompletionHeader, "capability_generation"),
    );
    try std.testing.expectEqual(
        @as(usize, 72),
        @offsetOf(protocol.CompletionHeader, "plan_token_fingerprint"),
    );
    try std.testing.expectEqual(
        @as(usize, 96),
        @offsetOf(protocol.CompletionHeader, "requested_rule_count"),
    );
    try std.testing.expectEqual(
        @as(usize, 120),
        @offsetOf(protocol.CompletionHeader, "status"),
    );
    try std.testing.expectEqual(
        @as(usize, 40),
        @offsetOf(protocol.SnapshotHeader, "committed_generation"),
    );
    try std.testing.expectEqual(
        @as(usize, 80),
        @offsetOf(protocol.SnapshotHeader, "source_count"),
    );
    try std.testing.expectEqual(
        @as(usize, 116),
        @offsetOf(protocol.SnapshotHeader, "flags"),
    );
    try std.testing.expectEqual(
        @as(usize, 128),
        @offsetOf(protocol.SnapshotHeader, "semantic_fingerprint"),
    );
    try std.testing.expectEqual(
        @as(usize, 32),
        @offsetOf(protocol.PersistenceHeader, "committed_generation"),
    );
    try std.testing.expectEqual(
        @as(usize, 72),
        @offsetOf(protocol.PersistenceHeader, "catalog_fingerprint"),
    );
    try std.testing.expectEqual(
        @as(usize, 80),
        @offsetOf(protocol.PersistenceHeader, "rule_state_count"),
    );
    try std.testing.expectEqual(
        @as(usize, 104),
        @offsetOf(protocol.PersistenceHeader, "checksum"),
    );
    try std.testing.expectEqual(
        @as(usize, 104),
        @offsetOf(protocol.RuleStateOutput, "capability_generation"),
    );
    try std.testing.expectEqual(
        @as(usize, 112),
        @offsetOf(protocol.RuleStateOutput, "unsupported_until_capability_generation"),
    );
    try std.testing.expectEqual(
        @as(usize, 168),
        @offsetOf(protocol.SourcePersistenceOutput, "gpu_inventory_count"),
    );
    try std.testing.expectEqual(
        @as(usize, 172),
        @offsetOf(protocol.SourcePersistenceOutput, "gpu_retained_count"),
    );
    try std.testing.expectEqual(
        @as(u64, 0x51b78849c815027d),
        moduleLayoutFingerprint(),
    );
    try std.testing.expectEqual(
        protocol.wire_contract_fingerprint,
        wire_contract.fingerprint(),
    );
    try std.testing.expectEqual(
        protocol.wire_contract_fingerprint,
        abi.rm_metric_snapshot_wire_contract_fingerprint(),
    );
}

test "metric snapshot module-local C ABI owns one opaque session" {
    var config = minimalConfig();
    var handle: ?*anyopaque = null;
    try std.testing.expectEqual(
        @intFromEnum(ResultCode.ok),
        abi.rm_metric_snapshot_create(&config, &handle),
    );
    defer abi.rm_metric_snapshot_destroy(handle);
    try std.testing.expect(handle != null);
    try std.testing.expectEqual(protocol.abi_version, abi.rm_metric_snapshot_abi_version());

    var capacity = std.mem.zeroes(protocol.Capacity);
    try std.testing.expectEqual(
        @intFromEnum(ResultCode.abi_mismatch),
        abi.rm_metric_snapshot_query_capacity(
            handle,
            &capacity,
            @sizeOf(protocol.Capacity) - 1,
        ),
    );
    try std.testing.expectEqual(
        std.mem.zeroes(protocol.Capacity),
        capacity,
    );
    try std.testing.expectEqual(
        @intFromEnum(ResultCode.ok),
        abi.rm_metric_snapshot_query_capacity(
            handle,
            &capacity,
            @sizeOf(protocol.Capacity),
        ),
    );
    try std.testing.expectEqual(config.maximum_source_count, capacity.source_capacity);

    var header = std.mem.zeroes(protocol.SnapshotHeader);
    try std.testing.expectEqual(
        @intFromEnum(ResultCode.abi_mismatch),
        abi.rm_metric_snapshot_query_header(
            handle,
            &header,
            @sizeOf(protocol.SnapshotHeader) - 1,
        ),
    );
    try std.testing.expectEqual(std.mem.zeroes(protocol.SnapshotHeader), header);
    try std.testing.expectEqual(
        @intFromEnum(ResultCode.abi_mismatch),
        abi.rm_metric_snapshot_query_header(
            handle,
            &header,
            @sizeOf(protocol.SnapshotHeader) + 1,
        ),
    );
    try std.testing.expectEqual(std.mem.zeroes(protocol.SnapshotHeader), header);
    try std.testing.expectEqual(
        @intFromEnum(ResultCode.ok),
        abi.rm_metric_snapshot_query_header(
            handle,
            &header,
            @sizeOf(protocol.SnapshotHeader),
        ),
    );
    try std.testing.expectEqual(protocol.abi_version, header.abi_version);

    var rejected: ?*anyopaque = @ptrFromInt(8);
    try std.testing.expectEqual(
        @intFromEnum(ResultCode.invalid_argument),
        abi.rm_metric_snapshot_create(null, &rejected),
    );
    try std.testing.expectEqual(@as(?*anyopaque, null), rejected);
}

fn minimalConfig() protocol.Config {
    return .{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.Config),
        .generation = 1,
        .maximum_source_count = 1,
        .maximum_metric_count = 1,
        .maximum_rule_count = 1,
        .maximum_requested_count = 1,
        .maximum_observation_count = 1,
        .maximum_gpu_adapter_count = 1,
        .maximum_persistence_source_count = 1,
        .maximum_persistence_rule_count = 1,
        .maximum_persistence_gpu_count = 1,
        .source_index_capacity = 1,
        .metric_index_capacity = 1,
        .rule_index_capacity = 1,
        .gpu_index_capacity = 1,
        .maximum_future_skew_milliseconds = 1,
        .resident_byte_budget = 1 << 20,
        .catalog_contract_version = protocol.catalog_contract_version,
        .value_contract_version = protocol.value_contract_version,
        .observation_contract_version = protocol.observation_contract_version,
        .inventory_contract_version = protocol.inventory_contract_version,
        .persistence_contract_version = protocol.persistence_contract_version,
        .flags = 0,
        .maximum_plan_metric_count = 1,
        .maximum_source_mode_count = 1,
        .maximum_source_plan_count = 1,
        .maximum_metric_plan_count = 1,
        .gpu_luid_index_capacity = 1,
        .gpu_key_index_capacity = 1,
        .reserved_u32 = 0,
        .reserved = .{ 0, 0 },
    };
}

fn moduleLayoutFingerprint() u64 {
    const types = .{
        protocol.Config,
        protocol.Capacity,
        protocol.SourcePolicyInput,
        protocol.MetricDefinitionInput,
        protocol.CatalogReplaceInput,
        protocol.CompletionHeader,
        protocol.PlanInput,
        protocol.PlanOutput,
        protocol.PlanMetricInput,
        protocol.SourceModeInput,
        protocol.SourcePlanOutput,
        protocol.MetricPlanOutput,
        protocol.RequestedMetricInput,
        protocol.ObservationInput,
        protocol.CpuCounterInput,
        protocol.GpuInventoryInput,
        protocol.FinalizeInput,
        protocol.ControlInput,
        protocol.ReadInput,
        protocol.SnapshotHeader,
        protocol.MetricOutput,
        protocol.SourceOutput,
        protocol.GpuInventoryOutput,
        protocol.RuleStateOutput,
        protocol.SourcePersistenceOutput,
        protocol.PersistenceInput,
        protocol.PersistenceHeader,
    };
    var hash: u64 = 0xcbf29ce484222325;
    inline for (types) |T| {
        hash = fingerprintInteger(hash, layoutFingerprint(T));
    }
    return hash;
}

fn layoutFingerprint(comptime T: type) u64 {
    const fields = @typeInfo(T).@"struct".fields;
    var hash: u64 = 0xcbf29ce484222325;
    hash = fingerprintInteger(hash, @sizeOf(T));
    hash = fingerprintInteger(hash, @alignOf(T));
    hash = fingerprintInteger(hash, fields.len);
    inline for (fields) |field| {
        inline for (field.name) |byte| {
            hash = (hash ^ byte) *% 0x100000001b3;
        }
        hash = (hash ^ 0xff) *% 0x100000001b3;
        hash = fingerprintInteger(hash, @offsetOf(T, field.name));
        hash = fingerprintInteger(hash, @sizeOf(field.type));
        hash = fingerprintInteger(hash, @alignOf(field.type));
    }
    return hash;
}

fn fingerprintInteger(initial: u64, value: usize) u64 {
    var hash = initial;
    var remaining: u64 = @intCast(value);
    for (0..8) |_| {
        hash = (hash ^ @as(u8, @truncate(remaining))) *% 0x100000001b3;
        remaining >>= 8;
    }
    return hash;
}
