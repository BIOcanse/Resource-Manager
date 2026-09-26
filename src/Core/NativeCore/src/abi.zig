const std = @import("std");
const pdh = @import("telemetry/pdh_collector.zig");
const process_policy = @import("scheduling/process_policy_executor.zig");
const memory_cleanup = @import("scheduling/memory_cleanup_planner.zig");
const compute_scoring = @import("scheduling/compute_scoring/root.zig");
const compute_scoring_abi = compute_scoring.abi;
const memory_mode_controller = @import("scheduling/memory_mode_controller/root.zig");
const memory_mode_controller_abi = memory_mode_controller.abi;
const smart_coordinator_abi = @import("scheduling/smart_coordinator/abi.zig");
const placement_coordinator = @import("scheduling/placement_coordinator/root.zig");
const placement_coordinator_abi = placement_coordinator.abi;
const sampling_subscription = @import("monitoring/sampling_subscription/root.zig");
const sampling_subscription_abi = sampling_subscription.abi;
const metric_snapshot = @import("monitoring/metric_snapshot/root.zig");
const metric_snapshot_abi = metric_snapshot.abi;
const software_identity_catalog = @import("software_identity/catalog/root.zig");
const software_identity_catalog_abi = software_identity_catalog.abi;
const software_identity_resolution = @import("software_identity/resolution/root.zig");
const software_identity_resolution_abi = software_identity_resolution.abi;
const portable_software_registry = @import("software_identity/portable_registry/root.zig");
const portable_software_registry_abi = portable_software_registry.abi;
const report_coordinator = @import("reporting/report_coordinator/root.zig");
const report_coordinator_abi = report_coordinator.abi;
const file_query = @import("indexing/file_query/root.zig");
const file_query_abi = file_query.abi;
const public_service_coordinator = @import("public_services/service_coordinator/root.zig");
const public_service_coordinator_abi = public_service_coordinator.abi;
const display_coordinator = @import("device_topology/display_coordinator/root.zig");
const display_coordinator_abi = display_coordinator.abi;
const operation_coordinator = @import("operations/operation_coordinator/root.zig");
const operation_coordinator_abi = operation_coordinator.abi;
const adapter_instance_lease = @import("adapter_instance_lease/root.zig");
const transaction_journal_abi = @import("persistence/transaction_journal/abi.zig");
const applied_ownership = @import("persistence/applied_ownership/root.zig");
const applied_ownership_abi = applied_ownership.abi;
const ResultCode = @import("common/result_codes.zig").ResultCode;

