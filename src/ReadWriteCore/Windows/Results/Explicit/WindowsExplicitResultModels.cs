using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ResourceManager.ReadWrite.Windows.Results.Explicit;

/// <summary>Closed set of explicit result records supported by this contract.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<WindowsExplicitResultKind>))]
public enum WindowsExplicitResultKind
{
    MouseInputManual,
    KeyboardInputManual,
    DisplayAnimationManual,
    DisplayObservationManual,
    StoragePerformanceManual,
    CpuBenchmark,
    GpuBenchmark,
    CpuSelfTest,
    MemoryTestManual,
    StorageVerificationManual,
    MemoryHierarchyLatencyManual,
    ReferenceRanking
}

[JsonConverter(typeof(JsonStringEnumConverter<WindowsExplicitResultScope>))]
public enum WindowsExplicitResultScope
{
    RawInputDevice,
    DisplaySession,
    StorageDevice,
    Cpu,
    Gpu,
    Memory,
    ReferenceSubject
}

/// <summary>The fixed Information object group for one explicit result field.</summary>
public enum WindowsExplicitResultObjectKind
{
    ManualTest,
    TestResult,
    Reference
}

/// <summary>
/// Per-owner memory and import bounds. Defaults are visible at construction and may be
/// replaced by the caller; they are operational limits, not result-schema field limits.
/// </summary>
public sealed record WindowsExplicitResultLimits(
    int MaximumImportBytes = 64 * 1024,
    int MaximumSourceMetadataBytes = 16 * 1024,
    int MaximumInputSamples = 4096,
    int MaximumDisplayFrames = 8192,
    int MaximumJsonDepth = 12,
    int MaximumJsonNodeCount = 40_000)
{
    internal WindowsExplicitResultLimits Validate()
    {
        if (MaximumImportBytes <= 0) throw new ArgumentOutOfRangeException(nameof(MaximumImportBytes));
        if (MaximumSourceMetadataBytes <= 0) throw new ArgumentOutOfRangeException(nameof(MaximumSourceMetadataBytes));
        if (MaximumInputSamples <= 0) throw new ArgumentOutOfRangeException(nameof(MaximumInputSamples));
        if (MaximumDisplayFrames <= 0) throw new ArgumentOutOfRangeException(nameof(MaximumDisplayFrames));
        if (MaximumJsonDepth <= 0) throw new ArgumentOutOfRangeException(nameof(MaximumJsonDepth));
        if (MaximumJsonNodeCount <= 0) throw new ArgumentOutOfRangeException(nameof(MaximumJsonNodeCount));
        return this;
    }
}

[JsonConverter(typeof(JsonStringEnumConverter<WindowsExplicitInputSampleKind>))]
public enum WindowsExplicitInputSampleKind
{
    MouseReport,
    MouseMovement,
    MouseButtonTransition,
    KeyTransition
}

[JsonConverter(typeof(JsonStringEnumConverter<WindowsExplicitInputEvidenceKind>))]
public enum WindowsExplicitInputEvidenceKind
{
    MouseReportObservation,
    MouseTrajectoryTest,
    MouseButtonTransitionTest,
    KeyboardTransitionTest,
    KeyboardCombinationTest
}

[JsonConverter(typeof(JsonStringEnumConverter<WindowsExplicitDisplayTimebase>))]
public enum WindowsExplicitDisplayTimebase
{
    QueryPerformanceCounter,
    BrowserPerformanceNow,
    StopwatchTicks
}

/// <summary>
/// Metadata supplied with one actual recorded/submitted result. SourceMetadata is cloned
/// and retained as raw JSON; SourceId and SourceVersion identify the submitter's source.
/// </summary>
public sealed record WindowsExplicitResultMetadata(
    [property: JsonPropertyName("deviceIdentity")] string DeviceIdentity,
    [property: JsonPropertyName("scope")] WindowsExplicitResultScope Scope,
    [property: JsonPropertyName("scopeIdentity")] string ScopeIdentity,
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("sourceId")] string SourceId,
    [property: JsonPropertyName("sourceVersion")] string SourceVersion,
    [property: JsonPropertyName("recordedAt")] DateTimeOffset RecordedAt,
    [property: JsonPropertyName("method")] string Method,
    [property: JsonPropertyName("workload")] string Workload,
    [property: JsonPropertyName("sourceMetadata")] JsonElement SourceMetadata);

