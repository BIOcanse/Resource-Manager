using System.Text.Json.Serialization;
using ResourceManager.App.Domain.RuntimeSpecialization.FreedomPoints;
using ResourceManager.App.Domain.Software;

namespace ResourceManager.App.Domain.RuntimeSpecialization;

/// <summary>
/// Build-time optimization choices: rollback and retirement stores, the ideal-processor workspace, Normal-mode
/// restoration, validation scopes, smart-record history paging, base scores by software kind, CPU core class
/// scores and the optimization report cadence and rule limits (freedom-point tables scheduling, scoring,
/// scoring/cpu and reports).
/// </summary>
public sealed record CompiledOptimizationSettings(
    [property: global::FreedomPoint.FreedomPointBinding(BackendFreedomPointPositions.RollbackStateStore)]
    RollbackStateStoreOptions RollbackStateStore,
    [property: global::FreedomPoint.FreedomPointBinding(BackendFreedomPointPositions.AuthorityRetirement)]
    AuthorityRetirementOptions AuthorityRetirement,
    [property: global::FreedomPoint.FreedomPointBinding(BackendFreedomPointPositions.IdealProcessorWorkspace)]
    IdealProcessorWorkspaceOptions IdealProcessorWorkspace,
    [property: global::FreedomPoint.FreedomPointBinding(BackendFreedomPointPositions.NormalRestoration)]
    NormalRestorationOptions NormalRestoration,
    [property: global::FreedomPoint.FreedomPointBinding(BackendFreedomPointPositions.EffectValidationLease)]
    EffectValidationLeaseOptions EffectValidationLease,
    [property: global::FreedomPoint.FreedomPointBinding(BackendFreedomPointPositions.MemoryCleanupValidationEvidence)]
    MemoryCleanupValidationEvidenceOptions MemoryCleanupValidationEvidence,
    [property: global::FreedomPoint.FreedomPointBinding(BackendFreedomPointPositions.SmartRecordHistory)]
    SmartRecordHistoryOptions SmartRecordHistory,
    [property: global::FreedomPoint.FreedomPointBinding(BackendFreedomPointPositions.SoftwareKindBaseScores)]
    SoftwareKindBaseScores SoftwareKindBaseScores,
    [property: global::FreedomPoint.FreedomPointBinding(BackendFreedomPointPositions.CoreClassScores)]
    CpuCoreClassScores CoreClassScores,
    [property: global::FreedomPoint.FreedomPointBinding(BackendFreedomPointPositions.ReportSampleIntervalMs)]
    TimeSpan ReportSampleInterval,
    [property: global::FreedomPoint.FreedomPointBinding(BackendFreedomPointPositions.ReportRuleLimits)]
    ReportRuleLimits ReportRuleLimits,
    [property: global::FreedomPoint.FreedomPointBinding(BackendFreedomPointPositions.OptimizationStorageLimits)]
    OptimizationStorageLimits StorageLimits,
    [property: global::FreedomPoint.FreedomPointBinding(BackendFreedomPointPositions.LegacyAffinity)]
    LegacyAffinityOptions LegacyAffinity,
    [property: global::FreedomPoint.FreedomPointBinding(BackendFreedomPointPositions.OptimizationRuntimeLimits)]
    OptimizationRuntimeLimits RuntimeLimits);

public sealed record OptimizationRuntimeLimits(
    [property: JsonPropertyName("maximum_base_importance")] double MaximumBaseImportance,
    [property: JsonPropertyName("stack_input_character_limit")] int StackInputCharacterLimit,
    [property: JsonPropertyName("stack_utf8_byte_limit")] int StackUtf8ByteLimit,
    [property: JsonPropertyName("startup_catalog_subscription_ms")] int StartupCatalogSubscriptionMilliseconds,
    [property: JsonPropertyName("format_large_value_threshold")] double FormatLargeValueThreshold,
    [property: JsonPropertyName("format_byte_digits")] int FormatByteDigits,
    [property: JsonPropertyName("format_large_value_digits")] int FormatLargeValueDigits,
    [property: JsonPropertyName("format_small_value_digits")] int FormatSmallValueDigits)
{
    public OptimizationRuntimeLimits Validate()
    {
        if (!double.IsFinite(MaximumBaseImportance) || MaximumBaseImportance <= 0
            || StackInputCharacterLimit < 0 || StackUtf8ByteLimit < 0 || StartupCatalogSubscriptionMilliseconds < 1
            || !double.IsFinite(FormatLargeValueThreshold) || FormatLargeValueThreshold < 0
            || FormatByteDigits < 0 || FormatLargeValueDigits < 0 || FormatSmallValueDigits < 0)
            throw new InvalidDataException("Invalid optimization runtime limits.");
        return this;
    }
}