comptime {
    _ = compute_scoring_abi.rm_compute_scoring_abi_version;
    _ = compute_scoring_abi.rm_compute_scoring_create;
    _ = compute_scoring_abi.rm_compute_scoring_destroy;
    _ = compute_scoring_abi.rm_compute_scoring_reconfigure;
    _ = compute_scoring_abi.rm_compute_scoring_query_capacity;
    _ = compute_scoring_abi.rm_compute_scoring_score;
    _ = compute_scoring_abi.rm_compute_scoring_weighted_cpu_use;
    _ = compute_scoring_abi.rm_compute_scoring_software_base_mean;
    _ = memory_mode_controller_abi.rm_memory_mode_controller_abi_version;
    _ = memory_mode_controller_abi.rm_memory_mode_controller_create;
    _ = memory_mode_controller_abi.rm_memory_mode_controller_destroy;
    _ = memory_mode_controller_abi.rm_memory_mode_controller_reconfigure;
    _ = memory_mode_controller_abi.rm_memory_mode_controller_query_capacity;
    _ = memory_mode_controller_abi.rm_memory_mode_controller_plan;
    _ = smart_coordinator_abi.rm_smart_coordinator_abi_version;
    _ = smart_coordinator_abi.rm_smart_coordinator_capacity_for_config;
    _ = smart_coordinator_abi.rm_smart_coordinator_create;
    _ = smart_coordinator_abi.rm_smart_coordinator_destroy;
    _ = smart_coordinator_abi.rm_smart_coordinator_query_capacity;
    _ = smart_coordinator_abi.rm_smart_coordinator_reconfigure;
    _ = smart_coordinator_abi.rm_smart_coordinator_reset;
    _ = smart_coordinator_abi.rm_smart_coordinator_plan;
    _ = smart_coordinator_abi.rm_smart_coordinator_feedback;
    _ = smart_coordinator_abi.rm_smart_coordinator_snapshot;
    _ = placement_coordinator_abi.rm_placement_coordinator_abi_version;
    _ = placement_coordinator_abi.rm_placement_coordinator_create;
    _ = placement_coordinator_abi.rm_placement_coordinator_destroy;
    _ = placement_coordinator_abi.rm_placement_coordinator_reconfigure;
    _ = placement_coordinator_abi.rm_placement_coordinator_reset;
    _ = placement_coordinator_abi.rm_placement_coordinator_query_capacity;
    _ = placement_coordinator_abi.rm_placement_coordinator_plan;
    _ = placement_coordinator_abi.rm_placement_coordinator_feedback;
    _ = placement_coordinator_abi.rm_placement_coordinator_snapshot;
    _ = sampling_subscription_abi.rm_sampling_subscription_abi_version;
    _ = sampling_subscription_abi.rm_sampling_subscription_create;
    _ = sampling_subscription_abi.rm_sampling_subscription_destroy;
    _ = sampling_subscription_abi.rm_sampling_subscription_reconfigure;
    _ = sampling_subscription_abi.rm_sampling_subscription_reset;
    _ = sampling_subscription_abi.rm_sampling_subscription_query_capacity;
    _ = sampling_subscription_abi.rm_sampling_subscription_track;
    _ = sampling_subscription_abi.rm_sampling_subscription_remove;
    _ = sampling_subscription_abi.rm_sampling_subscription_plan;
    _ = sampling_subscription_abi.rm_sampling_subscription_complete;
    _ = sampling_subscription_abi.rm_sampling_subscription_snapshot;
    _ = metric_snapshot_abi.rm_metric_snapshot_abi_version;
    _ = metric_snapshot_abi.rm_metric_snapshot_wire_contract_fingerprint;
    _ = metric_snapshot_abi.rm_metric_snapshot_create;
    _ = metric_snapshot_abi.rm_metric_snapshot_destroy;
    _ = metric_snapshot_abi.rm_metric_snapshot_reconfigure;
    _ = metric_snapshot_abi.rm_metric_snapshot_reset;
    _ = metric_snapshot_abi.rm_metric_snapshot_query_capacity;
    _ = metric_snapshot_abi.rm_metric_snapshot_replace_catalog;
    _ = metric_snapshot_abi.rm_metric_snapshot_calculate_catalog_fingerprint;
    _ = metric_snapshot_abi.rm_metric_snapshot_plan;
    _ = metric_snapshot_abi.rm_metric_snapshot_begin_completion;
    _ = metric_snapshot_abi.rm_metric_snapshot_submit_requested;
    _ = metric_snapshot_abi.rm_metric_snapshot_submit_observations;
    _ = metric_snapshot_abi.rm_metric_snapshot_submit_cpu_counter;
    _ = metric_snapshot_abi.rm_metric_snapshot_submit_gpu_inventory;
    _ = metric_snapshot_abi.rm_metric_snapshot_finalize_completion;
    _ = metric_snapshot_abi.rm_metric_snapshot_abort_completion;
    _ = metric_snapshot_abi.rm_metric_snapshot_query_header;
    _ = metric_snapshot_abi.rm_metric_snapshot_read;
    _ = metric_snapshot_abi.rm_metric_snapshot_export_state;
    _ = metric_snapshot_abi.rm_metric_snapshot_import_state;
    _ = software_identity_catalog_abi.rm_software_identity_catalog_abi_version;
    _ = software_identity_catalog_abi.rm_software_identity_catalog_create;
    _ = software_identity_catalog_abi.rm_software_identity_catalog_destroy;
    _ = software_identity_catalog_abi.rm_software_identity_catalog_reconfigure;
    _ = software_identity_catalog_abi.rm_software_identity_catalog_query_capacity;
    _ = software_identity_catalog_abi.rm_software_identity_catalog_replace;
    _ = software_identity_catalog_abi.rm_software_identity_catalog_match_known;
    _ = software_identity_catalog_abi.rm_software_identity_catalog_match;
    _ = software_identity_catalog_abi.rm_software_identity_catalog_snapshot;
    _ = software_identity_resolution_abi.rm_software_identity_resolution_abi_version;
    _ = software_identity_resolution_abi.rm_software_identity_resolution_create;
    _ = software_identity_resolution_abi.rm_software_identity_resolution_destroy;
    _ = software_identity_resolution_abi.rm_software_identity_resolution_reconfigure;
    _ = software_identity_resolution_abi.rm_software_identity_resolution_query_capacity;
    _ = software_identity_resolution_abi.rm_software_identity_resolution_replace_policy;
    _ = software_identity_resolution_abi.rm_software_identity_resolution_resolve;
    _ = software_identity_resolution_abi.rm_software_identity_resolution_snapshot;
    _ = portable_software_registry_abi.rm_software_identity_portable_registry_abi_version;
    _ = portable_software_registry_abi.rm_software_identity_portable_registry_create;
    _ = portable_software_registry_abi.rm_software_identity_portable_registry_destroy;
    _ = portable_software_registry_abi.rm_software_identity_portable_registry_reconfigure;
    _ = portable_software_registry_abi.rm_software_identity_portable_registry_query_capacity;
    _ = portable_software_registry_abi.rm_software_identity_portable_registry_import;
    _ = portable_software_registry_abi.rm_software_identity_portable_registry_observe;
    _ = portable_software_registry_abi.rm_software_identity_portable_registry_confirm_root;
    _ = portable_software_registry_abi.rm_software_identity_portable_registry_mark_missing;
    _ = portable_software_registry_abi.rm_software_identity_portable_registry_plan_persistence;
    _ = portable_software_registry_abi.rm_software_identity_portable_registry_apply_feedback;
    _ = portable_software_registry_abi.rm_software_identity_portable_registry_snapshot;
    _ = report_coordinator_abi.rm_report_coordinator_abi_version;
    _ = report_coordinator_abi.rm_report_coordinator_create;
    _ = report_coordinator_abi.rm_report_coordinator_destroy;
    _ = report_coordinator_abi.rm_report_coordinator_reconfigure;
    _ = report_coordinator_abi.rm_report_coordinator_query_capacity;
    _ = report_coordinator_abi.rm_report_coordinator_replace_rules;
    _ = report_coordinator_abi.rm_report_coordinator_import;
    _ = report_coordinator_abi.rm_report_coordinator_observe;
    _ = report_coordinator_abi.rm_report_coordinator_command_trust;
    _ = report_coordinator_abi.rm_report_coordinator_plan;
    _ = report_coordinator_abi.rm_report_coordinator_apply_feedback;
    _ = file_query_abi.rm_file_query_abi_version;
    _ = file_query_abi.rm_file_query_create;
    _ = file_query_abi.rm_file_query_destroy;
    _ = file_query_abi.rm_file_query_reconfigure;
    _ = file_query_abi.rm_file_query_query_capacity;
    _ = file_query_abi.rm_file_query_begin;
    _ = file_query_abi.rm_file_query_submit_candidates;
    _ = file_query_abi.rm_file_query_finalize;
    _ = file_query_abi.rm_file_query_reset;
    _ = file_query_abi.rm_file_query_snapshot;
    _ = public_service_coordinator_abi.rm_public_service_coordinator_abi_version;
    _ = public_service_coordinator_abi.rm_public_service_coordinator_create;
    _ = public_service_coordinator_abi.rm_public_service_coordinator_destroy;
    _ = public_service_coordinator_abi.rm_public_service_coordinator_query_capacity;
    _ = public_service_coordinator_abi.rm_public_service_coordinator_replace_catalog;
    _ = public_service_coordinator_abi.rm_public_service_coordinator_replace_models;
    _ = public_service_coordinator_abi.rm_public_service_coordinator_plan_model_acquisition;
    _ = public_service_coordinator_abi.rm_public_service_coordinator_complete_model_acquisition;
    _ = public_service_coordinator_abi.rm_public_service_coordinator_admit_request;
    _ = public_service_coordinator_abi.rm_public_service_coordinator_complete_request;
    _ = public_service_coordinator_abi.rm_public_service_coordinator_resolve_model;
    _ = public_service_coordinator_abi.rm_public_service_coordinator_begin_lease;
    _ = public_service_coordinator_abi.rm_public_service_coordinator_end_lease;
    _ = public_service_coordinator_abi.rm_public_service_coordinator_upsert_subscription;
    _ = public_service_coordinator_abi.rm_public_service_coordinator_remove_subscription;
    _ = public_service_coordinator_abi.rm_public_service_coordinator_enqueue_task;
    _ = public_service_coordinator_abi.rm_public_service_coordinator_plan_tasks;
    _ = public_service_coordinator_abi.rm_public_service_coordinator_complete_task;
    _ = public_service_coordinator_abi.rm_public_service_coordinator_cancel_queued_tasks;
    _ = public_service_coordinator_abi.rm_public_service_coordinator_read_capabilities;
    _ = public_service_coordinator_abi.rm_public_service_coordinator_snapshot;
    _ = display_coordinator_abi.rm_display_coordinator_abi_version;
    _ = display_coordinator_abi.rm_display_coordinator_create;
    _ = display_coordinator_abi.rm_display_coordinator_destroy;
    _ = display_coordinator_abi.rm_display_coordinator_query_capacity;
    _ = display_coordinator_abi.rm_display_coordinator_begin_refresh;
    _ = display_coordinator_abi.rm_display_coordinator_submit_source;
    _ = display_coordinator_abi.rm_display_coordinator_finalize;
    _ = display_coordinator_abi.rm_display_coordinator_abort_refresh;
    _ = display_coordinator_abi.rm_display_coordinator_snapshot;
    _ = display_coordinator_abi.rm_display_coordinator_read_nodes;
    _ = display_coordinator_abi.rm_display_coordinator_read_edges;
    _ = display_coordinator_abi.rm_display_coordinator_read_capabilities;
    _ = display_coordinator_abi.rm_display_coordinator_read_texts;
    _ = display_coordinator_abi.rm_display_coordinator_read_diff;
    _ = display_coordinator_abi.rm_display_coordinator_read_unresolved;
    _ = display_coordinator_abi.rm_display_coordinator_export_persistence;
    _ = display_coordinator_abi.rm_display_coordinator_import_persistence;
    _ = operation_coordinator_abi.rm_operation_coordinator_abi_version;
    _ = operation_coordinator_abi.rm_operation_coordinator_create;
    _ = operation_coordinator_abi.rm_operation_coordinator_destroy;
    _ = operation_coordinator_abi.rm_operation_coordinator_query_capacity;
    _ = operation_coordinator_abi.rm_operation_coordinator_reconfigure;
    _ = operation_coordinator_abi.rm_operation_coordinator_submit;
    _ = operation_coordinator_abi.rm_operation_coordinator_cancel;
    _ = operation_coordinator_abi.rm_operation_coordinator_plan;
    _ = operation_coordinator_abi.rm_operation_coordinator_feedback;
    _ = operation_coordinator_abi.rm_operation_coordinator_complete;
    _ = operation_coordinator_abi.rm_operation_coordinator_report_progress;
    _ = operation_coordinator_abi.rm_operation_coordinator_snapshot;
    _ = operation_coordinator_abi.rm_operation_coordinator_read_operations;
    _ = operation_coordinator_abi.rm_operation_coordinator_read_actions;
    _ = operation_coordinator_abi.rm_operation_coordinator_export_persistence;
    _ = operation_coordinator_abi.rm_operation_coordinator_import_persistence;
    _ = transaction_journal_abi.rm_transaction_journal_abi_version;
    _ = transaction_journal_abi.rm_transaction_journal_create_new;
    _ = transaction_journal_abi.rm_transaction_journal_open_existing;
    _ = transaction_journal_abi.rm_transaction_journal_destroy;
    _ = transaction_journal_abi.rm_transaction_journal_query_capacity;
    _ = transaction_journal_abi.rm_transaction_journal_prepare;
    _ = transaction_journal_abi.rm_transaction_journal_prepare_batch;
    _ = transaction_journal_abi.rm_transaction_journal_mutate;
    _ = transaction_journal_abi.rm_transaction_journal_stage_feedback;
    _ = transaction_journal_abi.rm_transaction_journal_recovery_evidence;
    _ = transaction_journal_abi.rm_transaction_journal_acknowledge;
    _ = transaction_journal_abi.rm_transaction_journal_get;
    _ = transaction_journal_abi.rm_transaction_journal_snapshot;
    _ = transaction_journal_abi.rm_transaction_journal_encode;
    _ = applied_ownership_abi.rm_applied_ownership_abi_version;
    _ = applied_ownership_abi.rm_applied_ownership_create_new;
    _ = applied_ownership_abi.rm_applied_ownership_open_existing;
    _ = applied_ownership_abi.rm_applied_ownership_destroy;
    _ = applied_ownership_abi.rm_applied_ownership_query_capacity;
    _ = applied_ownership_abi.rm_applied_ownership_promote;
    _ = applied_ownership_abi.rm_applied_ownership_plan_transition;
    _ = applied_ownership_abi.rm_applied_ownership_transition;
    _ = applied_ownership_abi.rm_applied_ownership_remove;
    _ = applied_ownership_abi.rm_applied_ownership_get;
    _ = applied_ownership_abi.rm_applied_ownership_snapshot;
    _ = applied_ownership_abi.rm_applied_ownership_encode;
    _ = applied_ownership_abi.rm_applied_ownership_decode_replace;
}