/// <summary>A measured value includes its declared canonical unit. No conversion is guessed.</summary>
public sealed record WindowsExplicitMeasurement<T>(
    [property: JsonPropertyName("value")] T Value,
    [property: JsonPropertyName("unit")] string Unit);

/// <summary>
/// Exact typed payload for the 32 in-scope field names. Only the values for Kind may be
/// non-null. Counts/byte capacities use exact unsigned values; null is unknown, never zero.
/// </summary>
public sealed record WindowsExplicitResultValues
{
    [JsonPropertyName("mouse_all_report_jitter_measured")]
    public WindowsExplicitMeasurement<double>? MouseAllReportJitterMeasured { get; init; }
    [JsonPropertyName("mouse_all_report_rate_measured")]
    public WindowsExplicitMeasurement<double>? MouseAllReportRateMeasured { get; init; }
    [JsonPropertyName("mouse_all_trajectory_manual_test")]
    public WindowsExplicitMeasurement<ulong>? MouseAllTrajectoryManualTest { get; init; }
    [JsonPropertyName("keyboard_all_combo_manual_test")]
    public WindowsExplicitMeasurement<ulong>? KeyboardAllComboManualTest { get; init; }
    [JsonPropertyName("keyboard_all_key_transition_manual_test")]
    public WindowsExplicitMeasurement<ulong>? KeyboardAllKeyTransitionManualTest { get; init; }
    [JsonPropertyName("keyboard_all_scan_code_manual_test")]
    public WindowsExplicitMeasurement<ImmutableArray<uint>>? KeyboardAllScanCodeManualTest { get; init; }
    [JsonPropertyName("mouse_all_button_bounce_manual_test")]
    public WindowsExplicitMeasurement<ulong>? MouseAllButtonBounceManualTest { get; init; }
    [JsonPropertyName("mouse_all_button_transition_manual_test")]
    public WindowsExplicitMeasurement<ulong>? MouseAllButtonTransitionManualTest { get; init; }
    [JsonPropertyName("display_all_animation_fps_manual_test")]
    public WindowsExplicitMeasurement<double>? DisplayAllAnimationFpsManualTest { get; init; }
    [JsonPropertyName("display_all_frame_jitter_manual_test")]
    public WindowsExplicitMeasurement<double>? DisplayAllFrameJitterManualTest { get; init; }
    [JsonPropertyName("display_all_dead_pixel_observation_manual_test")]
    public WindowsExplicitMeasurement<string>? DisplayAllDeadPixelObservationManualTest { get; init; }
    [JsonPropertyName("display_all_pattern_observation_manual_test")]
    public WindowsExplicitMeasurement<string>? DisplayAllPatternObservationManualTest { get; init; }
    [JsonPropertyName("storage_all_latency_manual_test")]
    public WindowsExplicitMeasurement<double>? StorageAllLatencyManualTest { get; init; }
    [JsonPropertyName("storage_all_random_iops_manual_test")]
    public WindowsExplicitMeasurement<double>? StorageAllRandomIopsManualTest { get; init; }
    [JsonPropertyName("storage_all_sequential_throughput_manual_test")]
    public WindowsExplicitMeasurement<double>? StorageAllSequentialThroughputManualTest { get; init; }
    [JsonPropertyName("cpu_all_benchmark_duration_ms")]
    public WindowsExplicitMeasurement<double>? CpuAllBenchmarkDurationMs { get; init; }
    [JsonPropertyName("cpu_all_benchmark_score")]
    public WindowsExplicitMeasurement<double>? CpuAllBenchmarkScore { get; init; }
    [JsonPropertyName("cpu_all_benchmark_workload_declared")]
    public WindowsExplicitMeasurement<string>? CpuAllBenchmarkWorkloadDeclared { get; init; }
    [JsonPropertyName("gpu_all_benchmark_duration_ms")]
    public WindowsExplicitMeasurement<double>? GpuAllBenchmarkDurationMs { get; init; }
    [JsonPropertyName("gpu_all_benchmark_fps")]
    public WindowsExplicitMeasurement<double>? GpuAllBenchmarkFps { get; init; }
    [JsonPropertyName("gpu_all_benchmark_score")]
    public WindowsExplicitMeasurement<double>? GpuAllBenchmarkScore { get; init; }
    [JsonPropertyName("cpu_all_selftest_error_reported")]
    public WindowsExplicitMeasurement<string>? CpuAllSelftestErrorReported { get; init; }
    [JsonPropertyName("cpu_all_selftest_error_time")]
    public WindowsExplicitMeasurement<DateTimeOffset>? CpuAllSelftestErrorTime { get; init; }
    [JsonPropertyName("cpu_all_selftest_workload_declared")]
    public WindowsExplicitMeasurement<string>? CpuAllSelftestWorkloadDeclared { get; init; }
    [JsonPropertyName("memory_all_test_coverage_manual_test")]
    public WindowsExplicitMeasurement<UInt128>? MemoryAllTestCoverageManualTest { get; init; }
    [JsonPropertyName("memory_all_test_error_count_manual_test")]
    public WindowsExplicitMeasurement<ulong>? MemoryAllTestErrorCountManualTest { get; init; }
    [JsonPropertyName("storage_all_readback_error_count_manual_test")]
    public WindowsExplicitMeasurement<ulong>? StorageAllReadbackErrorCountManualTest { get; init; }
    [JsonPropertyName("storage_all_verified_capacity_manual_test")]
    public WindowsExplicitMeasurement<UInt128>? StorageAllVerifiedCapacityManualTest { get; init; }
    [JsonPropertyName("memory_all_hierarchy_latency_manual_test")]
    public WindowsExplicitMeasurement<double>? MemoryAllHierarchyLatencyManualTest { get; init; }
    [JsonPropertyName("reference_all_ranking_date")]
    public WindowsExplicitMeasurement<DateOnly>? ReferenceAllRankingDate { get; init; }
    [JsonPropertyName("reference_all_ranking_method_declared")]
    public WindowsExplicitMeasurement<string>? ReferenceAllRankingMethodDeclared { get; init; }
    [JsonPropertyName("reference_all_ranking_source")]
    public WindowsExplicitMeasurement<string>? ReferenceAllRankingSource { get; init; }
}

