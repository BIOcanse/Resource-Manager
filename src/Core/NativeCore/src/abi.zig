const freeze_reports_abi = @import("reporting/system_events/abi.zig");
comptime {
    _ = freeze_reports_abi.rm_freeze_create;
    _ = freeze_reports_abi.rm_freeze_destroy;
    _ = freeze_reports_abi.rm_freeze_start_events;
    _ = freeze_reports_abi.rm_freeze_stop_events;
    _ = freeze_reports_abi.rm_freeze_start_recorder;
    _ = freeze_reports_abi.rm_freeze_stop_recorder;
    _ = freeze_reports_abi.rm_freeze_submit;
    _ = freeze_reports_abi.rm_freeze_setting;
    _ = freeze_reports_abi.rm_freeze_commit_threshold;
    _ = freeze_reports_abi.rm_freeze_process_frame;
    _ = freeze_reports_abi.rm_freeze_request_with_error;
    _ = freeze_reports_abi.rm_freeze_error_response;
    _ = freeze_reports_abi.rm_freeze_lifecycle_with_error;
}
const kernel_leak_observer_abi = @import("monitoring/kernel_leak/observer_abi.zig");
const kernel_leak_items_abi = @import("monitoring/kernel_leak/items_abi.zig");
const kernel_leak_input_abi = @import("monitoring/kernel_leak/input_adapter_abi.zig");
const kernel_leak_attribution_abi = @import("platform/windows/kernel_leak_attribution_abi.zig");
comptime {
    _ = kernel_leak_observer_abi.rm_kernel_leak_run;
    _ = kernel_leak_observer_abi.rm_kernel_leak_series_size;
    _ = kernel_leak_observer_abi.rm_kernel_leak_series_init;
    _ = kernel_leak_observer_abi.rm_kernel_leak_series_observe;
    _ = kernel_leak_observer_abi.rm_kernel_leak_series_clear;
    _ = kernel_leak_observer_abi.rm_kernel_leak_series_metrics;
    _ = kernel_leak_items_abi.rm_kernel_leak_items_size;
    _ = kernel_leak_items_abi.rm_kernel_leak_items_init;
    _ = kernel_leak_items_abi.rm_kernel_leak_items_restore;
    _ = kernel_leak_items_abi.rm_kernel_leak_items_plan;
    _ = kernel_leak_items_abi.rm_kernel_leak_items_clear;
    _ = kernel_leak_items_abi.rm_kernel_leak_items_snapshot;
    _ = kernel_leak_items_abi.rm_kernel_leak_items_commit;
    _ = kernel_leak_items_abi.rm_kernel_leak_items_reserve;
    _ = kernel_leak_items_abi.rm_kernel_leak_items_metadata;
    _ = kernel_leak_items_abi.rm_kernel_leak_items_service;
    _ = kernel_leak_items_abi.rm_kernel_leak_item_metadata_valid;
    _ = kernel_leak_input_abi.rm_kernel_leak_input_capture;
    _ = kernel_leak_attribution_abi.rm_kernel_leak_attribution_workspace_bytes;
    _ = kernel_leak_attribution_abi.rm_kernel_leak_attribution_required;
    _ = kernel_leak_attribution_abi.rm_kernel_leak_attribution_capture;
    _ = kernel_leak_attribution_abi.rm_kernel_leak_attribution_compose;
}
const notifications_abi = @import("notifications/abi.zig");
comptime {
    _ = notifications_abi.rm_notifications_create;
    _ = notifications_abi.rm_notifications_destroy;
    _ = notifications_abi.rm_notifications_read;
    _ = notifications_abi.rm_notifications_post;
    _ = notifications_abi.rm_notifications_subscribe;
    _ = notifications_abi.rm_notifications_unsubscribe;
    _ = notifications_abi.rm_notifications_owned_bytes;
    _ = notifications_abi.rm_notifications_scope;
    _ = notifications_abi.rm_notifications_project;
    _ = notifications_abi.rm_notifications_sequences_equal;
}
const dependency_cache_abi = @import("dependencies/cache/abi.zig");
const dependency_probe_abi = @import("dependencies/probe/abi.zig");
comptime {
    _ = dependency_cache_abi.rm_dependency_cache_initialize;
    _ = dependency_cache_abi.rm_dependency_cache_execute;
    _ = dependency_probe_abi.rm_dependency_probe_execute;
    _ = dependency_probe_abi.rm_dependency_path_fact;
}
const interaction_access_abi = @import("interaction/capabilities/access_abi.zig");
comptime {
    _ = interaction_access_abi.rm_interaction_access_evaluate;
    _ = interaction_access_abi.rm_interaction_access_read;
    _ = interaction_access_abi.rm_interaction_access_process_read;
    _ = interaction_access_abi.rm_interaction_access_gpu_operation;
}