pub export fn rm_pdh_collector_abi_version() callconv(.c) u32 {
    return pdh.abi_version;
}

pub export fn rm_process_policy_executor_abi_version() callconv(.c) u32 {
    return process_policy.abi_version;
}

pub export fn rm_adapter_instance_lease_abi_version() callconv(.c) u32 {
    return adapter_instance_lease.abi_version;
}

pub export fn rm_adapter_instance_lease_create(
    config: ?*const adapter_instance_lease.Config,
    handle: ?*?*anyopaque,
) callconv(.c) i32 {
    const output = handle orelse return code(.invalid_argument);
    output.* = null;
    const session = adapter_instance_lease.Session.create(
        config orelse return code(.invalid_argument),
    ) catch |err| switch (err) {
        error.InvalidConfiguration => return code(.abi_mismatch),
        else => return code(.out_of_memory),
    };
    output.* = @ptrCast(session);
    return code(.ok);
}

pub export fn rm_adapter_instance_lease_destroy(handle: ?*anyopaque) callconv(.c) void {
    const session: *adapter_instance_lease.Session = @ptrCast(@alignCast(handle orelse return));
    session.destroy();
}

pub export fn rm_adapter_instance_lease_issue(
    handle: ?*anyopaque,
    input: ?*const adapter_instance_lease.IssueInput,
    output: ?*adapter_instance_lease.LeaseReceipt,
    output_size: u32,
) callconv(.c) i32 {
    if (output_size != @sizeOf(adapter_instance_lease.LeaseReceipt)) {
        return code(.abi_mismatch);
    }
    const session: *adapter_instance_lease.Session = @ptrCast(@alignCast(
        handle orelse return code(.invalid_argument),
    ));
    return code(session.issue(
        input orelse return code(.invalid_argument),
        output orelse return code(.invalid_argument),
    ));
}

