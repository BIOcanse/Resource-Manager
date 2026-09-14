const protocol = @import("protocol.zig");
const state = @import("state.zig");
const ResultCode = @import("../../common/result_codes.zig").ResultCode;

pub fn command(
    session: *state.Session,
    input: *const protocol.TrustCommandInput,
) ResultCode {
    if (!session.import_ready) return .unavailable;
    if (!protocol.validTrustCommand(input, &session.config)) return .abi_mismatch;
    if (session.planned_persistence_count != 0) return .unavailable;
    if (!session.operationAdvances(input.operation_epoch, input.command_monotonic_milliseconds)) {
        return .stale_frame;
    }
    const command_kind = @as(protocol.TrustCommandKind, @enumFromInt(input.command_kind));
    const existing = session.findTrust(input.target_handle, input.family_handle);
    const logical_utc = @max(session.logical_utc_ms, input.command_utc_milliseconds);

    switch (command_kind) {
        .add => {
            const slot_index = existing orelse state.firstFreeFrom(
                state.TrustSlot,
                session.trusts,
                session.next_trust_slot,
            ) orelse return .buffer_too_small;
            const mutation = session.allocateMutation() orelse return .out_of_memory;
            if (existing == null) {
                const generation = state.nextSlotGeneration(
                    session.trusts[slot_index].slot_generation,
                ) orelse return .out_of_memory;
                const handle = session.allocateTrustHandle() orelse return .out_of_memory;
                session.trusts[slot_index] = .{
                    .occupied = true,
                    .slot_generation = generation,
                    .trust_handle = handle,
                    .target_handle = input.target_handle,
                    .family_handle = input.family_handle,
                    .payload_handle = input.payload_handle,
                    .trusted_at_utc_ms = logical_utc,
                    .mutation_version = mutation,
                };
                if (!session.insertTrustIndex(slot_index)) {
                    session.trusts[slot_index].occupied = false;
                    return .buffer_too_small;
                }
                state.advanceFreeCursor(
                    &session.next_trust_slot,
                    session.trusts.len,
                    slot_index,
                );
            } else {
                var trust = &session.trusts[slot_index];
                if (trust.delete_pending) return .stale_frame;
                trust.payload_handle = input.payload_handle;
                trust.trusted_at_utc_ms = logical_utc;
                trust.mutation_version = mutation;
            }
        },
        .remove => {
            const slot_index = existing orelse return .no_data;
            var trust = &session.trusts[slot_index];
            if (trust.delete_pending) return .stale_frame;
            trust.delete_pending = true;
            trust.mutation_version = session.allocateMutation() orelse return .out_of_memory;
        },
    }

    session.commitOperation(
        input.operation_epoch,
        input.command_monotonic_milliseconds,
        input.command_utc_milliseconds,
    );
    return .ok;
}