const loopback_tcp_abi = @import("security/loopback_tcp/adapters/abi.zig");
const cpu_monopoly_abi = @import("control/cpu_monopoly_protection/abi.zig");
comptime {
    _ = cpu_monopoly_abi.rm_cpu_monopoly_row_size;
    _ = cpu_monopoly_abi.rm_cpu_monopoly_migrate;
    _ = cpu_monopoly_abi.rm_cpu_monopoly_can_automatic_write;
    _ = cpu_monopoly_abi.rm_cpu_monopoly_end_reason;
    _ = cpu_monopoly_abi.rm_cpu_monopoly_begin;
    _ = cpu_monopoly_abi.rm_cpu_monopoly_unavailable;
    _ = cpu_monopoly_abi.rm_cpu_monopoly_observe;
    _ = cpu_monopoly_abi.rm_cpu_monopoly_select;
    _ = cpu_monopoly_abi.rm_cpu_monopoly_finish;
    _ = cpu_monopoly_abi.rm_cpu_monopoly_explicit;
    _ = cpu_monopoly_abi.rm_cpu_monopoly_eligible;
    _ = cpu_monopoly_abi.rm_cpu_monopoly_restore;
    _ = cpu_monopoly_abi.rm_cpu_monopoly_sweep;
}
const device_restart_abi = @import("platform/windows/device_restart/abi.zig");
const device_actions = @import("reporting/device_actions/core.zig");
const crash_dump_keys = @import("platform/windows/crash_dump_keys/abi.zig");
comptime {
    _ = crash_dump_keys.rm_crash_dump_keys_plan;
    _ = crash_dump_keys.rm_crash_dump_keys_capture;
    _ = crash_dump_keys.rm_crash_dump_keys_apply;
    _ = crash_dump_keys.rm_crash_dump_keys_inspect;
    _ = device_restart_abi.rm_device_restart;
    _ = device_restart_abi.rm_device_resolve_driver;
    _ = device_actions.rm_device_action_execute;
    _ = device_actions.rm_device_action_validate_capacity;
    _ = device_actions.rm_device_action_report_supported;
    _ = device_actions.rm_device_action_recover_record;
}
const pdh_gpu_abi = @import("platform/windows/pdh/gpu_abi.zig");
comptime {
    _ = pdh_gpu_abi.rm_pdh_gpu_workspace;
    _ = pdh_gpu_abi.rm_pdh_gpu_project;
    _ = pdh_gpu_abi.rm_pdh_gpu_parse;
    _ = pdh_gpu_abi.rm_pdh_gpu_shared;
    _ = pdh_gpu_abi.rm_pdh_gpu_decode;
    _ = pdh_gpu_abi.rm_pdh_gpu_shared_query;
    _ = pdh_gpu_abi.rm_pdh_gpu_shared_query_windows;
}
const settings_requests = @import("interaction/settings/requests.zig");
comptime {
    _ = settings_requests.rm_settings_request_next;
    _ = settings_requests.rm_settings_request_text;
    _ = settings_requests.rm_settings_report_rules_encode;
}
comptime {
    _ = loopback_tcp_abi.rm_loopback_tcp_find;
}
const crash_reports_abi = @import("interaction/http/crash_reports_abi.zig");
const crash_capabilities_abi = @import("interaction/capabilities/crash_reports_abi.zig");
const resource_bars_abi = @import("resource_breakdown/current_bars_abi.zig");
comptime {
    _ = resource_bars_abi.rm_resource_bars_project;
}
const crash_parameters_abi = @import("interaction/settings/crash_reports_abi.zig");
comptime {
    _ = crash_parameters_abi.rm_crash_report_parameters_resolve_mode;
    _ = crash_parameters_abi.rm_crash_report_parameters_keep_count;
}
const cpu_os_features_abi = @import("platform/windows/cpu_features_abi.zig");
comptime {
    _ = cpu_os_features_abi.rm_cpu_os_features_capture;
    _ = cpu_os_features_abi.rm_cpu_os_features_capture_with_callbacks;
}
const hook_wait_abi = @import("interaction/http/hook_wait_abi.zig");
comptime {
    _ = hook_wait_abi.rm_hook_wait_select;
    _ = hook_wait_abi.rm_hook_wait_encode_stream;
    _ = hook_wait_abi.rm_hook_wait_string;
    _ = hook_wait_abi.rm_hook_wait_datetime;
    _ = hook_wait_abi.rm_hook_wait_null;
    _ = hook_wait_abi.rm_hook_wait_bool;
    _ = hook_wait_abi.rm_hook_wait_i64;
    _ = hook_wait_abi.rm_hook_wait_u64;
    _ = hook_wait_abi.rm_hook_wait_f64;
    _ = hook_wait_abi.rm_hook_wait_decimal;
    _ = hook_wait_abi.rm_hook_wait_action_state;
}
const targeted_recording_csv_abi = @import("interaction/exports/targeted_recording_csv_abi.zig");
comptime {
    _ = targeted_recording_csv_abi.rm_targeted_recording_csv_encode;
    _ = targeted_recording_csv_abi.rm_targeted_recording_csv_measure;
    _ = targeted_recording_csv_abi.rm_targeted_recording_csv_text;
    _ = targeted_recording_csv_abi.rm_targeted_recording_csv_integer;
    _ = targeted_recording_csv_abi.rm_targeted_recording_csv_unsigned;
    _ = targeted_recording_csv_abi.rm_targeted_recording_csv_boolean;
    _ = targeted_recording_csv_abi.rm_targeted_recording_csv_number;
    _ = targeted_recording_csv_abi.rm_targeted_recording_csv_datetime;
    _ = targeted_recording_csv_abi.rm_targeted_recording_csv_source;
    _ = targeted_recording_csv_abi.rm_targeted_recording_csv_compare;
    _ = targeted_recording_csv_abi.rm_targeted_recording_csv_metric;
}
comptime {
    const tier_abi = @import("interaction/http/memory_cpu_tier_abi.zig");
    const tier_parameters_abi = @import("interaction/settings/memory_cpu_tier_abi.zig");
    _ = tier_parameters_abi.rm_memory_cpu_tier_resolve_parameters;
    _ = tier_abi.rm_memory_cpu_tier_use_published;
    _ = tier_abi.rm_memory_cpu_tier_encode_stream;
    _ = crash_capabilities_abi.rm_crash_capability_invoke;
    _ = crash_reports_abi.rm_crash_reports_describe;
    _ = crash_reports_abi.rm_crash_reports_encode_stream;
    _ = crash_reports_abi.rm_crash_reports_encode;
    _ = crash_reports_abi.rm_crash_reports_string;
    _ = crash_reports_abi.rm_crash_reports_datetime;
}

