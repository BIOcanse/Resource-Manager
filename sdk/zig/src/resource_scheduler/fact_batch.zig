const std = @import("std");
const types = @import("types.zig");

pub const FactBatchInput = extern struct {
    abi_version: u32,
    struct_size: u32,
    projection_epoch: u64,
    configuration_generation: u64,
    target_count: u32,
    private_resource_count: u32,
    journal_pending_count: u32,
    action_budget: u32,
    flags: u32,
    reserved0: u32,
    reserved1: u64,
};

pub fn validate(
    input: *const FactBatchInput,
    config_generation: u64,
    target_count: usize,
    private_resource_count: usize,
    journal_pending_count: usize,
) types.PlanError!void {
    if (target_count > std.math.maxInt(u32) or
        private_resource_count > std.math.maxInt(u32) or
        journal_pending_count > std.math.maxInt(u32) or
        input.abi_version != types.protocol_version or
        input.struct_size != @sizeOf(FactBatchInput) or
        input.projection_epoch == 0 or
        input.configuration_generation != config_generation or
        input.target_count != target_count or
        input.private_resource_count != private_resource_count or
        input.journal_pending_count != journal_pending_count or
        input.action_budget == 0 or
        input.flags != 0 or
        input.reserved0 != 0 or
        input.reserved1 != 0)
    {
        return error.InvalidRequest;
    }
}

test "fact batch layout is stable" {
    try std.testing.expectEqual(@as(usize, 56), @sizeOf(FactBatchInput));
    try std.testing.expectEqual(@as(usize, 8), @offsetOf(FactBatchInput, "projection_epoch"));
    try std.testing.expectEqual(@as(usize, 32), @offsetOf(FactBatchInput, "journal_pending_count"));
    try std.testing.expectEqual(@as(usize, 48), @offsetOf(FactBatchInput, "reserved1"));
}