public sealed record OptimizationStorageLimits(
    [property: JsonPropertyName("io_buffer_bytes")] int IoBufferBytes,
    [property: JsonPropertyName("maximum_json_depth")] int MaximumJsonDepth,
    [property: JsonPropertyName("retirement_maximum_image_bytes")] int RetirementMaximumImageBytes,
    [property: JsonPropertyName("retirement_maximum_relative_path_bytes")] int RetirementMaximumRelativePathBytes)
{
    public OptimizationStorageLimits Validate()
    {
        if (IoBufferBytes < 1 || MaximumJsonDepth < 1 || RetirementMaximumImageBytes < 1
            || RetirementMaximumRelativePathBytes < 1)
            throw new InvalidDataException("Invalid optimization storage limits.");
        return this;
    }
}

public sealed record LegacyAffinityOptions(
    [property: JsonPropertyName("minimum_logical_processors")] int MinimumLogicalProcessors,
    [property: JsonPropertyName("minimum_primary_processors")] int MinimumPrimaryProcessors,
    [property: JsonPropertyName("small_machine_maximum_processors")] int SmallMachineMaximumProcessors,
    [property: JsonPropertyName("small_machine_reserved_processors")] int SmallMachineReservedProcessors,
    [property: JsonPropertyName("medium_machine_maximum_processors")] int MediumMachineMaximumProcessors,
    [property: JsonPropertyName("medium_machine_reserved_processors")] int MediumMachineReservedProcessors,
    [property: JsonPropertyName("large_machine_minimum_reserved_processors")] int LargeMachineMinimumReservedProcessors,
    [property: JsonPropertyName("large_machine_reservation_divisor")] int LargeMachineReservationDivisor)
{
    public LegacyAffinityOptions Validate()
    {
        if (MinimumLogicalProcessors < 1 || MinimumPrimaryProcessors < 1 || SmallMachineMaximumProcessors < 1
            || SmallMachineReservedProcessors < 1 || MediumMachineMaximumProcessors < SmallMachineMaximumProcessors
            || MediumMachineReservedProcessors < 1 || LargeMachineMinimumReservedProcessors < 1
            || LargeMachineReservationDivisor < 1 || SmallMachineReservedProcessors > MinimumLogicalProcessors
            || MediumMachineReservedProcessors > SmallMachineMaximumProcessors
            || LargeMachineMinimumReservedProcessors > MediumMachineMaximumProcessors)
            throw new InvalidDataException("Invalid legacy affinity options.");
        return this;
    }
}

/// <summary>Rollback state store: how often a changed state is checkpointed to disk, and the largest canonical state it writes or reads.</summary>
public sealed record RollbackStateStoreOptions(
    [property: JsonPropertyName("checkpoint_interval_ms")] int CheckpointIntervalMilliseconds,
    [property: JsonPropertyName("maximum_canonical_bytes")] int MaximumCanonicalBytes)
{
    public RollbackStateStoreOptions Validate()
    {
        if (CheckpointIntervalMilliseconds < 1 || MaximumCanonicalBytes < 1024)
            throw new InvalidDataException("Invalid rollback state store options.");
        return this;
    }
}

