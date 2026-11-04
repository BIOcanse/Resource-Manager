//! Exact MetricFieldCode wire ordinals from ReadWriteCore/Fields/MetricFieldRegistry.cs.
//! These are field identifiers, never native metric-kind or source identifiers.
pub const cpu_fan_rpm: u32 = 19;
pub const cpu_fan_percent: u32 = 20;
pub const gpu_fan_percent: u32 = 63;
pub const gpu_fan_rpm: u32 = 64;
pub const gpu_engine_busy: u32 = 295;
pub const npu_engine_running_time_microseconds: u32 = 305;
pub const npu_engine_reported_running_time_ratio_percent: u32 = 306;
pub const npu_device_engine_busy_percent: u32 = 311;

/// C# ABI tests compare the values the implementation uses to its live enum.
pub export fn rm_metric_vector_field_code(slot: u32) u32 {
    return switch (slot) {
        0 => gpu_engine_busy,
        1 => npu_engine_reported_running_time_ratio_percent,
        2 => npu_device_engine_busy_percent,
        3 => cpu_fan_rpm,
        4 => cpu_fan_percent,
        5 => gpu_fan_percent,
        6 => gpu_fan_rpm,
        7 => npu_engine_running_time_microseconds,
        else => 0,
    };
}
