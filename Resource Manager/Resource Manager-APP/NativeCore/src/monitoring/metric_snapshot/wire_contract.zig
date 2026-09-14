const protocol = @import("protocol.zig");

const fnv_offset: u64 = 0xcbf29ce484222325;
const fnv_prime: u64 = 0x100000001b3;

pub fn fingerprint() u64 {
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
    var hash: u64 = fnv_offset;
    hash = mix(hash, types.len);
    inline for (types) |T| hash = mixStruct(hash, T);

    const semantic_values = [_]u64{
        protocol.abi_version,
        protocol.catalog_contract_version,
        protocol.value_contract_version,
        protocol.observation_contract_version,
        protocol.inventory_contract_version,
        protocol.persistence_contract_version,
        protocol.cpu_counter_contract_version,

        @intFromEnum(protocol.Phase.empty),
        @intFromEnum(protocol.Phase.catalog_ready),
        @intFromEnum(protocol.Phase.completion_open),
        @intFromEnum(protocol.Phase.ready),
        @intFromEnum(protocol.Phase.failed_retained),
        @intFromEnum(protocol.SourceRole.metrics),
        @intFromEnum(protocol.SourceRole.gpu_inventory),
        @intFromEnum(protocol.SourceStatus.complete),
        @intFromEnum(protocol.SourceStatus.partial),
        @intFromEnum(protocol.SourceStatus.unavailable),
        @intFromEnum(protocol.SourceStatus.unsupported),
        @intFromEnum(protocol.SourceStatus.skipped),
        @intFromEnum(protocol.ZoneMode.normal),
        @intFromEnum(protocol.ZoneMode.low_power),
        @intFromEnum(protocol.ZoneMode.freeze),
        @intFromEnum(protocol.SourceAvailability.available),
        @intFromEnum(protocol.SourceAvailability.unavailable),
        @intFromEnum(protocol.SourceAvailability.unsupported),
        @intFromEnum(protocol.ObservationStatus.current),
        @intFromEnum(protocol.ObservationStatus.unavailable),
        @intFromEnum(protocol.ObservationStatus.unsupported),
        @intFromEnum(protocol.ObservationStatus.skipped),
        @intFromEnum(protocol.MetricStatus.current),
        @intFromEnum(protocol.MetricStatus.retained),
        @intFromEnum(protocol.MetricStatus.unavailable),
        @intFromEnum(protocol.MetricStatus.unsupported),
        @intFromEnum(protocol.MetricStatus.skipped),
        @intFromEnum(protocol.InventoryStatus.current),
        @intFromEnum(protocol.InventoryStatus.retained),
        @intFromEnum(protocol.InventoryStatus.unavailable),
        @intFromEnum(protocol.InventoryStatus.unsupported),
        @intFromEnum(protocol.RetentionPolicy.retain_last_good),
        @intFromEnum(protocol.RetentionPolicy.mark_unavailable),
        @intFromEnum(protocol.MetricKind.cpu_usage),
        @intFromEnum(protocol.MetricKind.cpu_frequency),
        @intFromEnum(protocol.MetricKind.cpu_sensor),
        @intFromEnum(protocol.MetricKind.memory_used),
        @intFromEnum(protocol.MetricKind.memory_total),
        @intFromEnum(protocol.MetricKind.virtual_memory_used),
        @intFromEnum(protocol.MetricKind.virtual_memory_total),
        @intFromEnum(protocol.MetricKind.gpu_usage),
        @intFromEnum(protocol.MetricKind.gpu_clock),
        @intFromEnum(protocol.MetricKind.gpu_vram_used),
        @intFromEnum(protocol.MetricKind.gpu_vram_total),
        @intFromEnum(protocol.MetricKind.gpu_sensor),
        @intFromEnum(protocol.MetricKind.custom_numeric),
        @intFromEnum(protocol.ScopeKind.host),
        @intFromEnum(protocol.ScopeKind.cpu),
        @intFromEnum(protocol.ScopeKind.memory),
        @intFromEnum(protocol.ScopeKind.gpu_adapter),
        @intFromEnum(protocol.ValueKind.float64),
        @intFromEnum(protocol.ValueKind.signed64),
        @intFromEnum(protocol.ValueKind.unsigned64),

        protocol.MetricFlags.percentage,
        protocol.MetricFlags.nonnegative,
        protocol.MetricFlags.used_value,
        protocol.MetricFlags.total_value,
        protocol.MetricFlags.known,
        protocol.SourceFlags.required,
        protocol.SourceFlags.known,
        protocol.SourceResetReason.incarnation_changed,
        protocol.SourceResetReason.counter_regressed,
        protocol.SourceResetReason.monotonic_regressed,
        protocol.SourceResetReason.tick_frequency_changed,
        protocol.SourceResetReason.arithmetic_overflow,
        protocol.SourceResetReason.known,
        protocol.SnapshotFlags.catalog_loaded,
        protocol.SnapshotFlags.committed_data,
        protocol.SnapshotFlags.retained_data,
        protocol.SnapshotFlags.gpu_inventory_present,
        protocol.SnapshotFlags.completion_open,
        protocol.SnapshotFlags.known,
        protocol.PlanFlags.include_gpu_inventory,
        protocol.PlanFlags.known,
        protocol.PlanOutputFlags.gpu_inventory_selected,
        protocol.PlanOutputFlags.gpu_inventory_unavailable,
        protocol.PlanOutputFlags.known,
        protocol.ObservationValid.value,
        protocol.ObservationValid.observed_at,
        protocol.ObservationValid.capability,
        protocol.ObservationValid.required,
        protocol.ObservationValid.known,
        protocol.CpuCounterValid.counters,
        protocol.CpuCounterValid.monotonic_time,
        protocol.CpuCounterValid.observed_at,
        protocol.CpuCounterValid.capability,
        protocol.CpuCounterValid.required,
        protocol.CpuCounterValid.known,
        protocol.MetricPlanFlags.selected,
        protocol.MetricPlanFlags.unavailable,
        protocol.MetricPlanFlags.known,
        protocol.SourcePlanFlags.selected,
        protocol.SourcePlanFlags.low_power,
        protocol.SourcePlanFlags.known,
        protocol.InventoryValid.adapter_identity,
        protocol.InventoryValid.observed_at,
        protocol.InventoryValid.capability,
        protocol.InventoryValid.topology,
        protocol.InventoryValid.required,
        protocol.InventoryValid.known,
    };
    hash = mix(hash, semantic_values.len);
    for (semantic_values) |value| hash = mix(hash, value);
    return hash;
}