/// <summary>Authority retirement: how many retirements, orphan and canonical temporary files and payload entries and bytes one generation directory may hold.</summary>
public sealed record AuthorityRetirementOptions(
    [property: JsonPropertyName("maximum_pending_retirements")] int MaximumPendingRetirements,
    [property: JsonPropertyName("maximum_orphan_temporary_files")] int MaximumOrphanTemporaryFiles,
    [property: JsonPropertyName("maximum_canonical_temporary_files")] int MaximumCanonicalTemporaryFiles,
    [property: JsonPropertyName("maximum_payload_entries")] int MaximumPayloadEntries,
    [property: JsonPropertyName("maximum_payload_inventory_bytes")] long MaximumPayloadInventoryBytes)
{
    /// <summary>The owner lease file plus every pending retirement and orphan temporary file.</summary>
    [JsonIgnore]
    public int MaximumRetirementDirectoryEntries => 1 + MaximumPendingRetirements + MaximumOrphanTemporaryFiles;

    public AuthorityRetirementOptions Validate()
    {
        if (MaximumPendingRetirements < 1 || MaximumOrphanTemporaryFiles < 0 || MaximumCanonicalTemporaryFiles < 0
            || MaximumPayloadEntries < 1 || MaximumPayloadInventoryBytes < 1
            || (long)MaximumPendingRetirements + MaximumOrphanTemporaryFiles >= int.MaxValue)
            throw new InvalidDataException("Invalid authority retirement options.");
        return this;
    }
}

/// <summary>
/// Ideal-processor workspace trim: a workspace whose capacity exceeds trim_minimum_capacity and whose live rows stay at
/// or below capacity / trim_usage_divisor for low_usage_calls_before_trim completed calls is trimmed to
/// max(trimmed_minimum_capacity, live rows * trimmed_headroom_factor).
/// </summary>
public sealed record IdealProcessorWorkspaceOptions(
    [property: JsonPropertyName("low_usage_calls_before_trim")] int LowUsageCallsBeforeTrim,
    [property: JsonPropertyName("trim_minimum_capacity")] int TrimMinimumCapacity,
    [property: JsonPropertyName("trim_usage_divisor")] int TrimUsageDivisor,
    [property: JsonPropertyName("trimmed_minimum_capacity")] int TrimmedMinimumCapacity,
    [property: JsonPropertyName("trimmed_headroom_factor")] int TrimmedHeadroomFactor)
{
    public IdealProcessorWorkspaceOptions Validate()
    {
        if (LowUsageCallsBeforeTrim < 1 || TrimMinimumCapacity < 0 || TrimUsageDivisor < 1
            || TrimmedMinimumCapacity < 0 || TrimmedHeadroomFactor < 1)
            throw new InvalidDataException("Invalid ideal-processor workspace options.");
        return this;
    }
}

/// <summary>Normal-mode restoration when Smart stops: the time limit and the action and feedback budget it restores with.</summary>
public sealed record NormalRestorationOptions(
    [property: JsonPropertyName("timeout_ms")] int TimeoutMilliseconds,
    [property: JsonPropertyName("maximum_actions")] uint MaximumActions)
{
    public NormalRestorationOptions Validate()
    {
        if (TimeoutMilliseconds < 1 || MaximumActions < 1)
            throw new InvalidDataException("Invalid Normal-mode restoration options.");
        return this;
    }
}

/// <summary>Process-effect validation scope: the shortest and longest lease a scope may request.</summary>
public sealed record EffectValidationLeaseOptions(
    [property: JsonPropertyName("minimum_ms")] long MinimumMilliseconds,
    [property: JsonPropertyName("maximum_ms")] long MaximumMilliseconds)
{
    [JsonPropertyName("minimum_release_token_characters")]
    public int MinimumReleaseTokenCharacters { get; init; }
    [JsonPropertyName("maximum_release_token_characters")]
    public int MaximumReleaseTokenCharacters { get; init; }

    public EffectValidationLeaseOptions Validate()
    {
        if (MinimumMilliseconds < 1 || MaximumMilliseconds < MinimumMilliseconds
            || MinimumReleaseTokenCharacters < 1 || MaximumReleaseTokenCharacters < MinimumReleaseTokenCharacters)
            throw new InvalidDataException("Invalid effect validation lease options.");
        return this;
    }
}

