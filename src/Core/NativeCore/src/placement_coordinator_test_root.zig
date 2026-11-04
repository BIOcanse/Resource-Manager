test { _ = @import("scheduling/placement_coordinator/root.zig"); }

test "workspace workload byte evidence derives native widths from types" {
    const std = @import("std");
    const smart = @import("scheduling/smart_coordinator/scratch.zig");
    const state = @import("scheduling/smart_coordinator/state.zig");
    const protocol = @import("scheduling/smart_coordinator/protocol.zig");
    var config = std.mem.zeroes(protocol.Config);
    config.max_processes = 4096;
    config.max_software_groups = 2048;
    config.max_gpu_states = 32768;
    const before = smart.Layout.init(&config).requirements.byte_count;
    // Explicit workload: 450 distinct software/processes; each has identity,
    // CPU, and usage/VRAM for two GPUs. No extra ownership-only rows.
    // Native measures one staging column per entity kind (450/450/900) and
    // one candidate action per process; feedback rows match issued actions.
    const inputs = 450 * (2 + 2 * 2);
    const candidates = 450;
    const after = smart.Layout.initCounts(450, 450, 900).requirements.byte_count;
    try std.testing.expect(after < before);
    const table: u64 = 4 * try state.hashIndexCapacity(inputs);
    const total_before = std.mem.alignForward(u64, 104 * 32768 + 232 * 8192 + 7 + before, 8);
    const total_after = 104 * inputs + table + std.mem.alignForward(u64, 120 * candidates + 7 + after, 8) + 96 * candidates;
    try std.testing.expect(total_after < total_before);
    std.debug.print("Smart sizeof staged process/software/GPU/softwareGPU={d}/{d}/{d}/{d}; native before/after={d}/{d}; merged before/after={d}/{d}; I={d}, A=F={d}\n", .{
        @sizeOf(state.StagedProcess), @sizeOf(state.StagedSoftware), @sizeOf(state.StagedGpu), @sizeOf(state.StagedSoftwareGpu),
        before, after, total_before, total_after, inputs, candidates,
    });
}
