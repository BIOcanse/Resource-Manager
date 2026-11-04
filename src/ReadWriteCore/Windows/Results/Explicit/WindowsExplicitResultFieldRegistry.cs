using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using ResourceManager.ReadWrite.Information;

namespace ResourceManager.ReadWrite.Windows.Results.Explicit;

/// <summary>Only the exact 32 manual/result/reference names in the reviewed contract.</summary>
public static class WindowsExplicitResultFieldRegistry
{
    public const string SourceId = "resource-manager.explicit-result-submit";
    public const string CollectionItemId = "information.explicit-results";

    public static readonly DeviceInformationDefinition MouseAllReportJitterMeasured = Number("mouse_all_report_jitter_measured", "manual_test", "ms");
    public static readonly DeviceInformationDefinition MouseAllReportRateMeasured = Number("mouse_all_report_rate_measured", "manual_test", "Hz");
    public static readonly DeviceInformationDefinition MouseAllTrajectoryManualTest = Unsigned64("mouse_all_trajectory_manual_test", "manual_test", "points");
    public static readonly DeviceInformationDefinition KeyboardAllComboManualTest = Unsigned64("keyboard_all_combo_manual_test", "manual_test", "event");
    public static readonly DeviceInformationDefinition KeyboardAllKeyTransitionManualTest = Unsigned64("keyboard_all_key_transition_manual_test", "manual_test", "event");
    public static readonly DeviceInformationDefinition KeyboardAllScanCodeManualTest = new("keyboard_all_scan_code_manual_test", "manual_test", "all", "integer-list", "code", "keyboard.scan_code_manual_test", SourceId, ReadVolatility.EventDriven("WindowsExplicitResultOwner.Submit accepted result"));
    public static readonly DeviceInformationDefinition MouseAllButtonBounceManualTest = Unsigned64("mouse_all_button_bounce_manual_test", "manual_test", "count");
    public static readonly DeviceInformationDefinition MouseAllButtonTransitionManualTest = Unsigned64("mouse_all_button_transition_manual_test", "manual_test", "event");
    public static readonly DeviceInformationDefinition DisplayAllAnimationFpsManualTest = Number("display_all_animation_fps_manual_test", "manual_test", "fps");
    public static readonly DeviceInformationDefinition DisplayAllFrameJitterManualTest = Number("display_all_frame_jitter_manual_test", "manual_test", "ms");
    public static readonly DeviceInformationDefinition DisplayAllDeadPixelObservationManualTest = Text("display_all_dead_pixel_observation_manual_test", "manual_test", "text");
    public static readonly DeviceInformationDefinition DisplayAllPatternObservationManualTest = Text("display_all_pattern_observation_manual_test", "manual_test", "text");
    public static readonly DeviceInformationDefinition StorageAllLatencyManualTest = Number("storage_all_latency_manual_test", "manual_test", "ms");
    public static readonly DeviceInformationDefinition StorageAllRandomIopsManualTest = Number("storage_all_random_iops_manual_test", "manual_test", "ops/s");
    public static readonly DeviceInformationDefinition StorageAllSequentialThroughputManualTest = Number("storage_all_sequential_throughput_manual_test", "manual_test", "MB/s");
    public static readonly DeviceInformationDefinition CpuAllBenchmarkDurationMs = Number("cpu_all_benchmark_duration_ms", "test_result", "ms");
    public static readonly DeviceInformationDefinition CpuAllBenchmarkScore = Number("cpu_all_benchmark_score", "test_result", "score");
    public static readonly DeviceInformationDefinition CpuAllBenchmarkWorkloadDeclared = Text("cpu_all_benchmark_workload_declared", "test_result", "text");
    public static readonly DeviceInformationDefinition GpuAllBenchmarkDurationMs = Number("gpu_all_benchmark_duration_ms", "test_result", "ms");
    public static readonly DeviceInformationDefinition GpuAllBenchmarkFps = Number("gpu_all_benchmark_fps", "test_result", "fps");
    public static readonly DeviceInformationDefinition GpuAllBenchmarkScore = Number("gpu_all_benchmark_score", "test_result", "score");
    public static readonly DeviceInformationDefinition CpuAllSelftestErrorReported = Text("cpu_all_selftest_error_reported", "test_result", "text");
    public static readonly DeviceInformationDefinition CpuAllSelftestErrorTime = Text("cpu_all_selftest_error_time", "test_result", "timestamp");
    public static readonly DeviceInformationDefinition CpuAllSelftestWorkloadDeclared = Text("cpu_all_selftest_workload_declared", "test_result", "text");
    public static readonly DeviceInformationDefinition MemoryAllTestCoverageManualTest = Unsigned128("memory_all_test_coverage_manual_test", "manual_test", "B");
    public static readonly DeviceInformationDefinition MemoryAllTestErrorCountManualTest = Unsigned64("memory_all_test_error_count_manual_test", "manual_test", "count");
    public static readonly DeviceInformationDefinition StorageAllReadbackErrorCountManualTest = Unsigned64("storage_all_readback_error_count_manual_test", "manual_test", "count");
    public static readonly DeviceInformationDefinition StorageAllVerifiedCapacityManualTest = Unsigned128("storage_all_verified_capacity_manual_test", "manual_test", "B");
    public static readonly DeviceInformationDefinition MemoryAllHierarchyLatencyManualTest = Number("memory_all_hierarchy_latency_manual_test", "manual_test", "ns");
    public static readonly DeviceInformationDefinition ReferenceAllRankingDate = Text("reference_all_ranking_date", "reference", "date");
    public static readonly DeviceInformationDefinition ReferenceAllRankingMethodDeclared = Text("reference_all_ranking_method_declared", "reference", "text");
    public static readonly DeviceInformationDefinition ReferenceAllRankingSource = Text("reference_all_ranking_source", "reference", "URL");