/// <summary>Memory-cleanup validation evidence: pending batches and actions per batch the evidence ledger accepts.</summary>
public sealed record MemoryCleanupValidationEvidenceOptions(
    [property: JsonPropertyName("maximum_batch_count")] int MaximumBatchCount,
    [property: JsonPropertyName("maximum_action_count")] int MaximumActionCount)
{
    public MemoryCleanupValidationEvidenceOptions Validate()
    {
        if (MaximumBatchCount < 1 || MaximumActionCount < 1)
            throw new InvalidDataException("Invalid memory-cleanup validation evidence options.");
        return this;
    }
}

/// <summary>Smart decision-record history: the most rows one page request may ask for.</summary>
public sealed record SmartRecordHistoryOptions(
    [property: JsonPropertyName("maximum_page_rows")] int MaximumPageRows)
{
    public SmartRecordHistoryOptions Validate()
    {
        if (MaximumPageRows < 1) throw new InvalidDataException("Invalid smart record history options.");
        return this;
    }
}

/// <summary>Type-default base score of each software kind; other is every kind not listed.</summary>
public sealed record SoftwareKindBaseScores(
    [property: JsonPropertyName("game")] double Game,
    [property: JsonPropertyName("high_performance")] double HighPerformance,
    [property: JsonPropertyName("adapted")] double Adapted,
    [property: JsonPropertyName("windows_system")] double WindowsSystem,
    [property: JsonPropertyName("windows_component")] double WindowsComponent,
    [property: JsonPropertyName("windows_service")] double WindowsService,
    [property: JsonPropertyName("controlled")] double Controlled,
    [property: JsonPropertyName("managed")] double Managed,
    [property: JsonPropertyName("unattributed")] double Unattributed,
    [property: JsonPropertyName("other")] double Other)
{
    public double ForKind(string kind) => kind switch
    {
        SoftwareKinds.Game => Game,
        SoftwareKinds.HighPerformance => HighPerformance,
        SoftwareKinds.Adapted => Adapted,
        SoftwareKinds.WindowsSystem => WindowsSystem,
        SoftwareKinds.WindowsComponent => WindowsComponent,
        SoftwareKinds.WindowsService => WindowsService,
        SoftwareKinds.Controlled => Controlled,
        SoftwareKinds.Managed => Managed,
        SoftwareKinds.Unattributed => Unattributed,
        _ => Other
    };

    public SoftwareKindBaseScores Validate()
    {
        double[] scores = [Game, HighPerformance, Adapted, WindowsSystem, WindowsComponent, WindowsService, Controlled, Managed, Unattributed, Other];
        if (scores.Any(static score => !double.IsFinite(score) || score < 0 || score > 100))
            throw new InvalidDataException("Software kind base scores must be finite values from 0 to 100.");
        return this;
    }
}

/// <summary>Relative capacity score of each CPU core class used by the model presets (performance core = reference).</summary>
public sealed record CpuCoreClassScores(
    [property: JsonPropertyName("performance")] double Performance,
    [property: JsonPropertyName("efficient")] double Efficient,
    [property: JsonPropertyName("low_power_efficient")] double LowPowerEfficient,
    [property: JsonPropertyName("amd_compact")] double AmdCompact)
{
    public CpuCoreClassScores Validate()
    {
        double[] scores = [Performance, Efficient, LowPowerEfficient, AmdCompact];
        if (scores.Any(static score => !double.IsFinite(score) || score <= 0))
            throw new InvalidDataException("CPU core class scores must be positive finite values.");
        return this;
    }
}