pub export fn rm_adapter_instance_lease_renew(
    handle: ?*anyopaque,
    input: ?*const adapter_instance_lease.RenewInput,
    output: ?*adapter_instance_lease.LeaseReceipt,
    output_size: u32,
) callconv(.c) i32 {
    if (output_size != @sizeOf(adapter_instance_lease.LeaseReceipt)) {
        return code(.abi_mismatch);
    }
    const session: *adapter_instance_lease.Session = @ptrCast(@alignCast(
        handle orelse return code(.invalid_argument),
    ));
    return code(session.renew(
        input orelse return code(.invalid_argument),
        output orelse return code(.invalid_argument),
    ));
}

pub export fn rm_adapter_instance_lease_resolve(
    handle: ?*anyopaque,
    input: ?*const adapter_instance_lease.ResolveInput,
    output: ?*adapter_instance_lease.LeaseReceipt,
    output_size: u32,
) callconv(.c) i32 {
    if (output_size != @sizeOf(adapter_instance_lease.LeaseReceipt)) {
        return code(.abi_mismatch);
    }
    const session: *adapter_instance_lease.Session = @ptrCast(@alignCast(
        handle orelse return code(.invalid_argument),
    ));
    return code(session.resolve(
        input orelse return code(.invalid_argument),
        output orelse return code(.invalid_argument),
    ));
}