/// <summary>Recorded RawInput evidence supplied by the caller; this type does not register RawInput.</summary>
public sealed record WindowsExplicitInputSession(
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("scopeIdentity")] string ScopeIdentity,
    [property: JsonPropertyName("deviceHandle")] ulong DeviceHandle,
    [property: JsonPropertyName("qpcFrequency")] long QpcFrequency,
    [property: JsonPropertyName("startQpc")] long StartQpc,
    [property: JsonPropertyName("endQpc")] long EndQpc,
    [property: JsonPropertyName("formulaVersion")] string FormulaVersion,
    [property: JsonPropertyName("evidence")] ImmutableArray<WindowsExplicitInputEvidence> Evidence,
    [property: JsonPropertyName("bounceWindowQpc")] long BounceWindowQpc,
    [property: JsonPropertyName("samples")] ImmutableArray<WindowsExplicitInputSample> Samples,
    [property: JsonPropertyName("rawEvents")] ImmutableArray<WindowsExplicitRawInputEvent> RawEvents = default,
    [property: JsonPropertyName("maximumRawInputBytes")] uint MaximumRawInputBytes = 0);

public sealed record WindowsExplicitInputEvidence(
    [property: JsonPropertyName("kind")] WindowsExplicitInputEvidenceKind Kind,
    [property: JsonPropertyName("actionId")] string ActionId,
    [property: JsonPropertyName("startQpc")] long StartQpc,
    [property: JsonPropertyName("endQpc")] long EndQpc);

public sealed record WindowsExplicitInputSample(
    [property: JsonPropertyName("deviceHandle")] ulong DeviceHandle,
    [property: JsonPropertyName("qpc")] long Qpc,
    [property: JsonPropertyName("kind")] WindowsExplicitInputSampleKind Kind,
    [property: JsonPropertyName("purposes")] ImmutableArray<WindowsExplicitInputEvidenceKind> Purposes,
    [property: JsonPropertyName("code")] uint Code,
    [property: JsonPropertyName("deltaX")] int DeltaX,
    [property: JsonPropertyName("deltaY")] int DeltaY,
    [property: JsonPropertyName("isDown")] bool? IsDown);