    public static ImmutableArray<DeviceInformationDefinition> Definitions { get; } =
    [
        MouseAllReportJitterMeasured, MouseAllReportRateMeasured, MouseAllTrajectoryManualTest,
        KeyboardAllComboManualTest, KeyboardAllKeyTransitionManualTest, KeyboardAllScanCodeManualTest,
        MouseAllButtonBounceManualTest, MouseAllButtonTransitionManualTest,
        DisplayAllAnimationFpsManualTest, DisplayAllFrameJitterManualTest,
        DisplayAllDeadPixelObservationManualTest, DisplayAllPatternObservationManualTest,
        StorageAllLatencyManualTest, StorageAllRandomIopsManualTest, StorageAllSequentialThroughputManualTest,
        CpuAllBenchmarkDurationMs, CpuAllBenchmarkScore, CpuAllBenchmarkWorkloadDeclared,
        GpuAllBenchmarkDurationMs, GpuAllBenchmarkFps, GpuAllBenchmarkScore,
        CpuAllSelftestErrorReported, CpuAllSelftestErrorTime, CpuAllSelftestWorkloadDeclared,
        MemoryAllTestCoverageManualTest, MemoryAllTestErrorCountManualTest,
        StorageAllReadbackErrorCountManualTest, StorageAllVerifiedCapacityManualTest,
        MemoryAllHierarchyLatencyManualTest, ReferenceAllRankingDate,
        ReferenceAllRankingMethodDeclared, ReferenceAllRankingSource
    ];

    internal static ImmutableArray<WindowsExplicitResultValueEntry> Entries(WindowsExplicitResultValues values)
    {
        var result = ImmutableArray.CreateBuilder<WindowsExplicitResultValueEntry>(Definitions.Length);
        Add(result, MouseAllReportJitterMeasured, values.MouseAllReportJitterMeasured);
        Add(result, MouseAllReportRateMeasured, values.MouseAllReportRateMeasured);
        Add(result, MouseAllTrajectoryManualTest, values.MouseAllTrajectoryManualTest);
        Add(result, KeyboardAllComboManualTest, values.KeyboardAllComboManualTest);
        Add(result, KeyboardAllKeyTransitionManualTest, values.KeyboardAllKeyTransitionManualTest);
        Add(result, KeyboardAllScanCodeManualTest, values.KeyboardAllScanCodeManualTest);
        Add(result, MouseAllButtonBounceManualTest, values.MouseAllButtonBounceManualTest);
        Add(result, MouseAllButtonTransitionManualTest, values.MouseAllButtonTransitionManualTest);
        Add(result, DisplayAllAnimationFpsManualTest, values.DisplayAllAnimationFpsManualTest);
        Add(result, DisplayAllFrameJitterManualTest, values.DisplayAllFrameJitterManualTest);
        Add(result, DisplayAllDeadPixelObservationManualTest, values.DisplayAllDeadPixelObservationManualTest);
        Add(result, DisplayAllPatternObservationManualTest, values.DisplayAllPatternObservationManualTest);
        Add(result, StorageAllLatencyManualTest, values.StorageAllLatencyManualTest);
        Add(result, StorageAllRandomIopsManualTest, values.StorageAllRandomIopsManualTest);
        Add(result, StorageAllSequentialThroughputManualTest, values.StorageAllSequentialThroughputManualTest);
        Add(result, CpuAllBenchmarkDurationMs, values.CpuAllBenchmarkDurationMs);
        Add(result, CpuAllBenchmarkScore, values.CpuAllBenchmarkScore);
        Add(result, CpuAllBenchmarkWorkloadDeclared, values.CpuAllBenchmarkWorkloadDeclared);
        Add(result, GpuAllBenchmarkDurationMs, values.GpuAllBenchmarkDurationMs);
        Add(result, GpuAllBenchmarkFps, values.GpuAllBenchmarkFps);
        Add(result, GpuAllBenchmarkScore, values.GpuAllBenchmarkScore);
        Add(result, CpuAllSelftestErrorReported, values.CpuAllSelftestErrorReported);
        Add(result, CpuAllSelftestErrorTime, values.CpuAllSelftestErrorTime);
        Add(result, CpuAllSelftestWorkloadDeclared, values.CpuAllSelftestWorkloadDeclared);
        Add(result, MemoryAllTestCoverageManualTest, values.MemoryAllTestCoverageManualTest);
        Add(result, MemoryAllTestErrorCountManualTest, values.MemoryAllTestErrorCountManualTest);
        Add(result, StorageAllReadbackErrorCountManualTest, values.StorageAllReadbackErrorCountManualTest);
        Add(result, StorageAllVerifiedCapacityManualTest, values.StorageAllVerifiedCapacityManualTest);
        Add(result, MemoryAllHierarchyLatencyManualTest, values.MemoryAllHierarchyLatencyManualTest);
        Add(result, ReferenceAllRankingDate, values.ReferenceAllRankingDate);
        Add(result, ReferenceAllRankingMethodDeclared, values.ReferenceAllRankingMethodDeclared);
        Add(result, ReferenceAllRankingSource, values.ReferenceAllRankingSource);
        return result.ToImmutable();
    }