pub export fn rm_adapter_instance_lease_revoke(
    handle: ?*anyopaque,
    input: ?*const adapter_instance_lease.RevokeInput,
) callconv(.c) i32 {
    const session: *adapter_instance_lease.Session = @ptrCast(@alignCast(
        handle orelse return code(.invalid_argument),
    ));
    return code(session.revoke(input orelse return code(.invalid_argument)));
}

pub export fn rm_adapter_instance_lease_expire(
    handle: ?*anyopaque,
    now_timestamp: u64,
    expired_count: ?*u32,
) callconv(.c) i32 {
    const session: *adapter_instance_lease.Session = @ptrCast(@alignCast(
        handle orelse return code(.invalid_argument),
    ));
    return code(session.expire(
        now_timestamp,
        expired_count orelse return code(.invalid_argument),
    ));
}

pub export fn rm_pdh_collector_create(
    config: ?*const pdh.Config,
    handle: ?*?*anyopaque,
) callconv(.c) i32 {
    const actual_config = config orelse return code(.invalid_argument);
    const output = handle orelse return code(.invalid_argument);
    output.* = null;
    if (actual_config.abi_version != pdh.abi_version or actual_config.struct_size != @sizeOf(pdh.Config)) {
        return code(.abi_mismatch);
    }

    const collector = pdh.Collector.create(actual_config) catch return code(.out_of_memory);
    output.* = @ptrCast(collector);
    return code(.ok);
}