const kernel_etw_shared_abi = @import("telemetry/kernel_etw/shared_abi.zig");
comptime {
    _ = kernel_etw_shared_abi.rm_kernel_etw_shared_create_with_allocator;
    _ = kernel_etw_shared_abi.rm_kernel_etw_shared_set_outputs;
    _ = kernel_etw_shared_abi.rm_kernel_etw_shared_query_residency_diagnostics;
    _ = kernel_etw_shared_abi.rm_kernel_etw_shared_query_interrupt_metadata;
    _ = kernel_etw_shared_abi.rm_kernel_etw_shared_configure_consumer_counter;
    _ = kernel_etw_shared_abi.rm_kernel_etw_shared_query_consumer_counter;
    _ = kernel_etw_shared_abi.rm_kernel_etw_shared_query_event_counter;
    _ = kernel_etw_shared_abi.rm_kernel_etw_shared_retire_consumer_counter;
    _ = kernel_etw_shared_abi.rm_kernel_etw_shared_take_retired_consumer_counter;
    _ = kernel_etw_shared_abi.rm_kernel_etw_shared_query_renderer_stamp;
    _ = kernel_etw_shared_abi.rm_kernel_etw_shared_acquire_outputs;
    _ = kernel_etw_shared_abi.rm_kernel_etw_shared_create_in_region;
    _ = kernel_etw_shared_abi.rm_kernel_etw_shared_open;
    _ = kernel_etw_shared_abi.rm_kernel_etw_shared_process;
    _ = kernel_etw_shared_abi.rm_kernel_etw_shared_request_stop;
    _ = kernel_etw_shared_abi.rm_kernel_etw_shared_stop;
    _ = kernel_etw_shared_abi.rm_kernel_etw_shared_close_trace;
    _ = kernel_etw_shared_abi.rm_kernel_etw_shared_destroy;
    _ = kernel_etw_shared_abi.rm_kernel_etw_shared_submit;
    _ = kernel_etw_shared_abi.rm_kernel_etw_shared_configure;
    _ = kernel_etw_shared_abi.rm_kernel_etw_shared_request_refresh;
    _ = kernel_etw_shared_abi.rm_kernel_etw_shared_request_residency;
    _ = kernel_etw_shared_abi.rm_kernel_etw_shared_configure_processors;
    _ = kernel_etw_shared_abi.rm_kernel_etw_shared_set_enabled;
    _ = kernel_etw_shared_abi.rm_kernel_etw_shared_acquire;
    _ = kernel_etw_shared_abi.rm_kernel_etw_shared_acquire_residency;
    _ = kernel_etw_shared_abi.rm_kernel_etw_shared_acquire_interrupts;
    _ = kernel_etw_shared_abi.rm_kernel_etw_shared_acquire_interrupts_at;
    _ = kernel_etw_shared_abi.rm_kernel_etw_shared_release;
    _ = kernel_etw_shared_abi.rm_kernel_etw_shared_status;
    _ = kernel_etw_shared_abi.rm_kernel_etw_shared_query_published_progress;
    _ = kernel_etw_shared_abi.rm_kernel_etw_shared_events_lost;
    _ = kernel_etw_shared_abi.rm_kernel_etw_shared_query_region_usage;
    _ = kernel_etw_shared_abi.rm_kernel_etw_shared_set_replay_clock;
    _ = kernel_etw_shared_abi.rm_kernel_etw_shared_acquire_io;
    _ = kernel_etw_shared_abi.rm_kernel_etw_shared_acquire_notifications;
    _ = kernel_etw_shared_abi.rm_kernel_etw_shared_acquire_renderer;
    _ = kernel_etw_shared_abi.rm_kernel_etw_shared_set_publication_callback;
    _ = kernel_etw_shared_abi.rm_kernel_etw_shared_acquire_thread_diagnostics;
    _ = kernel_etw_shared_abi.rm_kernel_etw_shared_acquire_thread_diagnostics_at;
    _ = kernel_etw_shared_abi.rm_kernel_etw_shared_clock;
    _ = kernel_etw_shared_abi.rm_kernel_etw_shared_query_owner_usage;
    _ = kernel_etw_shared_abi.rm_kernel_etw_shared_submit_tracked;
    _ = kernel_etw_shared_abi.rm_kernel_etw_shared_reset_io_at;
    _ = kernel_etw_shared_abi.rm_kernel_etw_shared_query_failure;
    _ = kernel_etw_shared_abi.rm_kernel_etw_shared_query_failure_detail;
    _ = kernel_etw_shared_abi.rm_kernel_etw_shared_query_failure_allocation;
    _ = kernel_etw_shared_abi.rm_kernel_etw_shared_query_disk_config;
}
const kernel_etw_owned_test = @import("telemetry/kernel_etw/owned_allocator_test_abi.zig");
comptime {
    _ = kernel_etw_owned_test.rm_kernel_etw_test_owned_drain;
    _ = kernel_etw_owned_test.rm_kernel_etw_test_owned_publish;
    _ = kernel_etw_owned_test.rm_kernel_etw_test_owned_cswitch;
}

