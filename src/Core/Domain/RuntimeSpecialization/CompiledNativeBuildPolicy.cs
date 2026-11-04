using System.Text.Json.Serialization;

namespace ResourceManager.App.Domain.RuntimeSpecialization;

/// <summary>Build-selected native capacities, growth policies and runtime-state ordering; also projected to Zig by the SDK generator.</summary>
public sealed record CompiledNativeBuildPolicy
{
    [JsonRequired, JsonPropertyName("data_area_borrow_drain_timeout_ms")] public uint DataAreaBorrowDrainTimeoutMilliseconds { get; init; }
    [JsonRequired, JsonPropertyName("initial_record_capacity")] public uint InitialRecordCapacity { get; init; }
    [JsonRequired, JsonPropertyName("initial_operation_capacity")] public uint InitialOperationCapacity { get; init; }
    [JsonRequired, JsonPropertyName("display_initial_text_bytes")] public uint DisplayInitialTextBytes { get; init; }
    [JsonRequired, JsonPropertyName("display_initial_diff_entries")] public uint DisplayInitialDiffEntries { get; init; }
    [JsonRequired, JsonPropertyName("adapter_lease_maximum_capacity")] public uint AdapterLeaseMaximumCapacity { get; init; }
    [JsonRequired, JsonPropertyName("adapter_lease_id_attempts")] public uint AdapterLeaseIdAttempts { get; init; }
    [JsonRequired, JsonPropertyName("maximum_network_interfaces")] public uint MaximumNetworkInterfaces { get; init; }
    [JsonRequired, JsonPropertyName("maximum_gpu_engine_rows")] public uint MaximumGpuEngineRows { get; init; }
    [JsonRequired, JsonPropertyName("calibration_maximum_corpus_bytes")] public uint CalibrationMaximumCorpusBytes { get; init; }
    [JsonRequired, JsonPropertyName("calibration_maximum_row_bytes")] public uint CalibrationMaximumRowBytes { get; init; }
    [JsonRequired, JsonPropertyName("selected_source_limit")] public uint SelectedSourceLimit { get; init; }
    [JsonRequired, JsonPropertyName("shared_read_retry_limit")] public uint SharedReadRetryLimit { get; init; }
    [JsonRequired, JsonPropertyName("interrupt_initial_capacity")] public uint InterruptInitialCapacity { get; init; }
    [JsonRequired, JsonPropertyName("interrupt_growth_factor")] public uint InterruptGrowthFactor { get; init; }
    [JsonRequired, JsonPropertyName("etw_allocator_cap_divisor")] public uint EtwAllocatorCapDivisor { get; init; }
    [JsonRequired, JsonPropertyName("etw_allocator_requested_divisor")] public uint EtwAllocatorRequestedDivisor { get; init; }
    [JsonRequired, JsonPropertyName("etw_allocator_alignment_divisor")] public uint EtwAllocatorAlignmentDivisor { get; init; }
    [JsonRequired, JsonPropertyName("etw_initial_pending_path_units")] public uint EtwInitialPendingPathUnits { get; init; }
    [JsonRequired, JsonPropertyName("diagnostic_minimum_inventory_bytes")] public uint DiagnosticMinimumInventoryBytes { get; init; }
    [JsonRequired, JsonPropertyName("residency_ring_chunk_bytes")] public uint ResidencyRingChunkBytes { get; init; }
    [JsonRequired, JsonPropertyName("query_dos_device_units")] public uint QueryDosDeviceUnits { get; init; }
    [JsonRequired, JsonPropertyName("runtime_rank_focused")] public uint RuntimeRankFocused { get; init; }
    [JsonRequired, JsonPropertyName("runtime_rank_unfocused")] public uint RuntimeRankUnfocused { get; init; }
    [JsonRequired, JsonPropertyName("runtime_rank_background_window")] public uint RuntimeRankBackgroundWindow { get; init; }
    [JsonRequired, JsonPropertyName("runtime_rank_tray")] public uint RuntimeRankTray { get; init; }
    [JsonRequired, JsonPropertyName("runtime_rank_background_process")] public uint RuntimeRankBackgroundProcess { get; init; }
    [JsonRequired, JsonPropertyName("runtime_rank_not_running")] public uint RuntimeRankNotRunning { get; init; }
    [JsonRequired, JsonPropertyName("system_events_enabled")] public bool SystemEventsEnabled { get; init; }
    [JsonRequired, JsonPropertyName("system_events_history_after_ms")] public uint SystemEventsHistoryAfterMilliseconds { get; init; }
    [JsonRequired, JsonPropertyName("system_events_event_tail_capacity")] public uint SystemEventsEventTailCapacity { get; init; }
    [JsonRequired, JsonPropertyName("system_events_entry_capacity")] public uint SystemEventsEntryCapacity { get; init; }
    [JsonRequired, JsonPropertyName("system_events_source_event_xml_bytes")] public uint SystemEventsSourceEventXmlBytes { get; init; }
    [JsonRequired, JsonPropertyName("system_events_source_bookmark_xml_bytes")] public uint SystemEventsSourceBookmarkXmlBytes { get; init; }
    [JsonRequired, JsonPropertyName("system_events_source_xpath_units")] public uint SystemEventsSourceXPathUnits { get; init; }
    [JsonRequired, JsonPropertyName("system_events_source_thread_stack_bytes")] public uint SystemEventsSourceThreadStackBytes { get; init; }
    [JsonRequired, JsonPropertyName("freeze_recorder_enabled")] public bool FreezeRecorderEnabled { get; init; }
    [JsonRequired, JsonPropertyName("freeze_recorder_interval_ms")] public uint FreezeRecorderIntervalMilliseconds { get; init; }
    [JsonRequired, JsonPropertyName("freeze_recorder_slots")] public uint FreezeRecorderSlots { get; init; }
    [JsonRequired, JsonPropertyName("freeze_recorder_review_window_ms")] public uint FreezeRecorderReviewWindowMilliseconds { get; init; }
    [JsonRequired, JsonPropertyName("freeze_recorder_physical_available_percent")] public uint FreezeRecorderPhysicalAvailablePercent { get; init; }
    [JsonRequired, JsonPropertyName("freeze_recorder_pool_physical_percent")] public uint FreezeRecorderPoolPhysicalPercent { get; init; }
    [JsonRequired, JsonPropertyName("freeze_recorder_storage_consecutive")] public uint FreezeRecorderStorageConsecutive { get; init; }
    [JsonRequired, JsonPropertyName("freeze_recorder_owner_memory_bytes")] public uint FreezeRecorderOwnerMemoryBytes { get; init; }
    [JsonRequired, JsonPropertyName("freeze_recorder_source_memory_bytes")] public uint FreezeRecorderSourceMemoryBytes { get; init; }
    [JsonRequired, JsonPropertyName("freeze_recorder_max_document_bytes")] public uint FreezeRecorderMaxDocumentBytes { get; init; }
    [JsonRequired, JsonPropertyName("freeze_recorder_process_input_capacity")] public uint FreezeRecorderProcessInputCapacity { get; init; }
    [JsonRequired, JsonPropertyName("freeze_recorder_thread_stack_bytes")] public uint FreezeRecorderThreadStackBytes { get; init; }
    [JsonRequired, JsonPropertyName("freeze_recorder_disk_capacity")] public uint FreezeRecorderDiskCapacity { get; init; }
    [JsonRequired, JsonPropertyName("freeze_recorder_process_capacity")] public uint FreezeRecorderProcessCapacity { get; init; }
    [JsonRequired, JsonPropertyName("freeze_recorder_image_name_capacity")] public uint FreezeRecorderImageNameCapacity { get; init; }

