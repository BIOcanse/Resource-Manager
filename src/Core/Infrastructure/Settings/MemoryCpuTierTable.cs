using System.Collections.Immutable;
using ResourceManager.App.Application.Settings;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Domain.Settings;
using ResourceManager.App.Infrastructure.RuntimeSpecialization.FreedomPoints;
using ResourceManager.App.StateItems;
using ResourceManager.Shared.BusinessBlocks.State;

namespace ResourceManager.App.Infrastructure.Settings;

/// <summary>
/// Compiles the memory/CPU tier table (freedom-point table memory_cpu_tiers). The points are build-time,
/// so <see cref="Product"/> (the embedded declarations) is the table the plan compiler also consumes.
/// </summary>
internal static class MemoryCpuTierTable
{
    internal const string Consumer = "MemoryCpuTierTable.Compile";

    [ResourceManager.ReadWrite.RuntimeRead(ResourceManager.ReadWrite.ReadVolatilityKind.Fixed)]
    internal static CompiledMemoryCpuTierTable Product { get; } = Compile(EmbeddedFreedomPointDefaults.Begin());

    /// <summary>Tier 3 with no overrides over <see cref="Product"/>: what an owner uses before the settings
    /// owner's first publication. It equals the first publication of a settings file without the section.</summary>
    [ResourceManager.ReadWrite.RuntimeRead(ResourceManager.ReadWrite.ReadVolatilityKind.Fixed)]
    internal static MemoryCpuTierResolution ProductDefault { get; } = MemoryCpuTierResolver.Resolve(Product, new());

    internal static CompiledMemoryCpuTierTable Compile(FreedomPointCompilation freedom)
    {
        ArgumentNullException.ThrowIfNull(freedom);
        ImmutableArray<CompiledMemoryCpuTierCell> Column(string address)
            => [.. freedom.Consume<CompiledMemoryCpuTierCell[]>(address, Consumer)];
        var table = new CompiledMemoryCpuTierTable
        {
            GcGen0MaxBudget = Column(BackendFreedomPointPaths.MemoryCpuTierGcGen0MaxBudget),
            GcConserveMemory = Column(BackendFreedomPointPaths.MemoryCpuTierGcConserveMemory),
            LedgerMemoryModeFloor = Column(BackendFreedomPointPaths.MemoryCpuTierLedgerMemoryModeFloor),
            BufferReturn = Column(BackendFreedomPointPaths.MemoryCpuTierBufferReturn),
            NativeWorkareaGrowth = Column(BackendFreedomPointPaths.MemoryCpuTierNativeWorkareaGrowth),
            SingleRequestStopBufferMs = Column(BackendFreedomPointPaths.SingleRequestStopBuffer),
            ServiceIdentityCacheMs = Column(BackendFreedomPointPaths.ServiceIdentityCacheLifetime),
            BrowserRuntimeSnapshotSeconds = Column(BackendFreedomPointPaths.MemoryCpuTierBrowserRuntimeSnapshotSeconds),
            PlatformSensorSnapshotSeconds = Column(BackendFreedomPointPaths.MemoryCpuTierPlatformSensorSnapshotSeconds),
            StorageSnapshotSeconds = Column(BackendFreedomPointPaths.MemoryCpuTierStorageSnapshotSeconds),
            RendererAbsenceEntries = Column(BackendFreedomPointPaths.MemoryCpuTierRendererAbsenceEntries),
            FileQuerySessionsMax = Column(BackendFreedomPointPaths.MemoryCpuTierFileQuerySessionsMax),
            FileQuerySessionsPrebuilt = Column(BackendFreedomPointPaths.MemoryCpuTierFileQuerySessionsPrebuilt),
            FileQueryIdleSessions = Column(BackendFreedomPointPaths.MemoryCpuTierFileQueryIdleSessions),
        };
        MemoryCpuTierResolver.ValidateTable(table);
        return table;
    }
}

/// <summary>How an option owner reads the effective memory/CPU tier values at its single use site.</summary>
internal static class MemoryCpuTierState
{
    /// <summary>The settings owner's latest publication; before it (or without a state authority, as in
    /// isolated owners and tests) the explicit product default, tier 3 with no overrides.</summary>
    [ResourceManager.ReadWrite.RuntimeRead(ResourceManager.ReadWrite.ReadVolatilityKind.EventDriven,
        "JsonAppSettingsStore publishes MemoryCpuTierEffective when the tier or an override changes")]
    internal static MemoryCpuTierEffectiveValues Read(StateAuthority? authority)
    {
        if (authority is null) return MemoryCpuTierTable.ProductDefault.Values;
        var published = authority.Read<MemoryCpuTierResolution>(StateItemUids.MemoryCpuTierEffective);
        return published.HasValue ? published.Value.Values : MemoryCpuTierTable.ProductDefault.Values;
    }
}