/// <summary>User report-rule limits: the largest required consecutive hit or miss count, the highest zero-activity threshold and the executable names one rule may list.</summary>
public sealed record ReportRuleLimits(
    [property: JsonPropertyName("maximum_consecutive_count")] uint MaximumConsecutiveCount,
    [property: JsonPropertyName("maximum_zero_activity_percent")] double MaximumZeroActivityPercent,
    [property: JsonPropertyName("maximum_executable_names")] int MaximumExecutableNames)
{
    [JsonPropertyName("maximum_executable_name_characters")]
    public int MaximumExecutableNameCharacters { get; init; }
    [JsonPropertyName("threshold_ranges")]
    public Dictionary<string, ReportRuleThresholdRange> ThresholdRanges { get; init; } = [];

    public ReportRuleLimits Validate()
    {
        if (MaximumConsecutiveCount < 1 || !double.IsFinite(MaximumZeroActivityPercent) || MaximumZeroActivityPercent <= 0
            || MaximumZeroActivityPercent > 100 || MaximumExecutableNames < 1 || MaximumExecutableNameCharacters < 1)
            throw new InvalidDataException("Invalid report rule limits.");
        foreach (var range in ThresholdRanges.Values) range.Validate();
        return this;
    }
}

public sealed record ReportRuleThresholdRange(
    [property: JsonPropertyName("unit")] string Unit,
    [property: JsonPropertyName("scale")] double Scale,
    [property: JsonPropertyName("step")] double Step,
    [property: JsonPropertyName("minimum")] double Minimum,
    [property: JsonPropertyName("maximum")] double Maximum)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Unit) || !double.IsFinite(Scale) || Scale <= 0
            || !double.IsFinite(Step) || Step <= 0 || !double.IsFinite(Minimum)
            || !double.IsFinite(Maximum) || Minimum > Maximum)
            throw new InvalidDataException("Invalid report rule threshold range.");
    }
}

/// <summary>Build-time GPU placement choices: process history, external GPU runtime timing, injection retries and launch reports.</summary>
public sealed record CompiledGpuPlacementSettings(
    [property: global::FreedomPoint.FreedomPointBinding(BackendFreedomPointPositions.ProcessHistory)]
    GpuPlacementProcessHistoryOptions ProcessHistory,
    [property: global::FreedomPoint.FreedomPointBinding(BackendFreedomPointPositions.ExternalGpuRuntime)]
    ExternalGpuRuntimeOptions ExternalRuntime,
    [property: global::FreedomPoint.FreedomPointBinding(BackendFreedomPointPositions.GpuInjection)]
    GpuInjectionOptions Injection,
    [property: global::FreedomPoint.FreedomPointBinding(BackendFreedomPointPositions.GpuLaunchReports)]
    GpuLaunchReportOptions LaunchReports);

/// <summary>
/// GPU placement process history: processes kept per software, how often an unchanged last-seen time is persisted,
/// the observation window of one attempt, the cleanup limit when observation stops, and the confidence of an observed
/// process (without and with an executable path, plus the bonus when more than one evidence source agrees, capped at 100).
/// </summary>
public sealed record GpuPlacementProcessHistoryOptions(
    [property: JsonPropertyName("maximum_processes_per_software")] int MaximumProcessesPerSoftware,
    [property: JsonPropertyName("last_seen_persist_interval_ms")] long LastSeenPersistIntervalMilliseconds,
    [property: JsonPropertyName("observation_window_ms")] int ObservationWindowMilliseconds,
    [property: JsonPropertyName("stop_cleanup_timeout_ms")] int StopCleanupTimeoutMilliseconds,
    [property: JsonPropertyName("confidence_without_path")] byte ConfidenceWithoutPath,
    [property: JsonPropertyName("confidence_with_path")] byte ConfidenceWithPath,
    [property: JsonPropertyName("confidence_corroboration_bonus")] byte ConfidenceCorroborationBonus)
{
    public GpuPlacementProcessHistoryOptions Validate()
    {
        if (MaximumProcessesPerSoftware < 1 || LastSeenPersistIntervalMilliseconds < 0 || ObservationWindowMilliseconds < 1
            || StopCleanupTimeoutMilliseconds < 1 || ConfidenceWithoutPath > 100 || ConfidenceWithPath > 100
            || ConfidenceCorroborationBonus > 100)
            throw new InvalidDataException("Invalid GPU placement process history options.");
        return this;
    }
}

