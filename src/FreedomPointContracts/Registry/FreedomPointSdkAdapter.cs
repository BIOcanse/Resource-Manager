using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Text.Json;
using FreedomPoint;
using ResourceManager.App.Domain.RuntimeSpecialization.FreedomPoints;

namespace ResourceManager.App.Infrastructure.RuntimeSpecialization.FreedomPoints;

/// <summary>Cold-path adapter for existing host declarations and configuration sources.
/// Only declaration positions own SDK identity; host addresses are compiler aliases.</summary>
internal sealed class FreedomPointSdkAdapter
{
    internal PointRegistry Registry { get; }
    internal CompiledFreedomPointTree Declarations { get; }
    private readonly FrozenDictionary<PointPath, CompiledFreedomPoint> byPath;
    private readonly FrozenDictionary<string, PointPath> byAddress;

    internal FreedomPointSdkAdapter(CompiledFreedomPointTree tree,
        Func<CompiledFreedomPoint, ImmutableArray<GenerationTarget>>? generationTargets = null)
    {
        var paths = new Dictionary<string, PointPath>(StringComparer.Ordinal);
        for (var rootIndex = 0; rootIndex < tree.Children.Length; rootIndex++)
        {
            if (rootIndex >= PointPath.MaximumOwner) throw new InvalidDataException("Too many freedom point root owners.");
            var root = tree.Children[rootIndex];
            var pending = new Stack<CompiledFreedomPointTable>();
            pending.Push(root);
            var owner2 = 0;
            while (pending.TryPop(out var table))
            {
                if (owner2 > PointPath.MaximumOwner) throw new InvalidDataException("Too many freedom point descendant owners.");
                Add(table.Build.Simple, PointFamily.Build, PointKind.Simple);
                Add(table.Build.Complex, PointFamily.Build, PointKind.Complex);
                Add(table.Runtime.Simple, PointFamily.Runtime, PointKind.Simple);
                Add(table.Runtime.Complex, PointFamily.Runtime, PointKind.Complex);
                for (var index = table.Children.Length - 1; index >= 0; index--) pending.Push(table.Children[index]);
                owner2++;

                void Add(ImmutableArray<CompiledFreedomPoint> points, PointFamily family, PointKind kind)
                {
                    foreach (var point in points)
                    {
                        if (point.Index > ushort.MaxValue) throw new InvalidDataException("Too many freedom point local indices.");
                        paths.Add(point.Address, new(PointPath.MainProgramPackage, (ushort)(rootIndex + 1),
                            (ushort)owner2, family, kind, (ushort)point.Index));
                    }
                }
            }
        }
        var tables = new Dictionary<CompiledFreedomPointTable, CompiledFreedomPointTable>(ReferenceEqualityComparer.Instance);
        var visit = new Stack<(CompiledFreedomPointTable Table, bool Visited)>();
        foreach (var root in tree.Children.Reverse()) visit.Push((root, false));
        while (visit.TryPop(out var item))
        {
            if (!item.Visited)
            {
                visit.Push((item.Table, true));
                foreach (var child in item.Table.Children.Reverse()) visit.Push((child, false));
                continue;
            }
            tables.Add(item.Table, item.Table with
            {
                Build = Bind(item.Table.Build), Runtime = Bind(item.Table.Runtime),
                Children = item.Table.Children.Select(child => tables[child]).ToImmutableArray()
            });
        }
        Declarations = tree with { Children = tree.Children.Select(root => tables[root]).ToImmutableArray() };
        byPath = Declarations.EnumeratePoints().ToFrozenDictionary(static p => p.Path);
        byAddress = paths.ToFrozenDictionary(StringComparer.Ordinal);
        try
        {
            Registry = PointRegistry.Declare(byPath.Values.Select(point => ToDeclaration(point,
                point.Status == "pending" ? [] : (generationTargets ?? HostTargets)(point))),
                new DeclarationIdentity("resource-manager", [new DeclarationModule("backend", 1, 1)]));
        }
        catch (FreedomPointException error) { throw new InvalidDataException(error.Message, error); }
        Declarations = Declarations with { SdkContractFingerprint = Registry.ContractFingerprint };

        CompiledFreedomPointFamily Bind(CompiledFreedomPointFamily family) => new(
            family.Simple.Select(p => p with { Path = paths[p.Address] }).ToImmutableArray(),
            family.Complex.Select(p => p with { Path = paths[p.Address] }).ToImmutableArray());
    }

