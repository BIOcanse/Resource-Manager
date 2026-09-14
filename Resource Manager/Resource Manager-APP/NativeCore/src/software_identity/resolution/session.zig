const protocol = @import("protocol.zig");
const state = @import("state.zig");
const ResultCode = @import("../../common/result_codes.zig").ResultCode;

pub const Session = state.Session;

pub fn replacePolicy(
    session: *Session,
    input: *const protocol.PolicyReplaceInput,
    policy_pointer: ?[*]const protocol.SourcePolicyInput,
    policy_count: u32,
) ResultCode {
    const policies = itemSlice(protocol.SourcePolicyInput, policy_pointer, policy_count) orelse
        return .invalid_argument;
    return session.replacePolicy(input, policies);
}

pub fn resolve(
    session: *Session,
    input: *const protocol.ResolveInput,
    observation_pointer: ?[*]const protocol.ObservationInput,
    observation_count: u32,
    output: *protocol.ResolutionOutput,
) ResultCode {
    const observations = itemSlice(protocol.ObservationInput, observation_pointer, observation_count) orelse
        return .invalid_argument;
    return session.resolve(input, observations, output);
}

fn itemSlice(comptime T: type, pointer: ?[*]const T, count: u32) ?[]const T {
    if (count == 0) return &.{};
    return (pointer orelse return null)[0..count];
}