fn mixStruct(initial: u64, comptime T: type) u64 {
    const fields = @typeInfo(T).@"struct".fields;
    var hash = mix(initial, @sizeOf(T));
    hash = mix(hash, @alignOf(T));
    hash = mix(hash, fields.len);
    inline for (fields) |field| {
        hash = mixBytes(hash, field.name);
        hash = mixByte(hash, 0xff);
        hash = mix(hash, @offsetOf(T, field.name));
        hash = mix(hash, @sizeOf(field.type));
        hash = mix(hash, @alignOf(field.type));
        hash = mixType(hash, field.type);
    }
    return hash;
}

fn mixType(initial: u64, comptime T: type) u64 {
    return switch (@typeInfo(T)) {
        .int => |info| blk: {
            var hash = mix(initial, 1);
            hash = mix(hash, @intFromEnum(info.signedness));
            break :blk mix(hash, info.bits);
        },
        .array => |info| blk: {
            var hash = mix(initial, 2);
            hash = mix(hash, info.len);
            break :blk mixType(hash, info.child);
        },
        else => @compileError("metric snapshot ABI fields must be integers or fixed arrays"),
    };
}

fn mixBytes(initial: u64, bytes: []const u8) u64 {
    var hash = initial;
    for (bytes) |byte| hash = mixByte(hash, byte);
    return hash;
}

fn mixByte(initial: u64, value: u8) u64 {
    return (initial ^ value) *% fnv_prime;
}

fn mix(initial: u64, value: anytype) u64 {
    var hash = initial;
    var remaining: u64 = @intCast(value);
    for (0..8) |_| {
        hash = mixByte(hash, @truncate(remaining));
        remaining >>= 8;
    }
    return hash;
}