pub export fn rm_pdh_collector_destroy(handle: ?*anyopaque) callconv(.c) void {
    const opaque_handle = handle orelse return;
    const collector: *pdh.Collector = @ptrCast(@alignCast(opaque_handle));
    collector.destroy();
}

pub export fn rm_pdh_collector_request_topology_refresh(handle: ?*anyopaque) callconv(.c) i32 {
    const opaque_handle = handle orelse return code(.invalid_argument);
    const collector: *pdh.Collector = @ptrCast(@alignCast(opaque_handle));
    collector.requestTopologyRefresh();
    return code(.ok);
}

pub export fn rm_pdh_collector_sample(
    handle: ?*anyopaque,
    header: ?*pdh.FrameHeader,
) callconv(.c) i32 {
    const opaque_handle = handle orelse return code(.invalid_argument);
    const output = header orelse return code(.invalid_argument);
    if (output.abi_version != pdh.abi_version or output.struct_size != @sizeOf(pdh.FrameHeader)) {
        return code(.abi_mismatch);
    }

    const collector: *pdh.Collector = @ptrCast(@alignCast(opaque_handle));
    return code(collector.sample(output));
}

pub export fn rm_pdh_collector_copy_frame(
    handle: ?*anyopaque,
    expected_sequence: u64,
    engine_rows: ?[*]pdh.GpuEngineRow,
    engine_capacity: u32,
    memory_rows: ?[*]pdh.GpuMemoryRow,
    memory_capacity: u32,
    system_io: ?*pdh.SystemIo,
) callconv(.c) i32 {
    const opaque_handle = handle orelse return code(.invalid_argument);
    const io = system_io orelse return code(.invalid_argument);
    if (io.struct_size != @sizeOf(pdh.SystemIo)) return code(.abi_mismatch);

    const collector: *pdh.Collector = @ptrCast(@alignCast(opaque_handle));
    return code(collector.copyFrame(
        expected_sequence,
        engine_rows,
        engine_capacity,
        memory_rows,
        memory_capacity,
        io,
    ));
}