[JsonConverter(typeof(JsonStringEnumConverter<WindowsExplicitRawInputPacketKind>))]
public enum WindowsExplicitRawInputPacketKind
{
    Mouse,
    Keyboard
}

/// <summary>
/// Full selected-device RAWMOUSE or RAWKEYBOARD packet plus a host QPC sampled after
/// retrieving that WM_INPUT payload. Fields for the other packet kind remain zero;
/// Kind is authoritative.
/// </summary>
public sealed record WindowsExplicitRawInputEvent(
    [property: JsonPropertyName("deviceHandle")] ulong DeviceHandle,
    [property: JsonPropertyName("qpcAtDispatch")] long QpcAtDispatch,
    [property: JsonPropertyName("kind")] WindowsExplicitRawInputPacketKind Kind,
    [property: JsonPropertyName("inputSizeBytes")] uint InputSizeBytes,
    [property: JsonPropertyName("messageInputCode")] uint MessageInputCode,
    [property: JsonPropertyName("purposes")] ImmutableArray<WindowsExplicitInputEvidenceKind> Purposes,
    [property: JsonPropertyName("mouseFlags")] ushort MouseFlags,
    [property: JsonPropertyName("mouseButtonsUnion")] uint MouseButtonsUnion,
    [property: JsonPropertyName("mouseButtonFlags")] ushort MouseButtonFlags,
    [property: JsonPropertyName("mouseButtonData")] ushort MouseButtonData,
    [property: JsonPropertyName("mouseRawButtons")] uint MouseRawButtons,
    [property: JsonPropertyName("mouseX")] int MouseX,
    [property: JsonPropertyName("mouseY")] int MouseY,
    [property: JsonPropertyName("mouseExtraInformation")] uint MouseExtraInformation,
    [property: JsonPropertyName("keyboardMakeCode")] ushort KeyboardMakeCode,
    [property: JsonPropertyName("keyboardFlags")] ushort KeyboardFlags,
    [property: JsonPropertyName("keyboardReserved")] ushort KeyboardReserved,
    [property: JsonPropertyName("virtualKey")] ushort VirtualKey,
    [property: JsonPropertyName("keyboardMessage")] uint KeyboardMessage,
    [property: JsonPropertyName("keyboardExtraInformation")] uint KeyboardExtraInformation);

/// <summary>Recorded display context supplied by the caller; this type does not sample scan-out.</summary>
public sealed record WindowsExplicitDisplaySession(
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("scopeIdentity")] string ScopeIdentity,
    [property: JsonPropertyName("visible")] bool Visible,
    [property: JsonPropertyName("refreshRateHz")] double RefreshRateHz,
    [property: JsonPropertyName("timebase")] WindowsExplicitDisplayTimebase Timebase,
    [property: JsonPropertyName("ticksPerSecond")] long TicksPerSecond,
    [property: JsonPropertyName("startTicks")] long StartTicks,
    [property: JsonPropertyName("endTicks")] long EndTicks,
    [property: JsonPropertyName("formulaVersion")] string FormulaVersion,
    [property: JsonPropertyName("frameTicks")] ImmutableArray<long> FrameTicks,
    [property: JsonPropertyName("patternId")] string? PatternId);

public sealed record WindowsExplicitResultSubmission(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("kind")] WindowsExplicitResultKind Kind,
    [property: JsonPropertyName("metadata")] WindowsExplicitResultMetadata Metadata,
    [property: JsonPropertyName("values")] WindowsExplicitResultValues Values,
    [property: JsonPropertyName("inputSession")] WindowsExplicitInputSession? InputSession = null,
    [property: JsonPropertyName("displaySession")] WindowsExplicitDisplaySession? DisplaySession = null);

/// <summary>The owner's one immutable latest submission and its public Information snapshot.</summary>
public sealed record WindowsExplicitResultCurrent(
    ulong Revision,
    WindowsExplicitResultKind? Kind,
    WindowsExplicitResultMetadata? Metadata,
    WindowsExplicitResultValues Values,
    WindowsExplicitInputSession? InputSession,
    WindowsExplicitDisplaySession? DisplaySession,
    ResourceManager.ReadWrite.Information.DeviceInformationSnapshot Information);
