using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using ResourceManager.App.Domain.RuntimeSpecialization.FreedomPoints;

namespace ResourceManager.App.Domain.RuntimeSpecialization;

/// <summary>One tier's cell of a memory/CPU tier option: the value (number, choice text, or null for
/// "not set") and the cell's category, <c>proportional</c> or <c>aggressive</c>.</summary>
public sealed record CompiledMemoryCpuTierCell(
    [property: JsonPropertyName("value")] JsonElement? Value,
    [property: JsonPropertyName("category")] string Category);

/// <summary>Build-time memory/CPU tier table (freedom-point table memory_cpu_tiers): one five-cell column
/// per option, tier 1 first. Only the settings owner's resolver reads it.</summary>
public sealed record CompiledMemoryCpuTierTable
{
    [global::FreedomPoint.FreedomPointBinding(BackendFreedomPointPositions.GcGen0MaxBudget)]
    public required ImmutableArray<CompiledMemoryCpuTierCell> GcGen0MaxBudget { get; init; }

    [global::FreedomPoint.FreedomPointBinding(BackendFreedomPointPositions.GcConserveMemory)]
    public required ImmutableArray<CompiledMemoryCpuTierCell> GcConserveMemory { get; init; }

    [global::FreedomPoint.FreedomPointBinding(BackendFreedomPointPositions.LedgerMemoryModeFloor)]
    public required ImmutableArray<CompiledMemoryCpuTierCell> LedgerMemoryModeFloor { get; init; }

    [global::FreedomPoint.FreedomPointBinding(BackendFreedomPointPositions.BufferReturn)]
    public required ImmutableArray<CompiledMemoryCpuTierCell> BufferReturn { get; init; }

    [global::FreedomPoint.FreedomPointBinding(BackendFreedomPointPositions.NativeWorkareaGrowth)]
    public required ImmutableArray<CompiledMemoryCpuTierCell> NativeWorkareaGrowth { get; init; }

    // The stop buffer and service identity cache are the five-value points monitoring/build/simple/0 and /1.
    [global::FreedomPoint.FreedomPointBinding(BackendFreedomPointPositions.SingleRequestStopBufferMs)]
    public required ImmutableArray<CompiledMemoryCpuTierCell> SingleRequestStopBufferMs { get; init; }

    [global::FreedomPoint.FreedomPointBinding(BackendFreedomPointPositions.ServiceIdentityCacheMs)]
    public required ImmutableArray<CompiledMemoryCpuTierCell> ServiceIdentityCacheMs { get; init; }

    [global::FreedomPoint.FreedomPointBinding(BackendFreedomPointPositions.BrowserRuntimeSnapshotSeconds)]
    public required ImmutableArray<CompiledMemoryCpuTierCell> BrowserRuntimeSnapshotSeconds { get; init; }

    [global::FreedomPoint.FreedomPointBinding(BackendFreedomPointPositions.PlatformSensorSnapshotSeconds)]
    public required ImmutableArray<CompiledMemoryCpuTierCell> PlatformSensorSnapshotSeconds { get; init; }

    [global::FreedomPoint.FreedomPointBinding(BackendFreedomPointPositions.StorageSnapshotSeconds)]
    public required ImmutableArray<CompiledMemoryCpuTierCell> StorageSnapshotSeconds { get; init; }

    [global::FreedomPoint.FreedomPointBinding(BackendFreedomPointPositions.RendererAbsenceEntries)]
    public required ImmutableArray<CompiledMemoryCpuTierCell> RendererAbsenceEntries { get; init; }

    [global::FreedomPoint.FreedomPointBinding(BackendFreedomPointPositions.FileQuerySessionsMax)]
    public required ImmutableArray<CompiledMemoryCpuTierCell> FileQuerySessionsMax { get; init; }

    [global::FreedomPoint.FreedomPointBinding(BackendFreedomPointPositions.FileQuerySessionsPrebuilt)]
    public required ImmutableArray<CompiledMemoryCpuTierCell> FileQuerySessionsPrebuilt { get; init; }

    [global::FreedomPoint.FreedomPointBinding(BackendFreedomPointPositions.FileQueryIdleSessions)]
    public required ImmutableArray<CompiledMemoryCpuTierCell> FileQueryIdleSessions { get; init; }
}