pub export fn rm_pdh_collector_get_diagnostics(
    handle: ?*anyopaque,
    diagnostics: ?*pdh.Diagnostics,
) callconv(.c) i32 {
    const opaque_handle = handle orelse return code(.invalid_argument);
    const output = diagnostics orelse return code(.invalid_argument);
    if (output.abi_version != pdh.abi_version or output.struct_size != @sizeOf(pdh.Diagnostics)) {
        return code(.abi_mismatch);
    }

    const collector: *pdh.Collector = @ptrCast(@alignCast(opaque_handle));
    collector.getDiagnostics(output);
    return code(.ok);
}

pub export fn rm_process_policy_apply_batch(
    header: ?*const process_policy.Header,
    items: ?[*]const process_policy.Item,
    item_capacity: u32,
    results: ?[*]process_policy.ItemResult,
    result_capacity: u32,
) callconv(.c) i32 {
    const actual_header = header orelse return code(.invalid_argument);
    return code(process_policy.execute(
        actual_header,
        items,
        item_capacity,
        results,
        result_capacity,
    ));
}

pub export fn rm_memory_cleanup_abi_version() callconv(.c) u32 {
    return memory_cleanup.abi_version;
}

pub export fn rm_memory_cleanup_create(
    config: ?*const memory_cleanup.Config,
    handle: ?*?*anyopaque,
) callconv(.c) i32 {
    const output = handle orelse return code(.invalid_argument);
    output.* = null;
    const session = memory_cleanup.Session.create(config orelse return code(.invalid_argument)) catch |err| switch (err) {
        error.InvalidConfiguration => return code(.abi_mismatch),
        else => return code(.out_of_memory),
    };
    output.* = @ptrCast(session);
    return code(.ok);
}

pub export fn rm_memory_cleanup_destroy(handle: ?*anyopaque) callconv(.c) void {
    const session: *memory_cleanup.Session = @ptrCast(@alignCast(handle orelse return));
    session.destroy();
}

pub export fn rm_memory_cleanup_reconfigure(
    handle: ?*anyopaque,
    config: ?*const memory_cleanup.Config,
) callconv(.c) i32 {
    const session: *memory_cleanup.Session = @ptrCast(@alignCast(handle orelse return code(.invalid_argument)));
    return code(session.reconfigure(config orelse return code(.invalid_argument)));
}