    public CompiledNativeBuildPolicy Validate()
    {
        uint[] positive = [InitialRecordCapacity, InitialOperationCapacity, DisplayInitialTextBytes,
            DisplayInitialDiffEntries, AdapterLeaseMaximumCapacity, AdapterLeaseIdAttempts, MaximumNetworkInterfaces,
            MaximumGpuEngineRows, CalibrationMaximumCorpusBytes, CalibrationMaximumRowBytes, SelectedSourceLimit,
            SharedReadRetryLimit, InterruptInitialCapacity, EtwAllocatorCapDivisor, EtwAllocatorRequestedDivisor,
            EtwAllocatorAlignmentDivisor, EtwInitialPendingPathUnits, DiagnosticMinimumInventoryBytes,
            ResidencyRingChunkBytes, QueryDosDeviceUnits,
            SystemEventsHistoryAfterMilliseconds, SystemEventsEventTailCapacity, SystemEventsEntryCapacity,
            SystemEventsSourceEventXmlBytes, SystemEventsSourceBookmarkXmlBytes, SystemEventsSourceXPathUnits,
            SystemEventsSourceThreadStackBytes, FreezeRecorderIntervalMilliseconds, FreezeRecorderSlots,
            FreezeRecorderReviewWindowMilliseconds, FreezeRecorderStorageConsecutive, FreezeRecorderOwnerMemoryBytes,
            FreezeRecorderSourceMemoryBytes, FreezeRecorderMaxDocumentBytes, FreezeRecorderProcessInputCapacity,
            FreezeRecorderThreadStackBytes, FreezeRecorderDiskCapacity, FreezeRecorderProcessCapacity, FreezeRecorderImageNameCapacity];
        if (positive.Any(static value => value == 0) || InterruptGrowthFactor < 2
            || ResidencyRingChunkBytes < 4 * sizeof(long)
            || FreezeRecorderPhysicalAvailablePercent > 100 || FreezeRecorderPoolPhysicalPercent > 100
            || FreezeRecorderDiskCapacity > byte.MaxValue || FreezeRecorderProcessCapacity > byte.MaxValue
            || FreezeRecorderImageNameCapacity > byte.MaxValue
            || new[] { RuntimeRankFocused, RuntimeRankUnfocused, RuntimeRankBackgroundWindow, RuntimeRankTray,
                RuntimeRankBackgroundProcess, RuntimeRankNotRunning }.Any(static rank => rank > int.MaxValue))
            throw new InvalidDataException("Invalid native build policy.");
        return this;
    }
}