const kernel_etw_profile_abi = @import("telemetry/kernel_etw/shared_profile_abi.zig");
comptime {
    _ = kernel_etw_profile_abi.rm_kernel_etw_shared_profile_enable;
    _ = kernel_etw_profile_abi.rm_kernel_etw_shared_profile_query;
    _ = kernel_etw_profile_abi.rm_kernel_etw_shared_profile_query_cpu_detail;
}

const kernel_etw_io_replay_abi = @import("telemetry/kernel_etw/io_replay_abi.zig");
comptime {
    _ = kernel_etw_io_replay_abi.rm_kernel_etw_io_replay_create;
    _ = kernel_etw_io_replay_abi.rm_kernel_etw_io_replay_destroy;
    _ = kernel_etw_io_replay_abi.rm_kernel_etw_io_replay_disk;
    _ = kernel_etw_io_replay_abi.rm_kernel_etw_io_replay_network;
    _ = kernel_etw_io_replay_abi.rm_kernel_etw_io_replay_process_changed;
    _ = kernel_etw_io_replay_abi.rm_kernel_etw_io_replay_invalidate;
    _ = kernel_etw_io_replay_abi.rm_kernel_etw_io_replay_snapshot;
    _ = kernel_etw_io_replay_abi.rm_kernel_etw_io_replay_shared_create;
    _ = kernel_etw_io_replay_abi.rm_kernel_etw_io_replay_shared_destroy;
    _ = kernel_etw_io_replay_abi.rm_kernel_etw_io_replay_shared_snapshot;
    _ = kernel_etw_io_replay_abi.rm_kernel_etw_io_replay_shared_disk;
    _ = kernel_etw_io_replay_abi.rm_kernel_etw_io_replay_shared_fail;
    _ = kernel_etw_io_replay_abi.rm_kernel_etw_io_replay_shared_loss;
    _ = kernel_etw_io_replay_abi.rm_kernel_etw_io_replay_shared_irq_counts;
    _ = kernel_etw_io_replay_abi.rm_kernel_etw_io_replay_file_create;
    _ = kernel_etw_io_replay_abi.rm_kernel_etw_io_replay_file_usage;
    _ = kernel_etw_io_replay_abi.rm_kernel_etw_io_replay_file_destroy;
    _ = kernel_etw_io_replay_abi.rm_kernel_etw_io_replay_file_observe;
    _ = kernel_etw_io_replay_abi.rm_kernel_etw_test_shared_disk;
    _ = kernel_etw_io_replay_abi.rm_kernel_etw_test_shared_network;
    _ = kernel_etw_io_replay_abi.rm_kernel_etw_test_shared_image;
    _ = kernel_etw_io_replay_abi.rm_kernel_etw_test_shared_interrupt;
    _ = kernel_etw_io_replay_abi.rm_kernel_etw_test_shared_loss;
    _ = kernel_etw_io_replay_abi.rm_kernel_etw_test_shared_thread;
    _ = kernel_etw_io_replay_abi.rm_kernel_etw_test_shared_process;
    _ = kernel_etw_io_replay_abi.rm_kernel_etw_test_shared_cswitch;
    _ = kernel_etw_io_replay_abi.rm_kernel_etw_test_shared_initialize;
    _ = kernel_etw_io_replay_abi.rm_kernel_etw_test_shared_raw;
    _ = kernel_etw_io_replay_abi.rm_kernel_etw_test_shared_diagnostic_usage;
    _ = kernel_etw_io_replay_abi.rm_kernel_etw_test_shared_diagnostic_clock;
    _ = kernel_etw_io_replay_abi.rm_kernel_etw_test_shared_diagnostic_process;
    _ = kernel_etw_io_replay_abi.rm_kernel_etw_test_shared_diagnostic_thread;
    _ = kernel_etw_io_replay_abi.rm_kernel_etw_test_shared_pending_diagnostic_samples;
    _ = kernel_etw_io_replay_abi.rm_kernel_etw_test_shared_diagnostic_input_usage;
    _ = kernel_etw_io_replay_abi.rm_kernel_etw_test_shared_chain_publication;
}
const std = @import("std");
comptime {
    _ = @import("shared_section_exports.zig");
}
comptime {
    @import("monitoring/metric_owner/root.zig").exportAnchor();
}
comptime {
    @import("monitoring/metric_owner/support_exports.zig").exportAnchor();
}
comptime {
    @import("monitoring/metric_owner/discovery_exports.zig").exportAnchor();
}
comptime {
    @import("monitoring/metric_owner/sdk_dispatch.zig").exportAnchor();
}
const invocation_abi = @import("rm_invocation_abi");
comptime {
    _ = invocation_abi.rm_invocation_owner_run;
    _ = invocation_abi.rm_invocation_payload_string;
    _ = invocation_abi.rm_invocation_crash_target;
}
const data_area_abi = @import("data_area/exports.zig");
comptime {
    _ = data_area_abi.rm_data_area_abi_version;
    _ = data_area_abi.rm_data_area_create;
    _ = data_area_abi.rm_data_area_apply;
    _ = data_area_abi.rm_data_area_binding;
    _ = data_area_abi.rm_data_area_read_view;
    _ = data_area_abi.rm_data_area_acquire;
    _ = data_area_abi.rm_data_area_release;
    _ = data_area_abi.rm_data_area_stop;
    _ = data_area_abi.rm_data_area_destroy;
}
const kernel_etw_abi = @import("telemetry/kernel_etw/abi.zig");
const kernel_etw_trace_abi = @import("telemetry/kernel_etw/trace_abi.zig");
const kernel_etw_interrupt_abi = @import("telemetry/kernel_etw/interrupt_abi.zig");
comptime {
    _ = kernel_etw_interrupt_abi.rm_kernel_etw_interrupt_abi_version;
    _ = kernel_etw_interrupt_abi.rm_kernel_etw_interrupt_create;
    _ = kernel_etw_interrupt_abi.rm_kernel_etw_interrupt_create_in_region;
    _ = kernel_etw_interrupt_abi.rm_kernel_etw_interrupt_query_region_usage;
    _ = kernel_etw_interrupt_abi.rm_kernel_etw_interrupt_destroy;
    _ = kernel_etw_interrupt_abi.rm_kernel_etw_interrupt_image;
    _ = kernel_etw_interrupt_abi.rm_kernel_etw_interrupt_observe;
    _ = kernel_etw_interrupt_abi.rm_kernel_etw_interrupt_reset;
    _ = kernel_etw_interrupt_abi.rm_kernel_etw_interrupt_refresh_loss;
    _ = kernel_etw_interrupt_abi.rm_kernel_etw_interrupt_acquire_view;
    _ = kernel_etw_interrupt_abi.rm_kernel_etw_interrupt_release_view;
    _ = kernel_etw_interrupt_abi.rm_kernel_etw_interrupt_query_usage;
    _ = kernel_etw_trace_abi.rm_kernel_etw_trace_bind_interrupts;
    _ = kernel_etw_trace_abi.rm_kernel_etw_trace_open;
    _ = kernel_etw_trace_abi.rm_kernel_etw_trace_process;
    _ = kernel_etw_trace_abi.rm_kernel_etw_trace_request_stop;
    _ = kernel_etw_trace_abi.rm_kernel_etw_trace_stop;
    _ = kernel_etw_trace_abi.rm_kernel_etw_trace_close;
    _ = kernel_etw_trace_abi.rm_kernel_etw_trace_status;
    _ = kernel_etw_trace_abi.rm_kernel_etw_trace_clock;
    _ = kernel_etw_trace_abi.rm_kernel_etw_trace_query_path_mapping;
    _ = kernel_etw_abi.rm_kernel_etw_abi_version;
    _ = kernel_etw_abi.rm_kernel_etw_create;
    _ = kernel_etw_abi.rm_kernel_etw_create_in_region;
    _ = kernel_etw_abi.rm_kernel_etw_query_region_usage;
    _ = kernel_etw_abi.rm_kernel_etw_destroy;
    _ = kernel_etw_abi.rm_kernel_etw_apply;
    _ = kernel_etw_abi.rm_kernel_etw_acquire_residency;
    _ = kernel_etw_abi.rm_kernel_etw_release_residency;
    _ = kernel_etw_abi.rm_kernel_etw_query_usage;
    _ = kernel_etw_abi.rm_kernel_etw_query_capacity_failure;
}