    internal static DeviceInformationSnapshot Project(string deviceIdentity,
        WindowsExplicitResultMetadata? metadata, WindowsExplicitResultValues values)
    {
        var byName = Entries(values).ToDictionary(static item => item.Name, StringComparer.Ordinal);
        var devices = Definitions.GroupBy(static definition => definition.Resource, StringComparer.Ordinal)
            .Select(group =>
            {
                var objectKind = group.Key switch
                {
                    "manual_test" => WindowsExplicitResultObjectKind.ManualTest,
                    "test_result" => WindowsExplicitResultObjectKind.TestResult,
                    "reference" => WindowsExplicitResultObjectKind.Reference,
                    _ => throw new InvalidOperationException($"Unknown explicit result object group '{group.Key}'.")
                };
                var objectIdentity = WindowsExplicitResultOwner.GetInformationDeviceIdentity(deviceIdentity, objectKind);
                return new DeviceInformationObject(objectIdentity, group.Key,
                    group.Select(definition => byName.TryGetValue(definition.Name, out var actual)
                        ? new DeviceInformationField(definition, Encode(definition, actual.Value))
                            { ObservedAt = metadata?.RecordedAt }
                        : new DeviceInformationField(definition, null)).ToImmutableArray());
            }).ToImmutableArray();
        return new(metadata?.RecordedAt, devices);
    }

    private static JsonElement Encode(DeviceInformationDefinition definition, object value)
    {
        if (definition.ValueKind == "unsigned-integer")
        {
            var text = value switch
            {
                UInt128 number => number.ToString(CultureInfo.InvariantCulture),
                _ => throw new InvalidOperationException($"{definition.Name} requires an exact UInt128 value.")
            };
            return JsonSerializer.SerializeToElement(text);
        }
        if (definition.ValueKind == "unsigned64")
        {
            var text = value is ulong number ? number.ToString(CultureInfo.InvariantCulture)
                : throw new InvalidOperationException($"{definition.Name} requires an exact UInt64 value.");
            return JsonSerializer.SerializeToElement(text);
        }
        return value switch
        {
            double number => JsonSerializer.SerializeToElement(number),
            string text => JsonSerializer.SerializeToElement(text),
            ImmutableArray<uint> codes => JsonSerializer.SerializeToElement(codes),
            DateOnly date => JsonSerializer.SerializeToElement(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
            DateTimeOffset timestamp => JsonSerializer.SerializeToElement(timestamp.ToString("O", CultureInfo.InvariantCulture)),
            _ => throw new InvalidOperationException($"{definition.Name} has an unsupported explicit value type.")
        };
    }

    private static void Add<T>(ImmutableArray<WindowsExplicitResultValueEntry>.Builder target,
        DeviceInformationDefinition definition, WindowsExplicitMeasurement<T>? measurement)
    {
        if (measurement is { } value)
            target.Add(new(definition.Name, value.Unit, value.Value!));
    }

    private static DeviceInformationDefinition Number(string name, string resource, string unit)
        => new(name, resource, "all", "number", unit, name, SourceId, ReadVolatility.EventDriven("WindowsExplicitResultOwner.Submit accepted result"));
    private static DeviceInformationDefinition Unsigned64(string name, string resource, string unit)
        => new(name, resource, "all", "unsigned64", unit, name, SourceId, ReadVolatility.EventDriven("WindowsExplicitResultOwner.Submit accepted result"));
    private static DeviceInformationDefinition Unsigned128(string name, string resource, string unit)
        => new(name, resource, "all", "unsigned-integer", unit, name, SourceId, ReadVolatility.EventDriven("WindowsExplicitResultOwner.Submit accepted result"));
    private static DeviceInformationDefinition Text(string name, string resource, string unit)
        => new(name, resource, "all", "text", unit, name, SourceId, ReadVolatility.EventDriven("WindowsExplicitResultOwner.Submit accepted result"));
}

internal sealed record WindowsExplicitResultValueEntry(string Name, string Unit, object Value);
