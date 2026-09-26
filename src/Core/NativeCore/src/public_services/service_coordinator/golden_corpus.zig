const protocol = @import("protocol.zig");

comptime {
    assertSize(protocol.Config, 224);
    assertSize(protocol.Capacity, 112);
    assertSize(protocol.CapabilityInput, 32);
    assertSize(protocol.RouteInput, 56);
    assertSize(protocol.CatalogReplaceInput, 72);
    assertSize(protocol.ModelInput, 40);
    assertSize(protocol.ModelAliasInput, 40);
    assertSize(protocol.ModelReplaceInput, 72);
    assertSize(protocol.ModelAcquisitionPlanInput, 48);
    assertSize(protocol.ModelAcquisitionPlanOutput, 64);
    assertSize(protocol.ModelAcquisitionCompletionInput, 56);
    assertSize(protocol.AccessInput, 72);
    assertSize(protocol.AccessOutput, 72);
    assertSize(protocol.RequestCompleteInput, 56);
    assertSize(protocol.RequestCompleteOutput, 40);
    assertSize(protocol.ModelResolveInput, 56);
    assertSize(protocol.ModelResolveOutput, 64);
    assertSize(protocol.LeaseBeginInput, 64);
    assertSize(protocol.LeaseOutput, 56);
    assertSize(protocol.LeaseEndInput, 56);
    assertSize(protocol.SubscriptionUpsertInput, 80);
    assertSize(protocol.SubscriptionOutput, 64);
    assertSize(protocol.SubscriptionRemoveInput, 56);
    assertSize(protocol.TaskEnqueueInput, 80);
    assertSize(protocol.TaskOutput, 96);
    assertSize(protocol.TaskPlanInput, 48);
    assertSize(protocol.TaskPlanOutput, 48);
    assertSize(protocol.TaskCompletionInput, 56);
    assertSize(protocol.TaskCompletionOutput, 48);
    assertSize(protocol.TaskCancelInput, 48);
    assertSize(protocol.TaskCancelOutput, 48);
    assertSize(protocol.CapabilityOutput, 48);
    assertSize(protocol.Snapshot, 144);

    assertOffset(protocol.Config, "generation", 8);
    assertOffset(protocol.Config, "maximum_capability_count", 32);
    assertOffset(protocol.Config, "capability_index_capacity", 68);
    assertOffset(protocol.Config, "retryable_task_outcome_mask", 120);
    assertOffset(protocol.Config, "rate_window_milliseconds", 128);
    assertOffset(protocol.Config, "resident_byte_budget", 176);
    assertOffset(protocol.Config, "flags", 184);
    assertOffset(protocol.Config, "model_catalog_acquisition_interval_milliseconds", 192);
    assertOffset(protocol.Config, "model_catalog_last_good_lifetime_milliseconds", 200);
    assertOffset(protocol.Config, "model_catalog_acquisition_timeout_milliseconds", 208);
    assertOffset(protocol.Config, "maximum_task_attempt_count", 216);
    assertOffset(protocol.Config, "retryable_http_status_policy_mask", 220);
    assertOffset(protocol.RouteInput, "path", 24);
    assertOffset(protocol.RouteInput, "method_mask", 32);
    assertOffset(protocol.AccessInput, "path", 40);
    assertOffset(protocol.AccessOutput, "request_handle", 16);
    assertOffset(protocol.RequestCompleteOutput, "request_handle", 8);
    assertOffset(protocol.SubscriptionUpsertInput, "base_score", 48);
    assertOffset(protocol.TaskOutput, "base_score", 40);
    assertOffset(protocol.TaskOutput, "kind", 64);
    assertOffset(protocol.TaskCompletionInput, "attempt", 32);
    assertOffset(protocol.TaskCompletionInput, "http_status_code", 36);
    assertOffset(protocol.TaskCompletionOutput, "next_wake_monotonic_milliseconds", 24);
    assertOffset(protocol.Snapshot, "catalog_generation", 48);
    assertOffset(protocol.Snapshot, "flags", 112);
    assertOffset(protocol.Snapshot, "model_acquisition_attempt_handle", 120);
}

fn assertSize(comptime T: type, comptime expected: usize) void {
    if (@sizeOf(T) != expected) @compileError(@typeName(T) ++ " size changed");
}

fn assertOffset(comptime T: type, comptime field: []const u8, comptime expected: usize) void {
    if (@offsetOf(T, field) != expected) {
        @compileError(@typeName(T) ++ "." ++ field ++ " offset changed");
    }
}