    internal PointPath RequirePath(string address) => byAddress.TryGetValue(address, out var path)
        ? path : throw new InvalidDataException($"Freedom point '{address}' is not declared.");

    internal ResolvedFacts Resolve(JsonElement profile, JsonElement cpuConfiguration)
    {
        try
        {
            return FactResolver.Resolve(Registry, new Dictionary<string, Func<PointDeclaration, PointValueSet>>(StringComparer.Ordinal)
            {
                ["registry"] = Read, ["host_manager_profile"] = Read, ["cpu_configuration"] = Read
            }, []);
        }
        catch (FreedomPointException error) { throw new InvalidDataException(error.Message, error); }

        PointValueSet Read(PointDeclaration declaration)
        {
            var point = byPath[declaration.Path];
            var value = point.ValueSource == "registry" ? point.Value!.Value
                : point.ValueSource == "cpu_configuration" ? cpuConfiguration : profile;
            if (point.ValueSource != "registry")
            {
                foreach (var name in point.SourcePath!.Split('/').Skip(1))
                    if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(name, out value))
                        throw new InvalidDataException($"Freedom point '{point.Address}' source '{point.SourcePath}' is missing.");
            }
            BackendFreedomPointRegistry.ValidateValue(value, point.ValueType, point.Address);
            return point.ValueType switch
            {
                "positive_int32" => PointValueSet.Simple(PointValue.FromInteger(value.GetInt32())),
                "nonnegative_int32" => PointValueSet.Simple(PointValue.FromInteger(value.GetInt32())),
                "number" => PointValueSet.Simple(PointValue.FromDecimal(value.GetDouble())),
                "boolean" => PointValueSet.Simple(PointValue.FromChoices(value.GetBoolean() ? 2UL : 1UL)),
                BackendFreedomPointRegistry.TierCellsType => PointValueSet.Simple([.. value.EnumerateArray().SelectMany(static cell => new[]
                {
                    PointValue.FromInteger(cell.GetProperty("value").GetInt32()),
                    PointValue.FromChoices(cell.GetProperty("category").GetString() == "aggressive" ? 2UL : 1UL)
                })]),
                _ => PointValueSet.ComplexValue(value)
            };
        }
    }

    internal JsonElement ValueAsJson(ResolvedFact fact)
    {
        var point = byPath[fact.Point];
        return point.ValueType switch
        {
            "positive_int32" => JsonSerializer.SerializeToElement(fact.Value.Slots[0].Integer),
            "nonnegative_int32" => JsonSerializer.SerializeToElement(fact.Value.Slots[0].Integer),
            "number" => JsonSerializer.SerializeToElement(fact.Value.Slots[0].Decimal),
            "boolean" => JsonSerializer.SerializeToElement(fact.Value.Slots[0].Choices == 2UL),
            BackendFreedomPointRegistry.TierCellsType => TierCellsAsJson(fact.Value.Slots),
            _ => fact.Value.Complex!.Value
        };
    }

    // Target identities belong to the declaration contract; loading the compiled host would make
    // build tools depend on the product. ActualCompiledMembersBindEveryActivePositionAndNoPendingPosition
    // verifies these identities against the attributed host properties.
    private static ImmutableArray<GenerationTarget> HostTargets(CompiledFreedomPoint point)
    {
        var (scope, symbol) = point.Path.Raw switch
        {
            BackendFreedomPointPositions.KernelLeakObservation => ("ResourceManager.App.Domain.RuntimeSpecialization.CompiledHostManagerPlan", "KernelLeak"),
            BackendFreedomPointPositions.Notifications => ("ResourceManager.App.Domain.RuntimeSpecialization.CompiledHostManagerPlan", "Notifications"),
            BackendFreedomPointPositions.MemoryProtection => ("ResourceManager.App.Domain.RuntimeSpecialization.CompiledHostManagerPlan", "HostMemoryProtectionRecords"),
            BackendFreedomPointPositions.KernelEtwLimits => ("ResourceManager.App.Domain.RuntimeSpecialization.CompiledHostManagerPlan", "KernelEtwLimits"),
            BackendFreedomPointPositions.NativeBuildPolicy => ("ResourceManager.App.Domain.RuntimeSpecialization.CompiledHostManagerPlan", "NativeBuildPolicy"),
            BackendFreedomPointPositions.MachineBaselineRatio => ("ResourceManager.App.Domain.RuntimeSpecialization.CompiledCpuScoringPlan", "BaselineRatio"),
            BackendFreedomPointPositions.CorePerformanceWeights => ("ResourceManager.App.Domain.RuntimeSpecialization.CompiledCpuScoringPlan", "CoreWeights"),
            BackendFreedomPointPositions.ObservationWindowMs => ("ResourceManager.App.Domain.RuntimeSpecialization.CompiledCpuCoreResidencyPlan", "ObservationWindowMilliseconds"),
            BackendFreedomPointPositions.ExecutionTimeSource => ("ResourceManager.App.Domain.RuntimeSpecialization.CompiledCpuCoreResidencyPlan", "ExecutionTimeSource"),
            BackendFreedomPointPositions.SmtAccounting => ("ResourceManager.App.Domain.RuntimeSpecialization.CompiledCpuCoreResidencyPlan", "PhysicalCoreByLogicalProcessor"),
            BackendFreedomPointPositions.CrashCounterResetSeconds => ("ResourceManager.App.Domain.RuntimeSpecialization.CompiledHostManagerPlan", "ServiceRecoveryResetPeriodSeconds"),
            BackendFreedomPointPositions.CrashReports => ("ResourceManager.App.Domain.RuntimeSpecialization.CompiledHostManagerPlan", "CrashReports"),
            BackendFreedomPointPositions.RequestLifetimeMs => ("ResourceManager.App.Domain.RuntimeSpecialization.CompiledHostManagerPlan", "TrustedAccessRequestLifetimeMilliseconds"),
            BackendFreedomPointPositions.AvailabilityCheckIntervalMs => ("ResourceManager.App.Domain.RuntimeSpecialization.CompiledHostManagerPlan", "HardwareBridgeAvailabilityCheckInterval"),
            BackendFreedomPointPositions.ObservationGrowthLimit => ("ResourceManager.App.Domain.RuntimeSpecialization.CompiledHostManagerReportCoordinatorBuildPlan", "ObservationGrowthLimit"),
            BackendFreedomPointPositions.GcGen0MaxBudget => ("ResourceManager.App.Domain.RuntimeSpecialization.CompiledMemoryCpuTierTable", "GcGen0MaxBudget"),
            BackendFreedomPointPositions.GcConserveMemory => ("ResourceManager.App.Domain.RuntimeSpecialization.CompiledMemoryCpuTierTable", "GcConserveMemory"),
            BackendFreedomPointPositions.LedgerMemoryModeFloor => ("ResourceManager.App.Domain.RuntimeSpecialization.CompiledMemoryCpuTierTable", "LedgerMemoryModeFloor"),
            BackendFreedomPointPositions.BufferReturn => ("ResourceManager.App.Domain.RuntimeSpecialization.CompiledMemoryCpuTierTable", "BufferReturn"),
            BackendFreedomPointPositions.NativeWorkareaGrowth => ("ResourceManager.App.Domain.RuntimeSpecialization.CompiledMemoryCpuTierTable", "NativeWorkareaGrowth"),
            BackendFreedomPointPositions.SingleRequestStopBufferMs => ("ResourceManager.App.Domain.RuntimeSpecialization.CompiledMemoryCpuTierTable", "SingleRequestStopBufferMs"),
            BackendFreedomPointPositions.ServiceIdentityCacheMs => ("ResourceManager.App.Domain.RuntimeSpecialization.CompiledMemoryCpuTierTable", "ServiceIdentityCacheMs"),
            BackendFreedomPointPositions.BrowserRuntimeSnapshotSeconds => ("ResourceManager.App.Domain.RuntimeSpecialization.CompiledMemoryCpuTierTable", "BrowserRuntimeSnapshotSeconds"),
            BackendFreedomPointPositions.PlatformSensorSnapshotSeconds => ("ResourceManager.App.Domain.RuntimeSpecialization.CompiledMemoryCpuTierTable", "PlatformSensorSnapshotSeconds"),
            BackendFreedomPointPositions.StorageSnapshotSeconds => ("ResourceManager.App.Domain.RuntimeSpecialization.CompiledMemoryCpuTierTable", "StorageSnapshotSeconds"),
            BackendFreedomPointPositions.RendererAbsenceEntries => ("ResourceManager.App.Domain.RuntimeSpecialization.CompiledMemoryCpuTierTable", "RendererAbsenceEntries"),
            BackendFreedomPointPositions.FileQuerySessionsMax => ("ResourceManager.App.Domain.RuntimeSpecialization.CompiledMemoryCpuTierTable", "FileQuerySessionsMax"),
            BackendFreedomPointPositions.FileQuerySessionsPrebuilt => ("ResourceManager.App.Domain.RuntimeSpecialization.CompiledMemoryCpuTierTable", "FileQuerySessionsPrebuilt"),
            BackendFreedomPointPositions.FileQueryIdleSessions => ("ResourceManager.App.Domain.RuntimeSpecialization.CompiledMemoryCpuTierTable", "FileQueryIdleSessions"),
            BackendFreedomPointPositions.OverlayFrameWindowMs => ("ResourceManager.App.Domain.RuntimeSpecialization.CompiledMonitoringSettings", "OverlayFrameWindow"),
            BackendFreedomPointPositions.TargetedRecording => ("ResourceManager.App.Domain.RuntimeSpecialization.CompiledMonitoringSettings", "TargetedRecording"),
            BackendFreedomPointPositions.VidmmSessionBuffers => ("ResourceManager.App.Domain.RuntimeSpecialization.CompiledMonitoringSettings", "VidMmSessionBuffers"),
            BackendFreedomPointPositions.GpuAllocationSessionBuffers => ("ResourceManager.App.Domain.RuntimeSpecialization.CompiledMonitoringSettings", "GpuAllocationSessionBuffers"),
            BackendFreedomPointPositions.PresentSessionBuffers => ("ResourceManager.App.Domain.RuntimeSpecialization.CompiledMonitoringSettings", "PresentSessionBuffers"),
            BackendFreedomPointPositions.FramePipeline => ("ResourceManager.App.Domain.RuntimeSpecialization.CompiledMonitoringSettings", "FramePipeline"),
            BackendFreedomPointPositions.HelperRequestTimeoutMs => ("ResourceManager.App.Domain.RuntimeSpecialization.CompiledHardwareControlSettings", "HelperRequestTimeout"),
            BackendFreedomPointPositions.HelperProcessTiming => ("ResourceManager.App.Domain.RuntimeSpecialization.CompiledHardwareControlSettings", "HelperProcessTiming"),
            BackendFreedomPointPositions.FanCurveFeed => ("ResourceManager.App.Domain.RuntimeSpecialization.CompiledHardwareControlSettings", "FanCurveFeed"),
            BackendFreedomPointPositions.UniwillEcChannel => ("ResourceManager.App.Domain.RuntimeSpecialization.CompiledHardwareControlSettings", "UniwillEcChannel"),
            BackendFreedomPointPositions.WorkerCount => ("ResourceManager.App.Domain.RuntimeSpecialization.CompiledBackendControlSettings", "WorkerCount"),
            BackendFreedomPointPositions.HistoryCapacity => ("ResourceManager.App.Domain.RuntimeSpecialization.CompiledBackendControlSettings", "HistoryCapacity"),
            BackendFreedomPointPositions.CompletionCapacity => ("ResourceManager.App.Domain.RuntimeSpecialization.CompiledBackendControlSettings", "CompletionCapacity"),
            BackendFreedomPointPositions.ReaderCapacity => ("ResourceManager.App.Domain.RuntimeSpecialization.CompiledBackendControlSettings", "ReaderCapacity"),
            BackendFreedomPointPositions.ReaderBytes => ("ResourceManager.App.Domain.RuntimeSpecialization.CompiledBackendControlSettings", "ReaderBytes"),
            BackendFreedomPointPositions.WorkerArenaBytes => ("ResourceManager.App.Domain.RuntimeSpecialization.CompiledBackendControlSettings", "WorkerArenaBytes"),
            BackendFreedomPointPositions.ReasonTextCapacity => ("ResourceManager.App.Domain.RuntimeSpecialization.CompiledBackendControlSettings", "ReasonTextCapacity"),
            BackendFreedomPointPositions.SubscriptionIntervalMs => ("ResourceManager.App.Domain.RuntimeSpecialization.CompiledHostManagerPlan", "SchedulerSamplingInterval"),
            BackendFreedomPointPositions.ActionTimeoutMs => ("ResourceManager.App.Domain.RuntimeSpecialization.CompiledHostManagerPlacementCoordinatorHotPublishPlan", "ActionTimeoutMilliseconds"),
            BackendFreedomPointPositions.GpuWindowExecutionLimits => ("ResourceManager.App.Domain.RuntimeSpecialization.CompiledHostManagerPlacementCoordinatorHotPublishPlan", "WindowExecution"),
            BackendFreedomPointPositions.GpuApiObservationWindowMs => ("ResourceManager.App.Domain.RuntimeSpecialization.CompiledHostManagerPlacementCoordinatorHotPublishPlan", "ApiObservationWindowMilliseconds"),
            BackendFreedomPointPositions.GpuNoPresentExpirySeconds => ("ResourceManager.App.Domain.RuntimeSpecialization.CompiledHostManagerPlacementCoordinatorHotPublishPlan", "NoPresentExpirySeconds"),
            BackendFreedomPointPositions.AutomaticExclusivity => ("ResourceManager.App.Domain.RuntimeSpecialization.CompiledHostManagerPlacementCoordinatorHotPublishPlan", "CpuAutomaticExclusivity"),
            BackendFreedomPointPositions.GameStartGraceMs => ("ResourceManager.App.Domain.RuntimeSpecialization.CompiledHostManagerSmartCoordinatorHotPublishPlan", "GameStartGraceMilliseconds"),
            BackendFreedomPointPositions.RequiredConsecutiveDecisions => ("ResourceManager.App.Domain.RuntimeSpecialization.CompiledHostManagerSmartCoordinatorHotPublishPlan", "RequiredConsecutiveDecisions"),
            BackendFreedomPointPositions.FailureRetryMs => ("ResourceManager.App.Domain.RuntimeSpecialization.CompiledHostManagerSmartCoordinatorHotPublishPlan", "FailureRetryMilliseconds"),
            BackendFreedomPointPositions.SoftwareWelfare => ("ResourceManager.App.Domain.RuntimeSpecialization.CompiledHostManagerSmartCoordinatorHotPublishPlan", "WelfareBaseScore"),
            BackendFreedomPointPositions.StateMultipliers => ("ResourceManager.App.Domain.RuntimeSpecialization.CompiledHostManagerSmartCoordinatorHotPublishPlan", "ProcessStateMultipliers"),
            BackendFreedomPointPositions.BaseScoreTiers => ("ResourceManager.App.Domain.RuntimeSpecialization.CompiledHostManagerSmartCoordinatorHotPublishPlan", "BaseScoreTiers"),
            BackendFreedomPointPositions.RollbackStateStore => ("ResourceManager.App.Domain.RuntimeSpecialization.CompiledOptimizationSettings", "RollbackStateStore"),
            BackendFreedomPointPositions.AuthorityRetirement => ("ResourceManager.App.Domain.RuntimeSpecialization.CompiledOptimizationSettings", "AuthorityRetirement"),
            BackendFreedomPointPositions.IdealProcessorWorkspace => ("ResourceManager.App.Domain.RuntimeSpecialization.CompiledOptimizationSettings", "IdealProcessorWorkspace"),
            BackendFreedomPointPositions.NormalRestoration => ("ResourceManager.App.Domain.RuntimeSpecialization.CompiledOptimizationSettings", "NormalRestoration"),
            BackendFreedomPointPositions.EffectValidationLease => ("ResourceManager.App.Domain.RuntimeSpecialization.CompiledOptimizationSettings", "EffectValidationLease"),
            BackendFreedomPointPositions.MemoryCleanupValidationEvidence => ("ResourceManager.App.Domain.RuntimeSpecialization.CompiledOptimizationSettings", "MemoryCleanupValidationEvidence"),
            BackendFreedomPointPositions.SmartRecordHistory => ("ResourceManager.App.Domain.RuntimeSpecialization.CompiledOptimizationSettings", "SmartRecordHistory"),
              BackendFreedomPointPositions.OptimizationStorageLimits => ("ResourceManager.App.Domain.RuntimeSpecialization.CompiledOptimizationSettings", "StorageLimits"),
              BackendFreedomPointPositions.LegacyAffinity => ("ResourceManager.App.Domain.RuntimeSpecialization.CompiledOptimizationSettings", "LegacyAffinity"),
              BackendFreedomPointPositions.OptimizationRuntimeLimits => ("ResourceManager.App.Domain.RuntimeSpecialization.CompiledOptimizationSettings", "RuntimeLimits"),
            BackendFreedomPointPositions.SoftwareKindBaseScores => ("ResourceManager.App.Domain.RuntimeSpecialization.CompiledOptimizationSettings", "SoftwareKindBaseScores"),
            BackendFreedomPointPositions.CoreClassScores => ("ResourceManager.App.Domain.RuntimeSpecialization.CompiledOptimizationSettings", "CoreClassScores"),
            BackendFreedomPointPositions.ReportSampleIntervalMs => ("ResourceManager.App.Domain.RuntimeSpecialization.CompiledOptimizationSettings", "ReportSampleInterval"),
            BackendFreedomPointPositions.ReportRuleLimits => ("ResourceManager.App.Domain.RuntimeSpecialization.CompiledOptimizationSettings", "ReportRuleLimits"),
            BackendFreedomPointPositions.ProcessHistory => ("ResourceManager.App.Domain.RuntimeSpecialization.CompiledGpuPlacementSettings", "ProcessHistory"),
            BackendFreedomPointPositions.ExternalGpuRuntime => ("ResourceManager.App.Domain.RuntimeSpecialization.CompiledGpuPlacementSettings", "ExternalRuntime"),
            BackendFreedomPointPositions.GpuInjection => ("ResourceManager.App.Domain.RuntimeSpecialization.CompiledGpuPlacementSettings", "Injection"),
            BackendFreedomPointPositions.GpuLaunchReports => ("ResourceManager.App.Domain.RuntimeSpecialization.CompiledGpuPlacementSettings", "LaunchReports"),
            BackendFreedomPointPositions.SqliteVacuum => ("ResourceManager.App.Domain.RuntimeSpecialization.CompiledPersistenceSettings", "SqliteVacuum"),
            _ => throw new InvalidDataException($"Active freedom point '{point.Path}' has no compiled host member.")
        };
        var host = new GenerationTarget
        {
            Language = GenerationLanguage.HostArtifact,
            Form = point.Path.Family == PointFamily.Build ? GenerationForm.BuildConstant : GenerationForm.RuntimeImmutable,
            Scope = scope, Symbol = symbol
        };
        if (point.Path.Raw == BackendFreedomPointPositions.NativeBuildPolicy)
            return [host, new GenerationTarget
            {
                Language = GenerationLanguage.HostArtifact, Form = GenerationForm.BuildConstant,
                Scope = "ResourceManager.App.Infrastructure.Telemetry.Etw.NativePolicyDefaults", Symbol = "SelectedBorrowDrainTimeoutMilliseconds"
            }];
        return point.Path.Raw == BackendFreedomPointPositions.KernelEtwLimits
            ? [host, new GenerationTarget
            {
                Language = GenerationLanguage.HostArtifact, Form = GenerationForm.BuildConstant,
                Scope = "ResourceManager.App.Infrastructure.Telemetry.Etw.KernelEtwNativeDefaults", Symbol = "SelectedLimits"
            }]
            : [host];
    }

    // A five-value simple point: per memory/CPU tier (tier 1 first) one millisecond integer slot and one
    // category choice slot (proportional, aggressive), so the cells keep the tier table's {value, category} shape.
    private static readonly ImmutableArray<SlotContract> TierCellSlots = [.. Enumerable.Range(0, BackendFreedomPointRegistry.TierCellCount)
        .SelectMany(static _ => new[]
        {
            new SlotContract { Kind = ValueKind.Integer, Unit = "milliseconds", IntegerMinimum = 0, IntegerMaximum = int.MaxValue },
            new SlotContract { Kind = ValueKind.Choose, Options = ["proportional", "aggressive"], RequiredSelectionCount = 1 }
        })];

    private static JsonElement TierCellsAsJson(ImmutableArray<PointValue> slots)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            for (var index = 0; index + 1 < slots.Length; index += 2)
            {
                writer.WriteStartObject();
                writer.WriteNumber("value", slots[index].Integer);
                writer.WriteString("category", slots[index + 1].Choices == 2UL ? "aggressive" : "proportional");
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    private static PointDeclaration ToDeclaration(CompiledFreedomPoint point, ImmutableArray<GenerationTarget> targets) => new()
    {
        Path = point.Path, Name = point.Id, Description = point.Description,
        Status = point.Status == "active" ? DeclarationStatus.Active : DeclarationStatus.Pending,
        PendingReason = point.PendingReason, ValueSource = point.ValueSource, SourcePointer = point.SourcePath,
        Update = point.ValueSource is null ? null : point.UpdateClass switch
        {
            "rebuild_backend" => UpdateClass.RebuildArtifact, "publish_plan" => UpdateClass.LivePublish,
            "recreate_host" => UpdateClass.HostRecreate,
            _ => throw new InvalidDataException($"Unknown update class '{point.UpdateClass}'.")
        },
        ComplexContract = point.Path.Kind == PointKind.Complex ? point.ValueType : null,
        Slots = point.Path.Kind == PointKind.Complex ? [] : point.ValueType == BackendFreedomPointRegistry.TierCellsType
            ? TierCellSlots
            : [point.ValueType switch
        {
            "positive_int32" => new SlotContract { Kind = ValueKind.Integer, IntegerMinimum = 1, IntegerMaximum = int.MaxValue },
            "nonnegative_int32" => new SlotContract { Kind = ValueKind.Integer, IntegerMinimum = 0, IntegerMaximum = int.MaxValue },
            "number" => new SlotContract { Kind = ValueKind.Decimal },
            "boolean" => new SlotContract { Kind = ValueKind.Choose, Options = ["false", "true"], RequiredSelectionCount = 1 },
            _ => throw new InvalidDataException($"Unknown simple type '{point.ValueType}'.")
        }],
        GenerationTargets = targets
    };
}
