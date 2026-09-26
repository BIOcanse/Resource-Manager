const protocol = @import("protocol.zig");
const state = @import("state.zig");
const planner = @import("planner.zig");
const ranking = @import("ranking.zig");
const ResultCode = @import("../../common/result_codes.zig").ResultCode;

pub const Session = state.Session;

pub fn begin(
    session: *Session,
    input: *const protocol.BeginInput,
    query_pointer: ?[*]const u8,
    query_byte_count: u32,
    output: *protocol.PlanOutput,
    source_pointer: ?[*]protocol.SourcePlanOutput,
    source_capacity: u32,
    plan_pointer: ?[*]u8,
    plan_capacity: u32,
) ResultCode {
    const query = constByteSlice(query_pointer, query_byte_count) orelse return .invalid_argument;
    const sources = mutableSlice(
        protocol.SourcePlanOutput,
        source_pointer,
        source_capacity,
    ) orelse return .invalid_argument;
    const plan_bytes = mutableByteSlice(plan_pointer, plan_capacity) orelse
        return .invalid_argument;
    return planner.begin(session, input, query, output, sources, plan_bytes);
}

pub fn submit(
    session: *Session,
    input: *const protocol.SubmitInput,
    candidate_pointer: ?[*]const protocol.CandidateInput,
    candidate_count: u32,
    byte_pointer: ?[*]const u8,
    byte_count: u32,
    output: *protocol.SubmitOutput,
) ResultCode {
    const candidates = constSlice(
        protocol.CandidateInput,
        candidate_pointer,
        candidate_count,
    ) orelse return .invalid_argument;
    const bytes = constByteSlice(byte_pointer, byte_count) orelse return .invalid_argument;
    return ranking.submit(session, input, candidates, bytes, output);
}

pub fn finalize(
    session: *Session,
    input: *const protocol.FinalizeInput,
    result_pointer: ?[*]protocol.ResultOutput,
    result_capacity: u32,
    output: *protocol.FinalizeOutput,
) ResultCode {
    const results = mutableSlice(
        protocol.ResultOutput,
        result_pointer,
        result_capacity,
    ) orelse return .invalid_argument;
    return ranking.finalize(session, input, results, output);
}

fn constSlice(comptime T: type, pointer: ?[*]const T, count: u32) ?[]const T {
    if (count == 0) return &.{};
    return (pointer orelse return null)[0..count];
}

fn mutableSlice(comptime T: type, pointer: ?[*]T, count: u32) ?[]T {
    if (count == 0) return &.{};
    return (pointer orelse return null)[0..count];
}

fn constByteSlice(pointer: ?[*]const u8, count: u32) ?[]const u8 {
    if (count == 0) return &.{};
    return (pointer orelse return null)[0..count];
}

fn mutableByteSlice(pointer: ?[*]u8, count: u32) ?[]u8 {
    if (count == 0) return &.{};
    return (pointer orelse return null)[0..count];
}