pub export fn rm_memory_cleanup_reset(handle: ?*anyopaque) callconv(.c) i32 {
    const session: *memory_cleanup.Session = @ptrCast(@alignCast(handle orelse return code(.invalid_argument)));
    session.reset();
    return code(.ok);
}

pub export fn rm_memory_cleanup_required_output_capacity(
    handle: ?*anyopaque,
    header: ?*const memory_cleanup.PlanHeader,
    input_capacity: u32,
    output_capacity: ?*u32,
) callconv(.c) i32 {
    const session: *memory_cleanup.Session = @ptrCast(@alignCast(handle orelse return code(.invalid_argument)));
    return code(session.requiredOutputCapacity(
        header orelse return code(.invalid_argument),
        input_capacity,
        output_capacity orelse return code(.invalid_argument),
    ));
}

pub export fn rm_memory_cleanup_plan(
    handle: ?*anyopaque,
    header: ?*memory_cleanup.PlanHeader,
    inputs: ?[*]const memory_cleanup.CandidateInput,
    input_capacity: u32,
    outputs: ?[*]memory_cleanup.DecisionOutput,
    output_capacity: u32,
) callconv(.c) i32 {
    const session: *memory_cleanup.Session = @ptrCast(@alignCast(handle orelse return code(.invalid_argument)));
    return code(session.plan(
        header orelse return code(.invalid_argument),
        inputs,
        input_capacity,
        outputs,
        output_capacity,
    ));
}

pub export fn rm_memory_cleanup_complete(
    handle: ?*anyopaque,
    feedback: ?[*]const memory_cleanup.FeedbackInput,
    feedback_count: u32,
) callconv(.c) i32 {
    const session: *memory_cleanup.Session = @ptrCast(@alignCast(handle orelse return code(.invalid_argument)));
    return code(session.complete(feedback, feedback_count));
}

fn code(result: ResultCode) i32 {
    return @intFromEnum(result);
}

test "ABI structures keep their strict layout" {
    try std.testing.expectEqual(@as(usize, 40), @sizeOf(adapter_instance_lease.Config));
    try std.testing.expectEqual(@as(usize, 152), @sizeOf(adapter_instance_lease.CallerFacts));
    try std.testing.expectEqual(@as(usize, 184), @sizeOf(adapter_instance_lease.IssueInput));
    try std.testing.expectEqual(@as(usize, 192), @sizeOf(adapter_instance_lease.RenewInput));
    try std.testing.expectEqual(@as(usize, 48), @sizeOf(adapter_instance_lease.ResolveInput));
    try std.testing.expectEqual(@as(usize, 32), @sizeOf(adapter_instance_lease.RevokeInput));
    try std.testing.expectEqual(@as(usize, 232), @sizeOf(adapter_instance_lease.LeaseReceipt));
    try std.testing.expectEqual(@as(usize, 16), @sizeOf(pdh.Config));
    try std.testing.expectEqual(@as(usize, 48), @sizeOf(pdh.FrameHeader));
    try std.testing.expectEqual(@as(usize, 24), @sizeOf(pdh.GpuEngineRow));
    try std.testing.expectEqual(@as(usize, 24), @sizeOf(pdh.GpuMemoryRow));
    try std.testing.expectEqual(@as(usize, 72), @sizeOf(pdh.SystemIo));
    try std.testing.expectEqual(@as(usize, 56), @sizeOf(pdh.Diagnostics));
    try std.testing.expectEqual(@as(usize, 32), @sizeOf(process_policy.Header));
    try std.testing.expectEqual(@as(usize, 48), @sizeOf(process_policy.Item));
    try std.testing.expectEqual(@as(usize, 48), @sizeOf(process_policy.ItemResult));
}

test {
    _ = sampling_subscription;
    _ = software_identity_catalog;
    _ = software_identity_resolution;
    _ = portable_software_registry;
    _ = report_coordinator;
    _ = file_query;
    _ = display_coordinator;
    _ = operation_coordinator;
}