// Capacity diagnostics have their own version so adding allocation details
// cannot change the version stamped into persisted owner records.
pub export fn rm_workspace_capacity_abi_version() callconv(.c) u32 {
    return 0x0002_0000;
}
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
const control_abi = @import("control/abi.zig");
const ResultCode = @import("common/result_codes.zig").ResultCode;

comptime {
    _ = control_abi.rm_control_abi_version;
    _ = control_abi.rm_control_profile_host_blocks;
    _ = control_abi.rm_control_create;
    _ = control_abi.rm_control_activate;
    _ = control_abi.rm_control_destroy;
    _ = control_abi.rm_control_claim_write;
    _ = control_abi.rm_control_claim_cell;
    _ = control_abi.rm_control_claim_read_function;
    _ = control_abi.rm_control_notify_changed;
    _ = control_abi.rm_control_read;
    _ = control_abi.rm_control_read_presence;
    _ = control_abi.rm_control_read_state;
    _ = control_abi.rm_control_publish;
    _ = control_abi.rm_control_publish_presence;
    _ = control_abi.rm_control_bind;
    _ = control_abi.rm_control_unbind;
    _ = control_abi.rm_control_register_reader;
    _ = control_abi.rm_control_unregister_reader;
    _ = control_abi.rm_control_run_running;
    _ = control_abi.rm_control_run_returned;
    _ = control_abi.rm_control_shutdown;
    _ = control_abi.rm_control_snapshot;
    _ = compute_scoring_abi.rm_compute_scoring_abi_version;
    _ = compute_scoring_abi.rm_compute_scoring_create;
    _ = compute_scoring_abi.rm_compute_scoring_destroy;
    _ = compute_scoring_abi.rm_compute_scoring_query_capacity;
    _ = compute_scoring_abi.rm_compute_scoring_score_with_scratch;
    _ = compute_scoring_abi.rm_compute_scoring_weighted_cpu_use;
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
    _ = smart_coordinator_abi.rm_smart_coordinator_query_workspace_capacity;
    _ = smart_coordinator_abi.rm_smart_coordinator_query_scratch_layout;
    _ = smart_coordinator_abi.rm_smart_coordinator_query_measure_table;
    _ = smart_coordinator_abi.rm_smart_coordinator_reconfigure;
    _ = smart_coordinator_abi.rm_smart_coordinator_reset;
    _ = smart_coordinator_abi.rm_smart_coordinator_measure_plan;
    _ = smart_coordinator_abi.rm_smart_coordinator_plan_measured;
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
    _ = sampling_subscription_abi.rm_sampling_subscription_query_workspace_capacity;
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
    _ = metric_snapshot_abi.rm_metric_snapshot_synchronize_counter_demand;
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
    _ = metric_snapshot_abi.rm_metric_snapshot_commit_current;
    _ = metric_snapshot_abi.rm_metric_snapshot_read_current;
    _ = metric_snapshot_abi.rm_metric_snapshot_read_current_selected;
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
    _ = portable_software_registry_abi.rm_software_identity_portable_registry_query_workspace_rows;
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
    _ = report_coordinator_abi.rm_report_coordinator_query_workspace_capacity;
    _ = report_coordinator_abi.rm_report_coordinator_replace_rules;
    _ = report_coordinator_abi.rm_report_coordinator_import;
    _ = report_coordinator_abi.rm_report_coordinator_observe;
    _ = report_coordinator_abi.rm_report_coordinator_query_observation_count;
    _ = report_coordinator_abi.rm_report_coordinator_reserve_observations;
    _ = report_coordinator_abi.rm_report_coordinator_command_trust;
    _ = report_coordinator_abi.rm_report_coordinator_clear_history;
    _ = report_coordinator_abi.rm_report_coordinator_query_plan_size;
    _ = report_coordinator_abi.rm_report_coordinator_plan;
    _ = report_coordinator_abi.rm_report_coordinator_apply_feedback;
    _ = file_query_abi.rm_file_query_abi_version;
    _ = file_query_abi.rm_file_query_create;
    _ = file_query_abi.rm_file_query_destroy;
    _ = file_query_abi.rm_file_query_scratch_byte_count;
    _ = file_query_abi.rm_file_query_bind_scratch;
    _ = file_query_abi.rm_file_query_release_scratch;
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
    _ = public_service_coordinator_abi.rm_public_service_coordinator_query_model_scratch;
    _ = public_service_coordinator_abi.rm_public_service_coordinator_bind_model_scratch;
    _ = public_service_coordinator_abi.rm_public_service_coordinator_unbind_model_scratch;
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
    _ = operation_coordinator_abi.rm_operation_coordinator_query_workspace_capacity;
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
    _ = transaction_journal_abi.rm_transaction_journal_query_workspace_capacity;
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
    _ = applied_ownership_abi.rm_applied_ownership_query_workspace_capacity;
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

// External collectors never allocate ordinary parser backing buffers.
pub export fn rm_pdh_collector_create_external_scratch(
    config: ?*const pdh.Config,
    handle: ?*?*anyopaque,
) callconv(.c) i32 {
    const result = rm_pdh_collector_create(config, handle);
    if (result != code(.ok)) return result;
    const collector: *pdh.Collector = @ptrCast(@alignCast(handle.?.*.?));
    collector.external_scratch = true;
    return result;
}

pub export fn rm_pdh_collector_sample_external_scratch(
    handle: ?*anyopaque,
    header: ?*pdh.FrameHeader,
    provider: ?*const pdh.ExternalScratch.Provider,
) callconv(.c) i32 {
    const opaque_handle = handle orelse return code(.invalid_argument);
    const output = header orelse return code(.invalid_argument);
    const source = provider orelse return code(.invalid_argument);
    if (output.abi_version != pdh.abi_version or output.struct_size != @sizeOf(pdh.FrameHeader))
        return code(.abi_mismatch);
    const collector: *pdh.Collector = @ptrCast(@alignCast(opaque_handle));
    return code(collector.sampleExternal(output, source.*));
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

pub export fn rm_pdh_collector_copy_gpu_engine_windows(handle: ?*anyopaque, expected_sequence: u64, rows: ?[*]pdh.GpuEngineWindow, capacity: u32, row_size: u32, count: ?*u32) callconv(.c) i32 {
    const collector: *pdh.Collector = @ptrCast(@alignCast(handle orelse return code(.invalid_argument)));
    return code(collector.copyEngineWindows(expected_sequence, rows, capacity, row_size, count orelse return code(.invalid_argument)));
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
    return @backingInt(result);
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
    try std.testing.expectEqual(@as(usize, 32), @sizeOf(pdh.GpuEngineRow));
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
    _ = public_service_coordinator;
    _ = display_coordinator;
    _ = operation_coordinator;
}
const abi_wired = @import("process_fields/abi.zig");
comptime {
    _ = abi_wired.rm_process_facts_inventory_visit;
    _ = abi_wired.rm_process_facts_image_path_read;
}
const process_read_access_abi_wired = @import("platform/windows/process_read_access_abi.zig");
comptime {
    _ = process_read_access_abi_wired.rm_process_read_access_version;
    _ = process_read_access_abi_wired.rm_process_read_acquire;
    _ = process_read_access_abi_wired.rm_process_read_query;
    _ = process_read_access_abi_wired.rm_process_read_close;
}
const desktop_publish_abi_wired = @import("interaction/http/desktop_publish_abi.zig");
comptime {
    _ = desktop_publish_abi_wired.rm_desktop_publisher_begin;
    _ = desktop_publish_abi_wired.rm_desktop_publisher_read;
    _ = desktop_publish_abi_wired.rm_desktop_publisher_end;
}
const interactive_session_facts_abi_wired = @import("platform/windows/interactive_session_facts_abi.zig");
comptime {
    _ = interactive_session_facts_abi_wired.rm_interactive_session_facts_abi_version;
    _ = interactive_session_facts_abi_wired.rm_interactive_session_facts_user_sid;
    _ = interactive_session_facts_abi_wired.rm_interactive_session_facts_active_ids;
}
const exports_wired = @import("desktop_observation/exports.zig");
comptime {
    _ = exports_wired.rm_desktop_observation_publish;
    _ = exports_wired.rm_desktop_observation_end;
    _ = exports_wired.rm_desktop_observation_read;
}
const publication_abi_wired = @import("monitoring/sampling_subscription/publication_abi.zig");
comptime {
    _ = publication_abi_wired.rm_current_publication_abi_version;
    _ = publication_abi_wired.rm_current_publication_signal_size;
    _ = publication_abi_wired.rm_current_publication_signal_alignment;
    _ = publication_abi_wired.rm_current_publication_subscription_size;
    _ = publication_abi_wired.rm_current_publication_subscription_alignment;
    _ = publication_abi_wired.rm_current_publication_signal_init;
    _ = publication_abi_wired.rm_current_publication_subscribe;
    _ = publication_abi_wired.rm_current_publication_unsubscribe;
    _ = publication_abi_wired.rm_current_publication_poll;
    _ = publication_abi_wired.rm_current_publication_publish;
    _ = publication_abi_wired.rm_current_publication_end;
    _ = publication_abi_wired.rm_current_publication_signal_deinit;
}
const host_abi_wired = @import("interaction/http/host_abi.zig");
comptime {
    _ = host_abi_wired.rm_http_common_family_requirement;
    _ = host_abi_wired.rm_http_common_owns;
    _ = host_abi_wired.rm_http_common_output_bound;
    _ = host_abi_wired.rm_http_common_dispatch;
    _ = host_abi_wired.rm_http_common_family_owns;
    _ = host_abi_wired.rm_http_common_family_capacity;
    _ = host_abi_wired.rm_http_common_family_init;
    _ = host_abi_wired.rm_http_common_family_admit;
    _ = host_abi_wired.rm_http_common_family_start;
    _ = host_abi_wired.rm_http_common_family_step;
    _ = host_abi_wired.rm_http_common_family_release;
}
const notifications_http = @import("interaction/http/notifications.zig");
const kernel_history = @import("interaction/capabilities/kernel_history.zig");
const kernel_history_http = @import("interaction/http/kernel_history.zig");
const runtime_http = @import("interaction/http/runtime_abi.zig");
comptime {
    _ = notifications_http.rm_http_notifications_borrow;
    _ = kernel_history.rm_kernel_history_invoke;
    _ = kernel_history.rm_kernel_history_clear;
    _ = kernel_history_http.rm_http_kernel_history_borrow;
    _ = runtime_http.rm_http_runtime_borrow;
    _ = runtime_http.rm_http_runtime_path;
    _ = runtime_http.rm_http_runtime_minimum_window;
}
const foundation_abi_wired = @import("host/foundation_abi.zig");
comptime {
    _ = foundation_abi_wired.rm_foundation_clock;
    _ = foundation_abi_wired.rm_foundation_identity;
    _ = foundation_abi_wired.rm_foundation_log_rotate;
    _ = foundation_abi_wired.rm_foundation_log_append;
}