/// <summary>
/// External GPU controller runtime: recovery and completion waits, the controller pipe connect limit, the controller
/// file poll period, the verified Qt presents that confirm a placement, the retained diagnostic summaries, and the
/// resident observation refresh and its publication poll period.
/// </summary>
public sealed record ExternalGpuRuntimeOptions(
    [property: JsonPropertyName("recovery_timeout_ms")] int RecoveryTimeoutMilliseconds,
    [property: JsonPropertyName("completion_wait_ms")] int CompletionWaitMilliseconds,
    [property: JsonPropertyName("controller_connect_timeout_ms")] int ControllerConnectTimeoutMilliseconds,
    [property: JsonPropertyName("controller_poll_ms")] int ControllerPollMilliseconds,
    [property: JsonPropertyName("qt_minimum_verified_presents")] int QtMinimumVerifiedPresents,
    [property: JsonPropertyName("diagnostic_summary_limit")] int DiagnosticSummaryLimit,
    [property: JsonPropertyName("resident_refresh_ms")] int ResidentRefreshMilliseconds,
    [property: JsonPropertyName("resident_poll_ms")] int ResidentPollMilliseconds)
{
    public ExternalGpuRuntimeOptions Validate()
    {
        if (RecoveryTimeoutMilliseconds < 1 || CompletionWaitMilliseconds < 1 || ControllerConnectTimeoutMilliseconds < 1
            || ControllerPollMilliseconds < 1 || QtMinimumVerifiedPresents < 1 || DiagnosticSummaryLimit < 1
            || ResidentRefreshMilliseconds < 1 || ResidentPollMilliseconds < 1)
            throw new InvalidDataException("Invalid external GPU runtime options.");
        return this;
    }
}

/// <summary>GPU placement injection: attempts and wait between attempts to find a loaded module, and module snapshot attempts.</summary>
public sealed record GpuInjectionOptions(
    [property: JsonPropertyName("module_find_attempts")] int ModuleFindAttempts,
    [property: JsonPropertyName("module_find_retry_ms")] int ModuleFindRetryMilliseconds,
    [property: JsonPropertyName("module_snapshot_attempts")] int ModuleSnapshotAttempts)
{
    [JsonPropertyName("maximum_renderer_qualification_rows")]
    public int MaximumRendererQualificationRows { get; init; }

    public GpuInjectionOptions Validate()
    {
        if (ModuleFindAttempts < 1 || ModuleFindRetryMilliseconds < 0 || ModuleSnapshotAttempts < 1
            || MaximumRendererQualificationRows < 1)
            throw new InvalidDataException("Invalid GPU injection options.");
        return this;
    }
}

/// <summary>GPU launch interception: execution reports kept.</summary>
public sealed record GpuLaunchReportOptions(
    [property: JsonPropertyName("maximum_reports")] int MaximumReports)
{
    public GpuLaunchReportOptions Validate()
    {
        if (MaximumReports < 1) throw new InvalidDataException("Invalid GPU launch report options.");
        return this;
    }
}

/// <summary>Build-time persistence choices of the service's own stores.</summary>
public sealed record CompiledPersistenceSettings(
    [property: global::FreedomPoint.FreedomPointBinding(BackendFreedomPointPositions.SqliteVacuum)]
    SqliteVacuumOptions SqliteVacuum);

/// <summary>SQLite maintenance: a pending VACUUM runs only on a database of at least minimum_database_bytes with at least minimum_free_page_ratio free pages.</summary>
public sealed record SqliteVacuumOptions(
    [property: JsonPropertyName("minimum_database_bytes")] long MinimumDatabaseBytes,
    [property: JsonPropertyName("minimum_free_page_ratio")] double MinimumFreePageRatio)
{
    public SqliteVacuumOptions Validate()
    {
        if (MinimumDatabaseBytes < 0 || !double.IsFinite(MinimumFreePageRatio) || MinimumFreePageRatio < 0 || MinimumFreePageRatio > 1)
            throw new InvalidDataException("Invalid SQLite vacuum options.");
        return this;
    }
}
