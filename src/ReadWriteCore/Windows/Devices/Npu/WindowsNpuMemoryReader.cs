using System.Runtime.InteropServices;
using ResourceManager.ReadWrite.Windows.Gpu;

namespace ResourceManager.ReadWrite.Windows.Devices.Npu;

public sealed record NpuMemoryCatalogIdentity(string PnpInstanceId)
{
    public string? NameDeclared { get; init; }
    public string ExactIdentityKey => "windows-npu|" + PnpInstanceId.Trim().ToUpperInvariant();
    public string BindingKey => ExactIdentityKey;
}

[Flags]
public enum NpuMemoryFields
{
    None = 0, DedicatedCommitted = 1, DedicatedResident = 2,
    SharedCommitted = 4, SharedResident = 8, All = 15
}

/// <summary>Current device-level bytes; null means unavailable or not requested.</summary>
public sealed record NpuMemoryRead(string DeviceIdentity, ulong? DedicatedCommitted,
    ulong? DedicatedResident, ulong? SharedCommitted, ulong? SharedResident,
    bool RequiresTopologyRefresh = false);

/// <summary>Reads selected NPU memory domains without retaining a native session or current cache.</summary>
public sealed class WindowsNpuMemoryReader
{
    private readonly IWindowsNpuDxCoreMemoryApi _api;
    private readonly Func<AdapterLuid, uint, uint, uint, WindowsGpuIdentityRead> _readIdentity;

    public WindowsNpuMemoryReader()
        : this(new WindowsNpuDxCoreNativeApi(), new WindowsGpuIdentityBridge().Read) { }

    internal WindowsNpuMemoryReader(IWindowsNpuDxCoreMemoryApi api,
        Func<AdapterLuid, uint, uint, uint, WindowsGpuIdentityRead> readIdentity)
    { _api = api; _readIdentity = readIdentity; }

    [ResourceManager.ReadWrite.RuntimeReadSource(typeof(ResourceManager.App.Infrastructure.Monitoring.NativeMetricSnapshotSources), nameof(ResourceManager.App.Infrastructure.Monitoring.NativeMetricSnapshotSources.WindowsNpuMemory))]
    public IReadOnlyList<NpuMemoryRead> Read(IReadOnlyList<NpuMemoryCatalogIdentity> devices,
        IReadOnlyDictionary<string, NpuMemoryFields> requested, uint effectiveMaximumInstanceCount,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(devices);
        ArgumentNullException.ThrowIfNull(requested);
        cancellationToken.ThrowIfCancellationRequested();
        if (effectiveMaximumInstanceCount == 0) return [];
        var selected = new Dictionary<string, NpuMemoryFields>(StringComparer.OrdinalIgnoreCase);
        foreach (var device in devices)
        {
            if (string.IsNullOrWhiteSpace(device.PnpInstanceId)) throw new ArgumentException("A canonical PnP target is required.", nameof(devices));
            if (requested.TryGetValue(device.ExactIdentityKey, out var fields) && (fields & NpuMemoryFields.All) != 0
                && !selected.TryAdd(device.ExactIdentityKey, fields & NpuMemoryFields.All))
                throw new ArgumentException("A target may only be selected once.", nameof(devices));
        }
        if (selected.Count == 0) return [];
        // The current operation supplies the effective bound derived from all applicable
        // compiled templates' existing MaximumInstanceCount conditions.
        if (devices.Count > effectiveMaximumInstanceCount)
            return selected.Keys.Select(static key => Empty(key)).ToArray();
        var result = selected.Keys.ToDictionary(static key => key, static key => Empty(key, true), StringComparer.OrdinalIgnoreCase);
        var candidates = new List<Candidate>();
        nint factory = 0, list = 0;
        try
        {
            if (_api.CreateFactory(out factory) < 0 || factory == 0
                || _api.CreateAdapterList(factory, WindowsNpuDxCoreEnumeration.NpuAttribute, out list) < 0
                || list == 0) return Unavailable(false);
            if (_api.IsStale(list)) return Unavailable();
            var count = _api.GetAdapterCount(list);
            if (count > WindowsNpuDxCoreEnumeration.MaximumAdapterCount) return Unavailable(false);
            var seenLuids = new HashSet<ulong>();
            var seenPnps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (uint index = 0; index < count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                nint adapter = 0;
                try
                {
                    if (_api.GetAdapter(list, index, out adapter) < 0 || adapter == 0) return Unavailable(false);
                    if (!_api.IsValid(adapter)) return Unavailable();
                    if (!_api.IsAttributeSupported(adapter, WindowsNpuDxCoreEnumeration.NpuAttribute)) continue;
                    if (!TryProperty<byte>(adapter, WindowsNpuDxCoreProperty.IsHardware, out var hardware)) return Unavailable();
                    if (hardware == 0) continue;
                    if (!TryAdapter(adapter, out var native) || !seenLuids.Add(WindowsGpuAdapterIdentity.Pack(native.Luid))) return Unavailable();
                    var countAvailable = TryPhysicalCount(adapter, out var physicalCount);
                    if (countAvailable && physicalCount > WindowsNpuInformationReader.MaximumPhysicalAdapterCount) return Unavailable(false);
                    var identities = _readIdentity(native.Luid, native.VendorId, native.DeviceId,
                        WindowsNpuInformationReader.MaximumPhysicalAdapterCount);
                    if (identities.Identities.Count > WindowsNpuInformationReader.MaximumPhysicalAdapterCount) return Unavailable(false);
                    if (!ValidBridge(identities) || !_api.IsValid(adapter)) return Unavailable();
                    foreach (var identity in identities.Identities)
                    {
                        var key = "windows-npu|" + identity.PnpInstanceId.Trim().ToUpperInvariant();
                        if (!seenPnps.Add(key)) return Unavailable();
                        if (!selected.TryGetValue(key, out var fields)) continue;
                        // A supported multi-physical identity still belongs to the existing 101 directory.
                        // It deliberately has no memory value in this first single-physical implementation.
                        var mismatch = countAvailable && physicalCount != identities.Identities.Count;
                        // The 101 directory declares physical PnP identities, not
                        // support for this single-physical memory implementation.
                        // Rebuilding that same directory cannot fix a count limitation.
                        result[key] = Empty(key);
                        if (!countAvailable || physicalCount != 1 || identities.Identities.Count != 1
                            || identity.PhysicalAdapterIndex != 0 || mismatch) continue;
                        candidates.Add(new Candidate(adapter, native, key, fields));
                        adapter = 0; // The operation owns this reference until the outer finally.
                    }
                }
                finally { if (adapter != 0) _api.Release(adapter); }
            }
            // Resolve the complete selection before querying, so duplicate PnP/LUID mappings cannot
            // produce two reads of a requested domain or publish a partial ambiguous result.
            if (_api.IsStale(list)) return Unavailable();
            foreach (var candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_api.IsStale(list) || !SameIdentity(candidate)) return Unavailable();
                if (!_api.IsQueryStateSupported(candidate.Adapter, WindowsNpuDxCoreState.AdapterMemoryUsageBytes)) continue;
                WindowsNpuDxCoreMemoryUsage? dedicated = null, shared = null;
                if ((candidate.Fields & (NpuMemoryFields.DedicatedCommitted | NpuMemoryFields.DedicatedResident)) != 0)
                    dedicated = Query(candidate.Adapter, WindowsNpuDxCoreMemoryType.Dedicated, cancellationToken);
                if ((candidate.Fields & (NpuMemoryFields.SharedCommitted | NpuMemoryFields.SharedResident)) != 0)
                    shared = Query(candidate.Adapter, WindowsNpuDxCoreMemoryType.Shared, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (_api.IsStale(list) || !SameIdentity(candidate)) return Unavailable();
                result[candidate.Key] = new NpuMemoryRead(candidate.Key,
                    candidate.Fields.HasFlag(NpuMemoryFields.DedicatedCommitted) ? dedicated?.Committed : null,
                    candidate.Fields.HasFlag(NpuMemoryFields.DedicatedResident) ? dedicated?.Resident : null,
                    candidate.Fields.HasFlag(NpuMemoryFields.SharedCommitted) ? shared?.Committed : null,
                    candidate.Fields.HasFlag(NpuMemoryFields.SharedResident) ? shared?.Resident : null);
            }
            cancellationToken.ThrowIfCancellationRequested();
            return _api.IsStale(list) ? Unavailable() : result.Values.ToArray();
        }
        catch (Exception exception) when (ExpectedNativeFailure(exception)) { return Unavailable(false); }
        finally
        {
            foreach (var candidate in candidates) _api.Release(candidate.Adapter);
            if (list != 0) _api.Release(list);
            if (factory != 0) _api.Release(factory);
        }

        NpuMemoryRead[] Unavailable(bool changed = true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return selected.Keys.Select(key => Empty(key, changed)).ToArray();
        }
    }

    [ResourceManager.ReadWrite.RuntimeRead(ResourceManager.ReadWrite.ReadVolatilityKind.EventDriven, "Windows NPU adapter topology changed")]
    private bool SameIdentity(Candidate candidate)
    {
        if (!_api.IsValid(candidate.Adapter)
            || !_api.IsAttributeSupported(candidate.Adapter, WindowsNpuDxCoreEnumeration.NpuAttribute)
            || !TryProperty<byte>(candidate.Adapter, WindowsNpuDxCoreProperty.IsHardware, out var hardware) || hardware == 0
            || !TryAdapter(candidate.Adapter, out var current) || current != candidate.Native
            || !TryPhysicalCount(candidate.Adapter, out var count) || count != 1) return false;
        var read = _readIdentity(current.Luid, current.VendorId, current.DeviceId,
            WindowsNpuInformationReader.MaximumPhysicalAdapterCount);
        return ValidBridge(read) && read.Identities.Count == 1 && read.Identities[0].PhysicalAdapterIndex == 0
            && string.Equals(candidate.Key, "windows-npu|" + read.Identities[0].PnpInstanceId.Trim(), StringComparison.OrdinalIgnoreCase)
            && _api.IsValid(candidate.Adapter);
    }

    private static bool ValidBridge(WindowsGpuIdentityRead read) => WindowsNpuDxCoreIdentity.ValidBridge(read);
    [ResourceManager.ReadWrite.RuntimeRead(ResourceManager.ReadWrite.ReadVolatilityKind.Fixed)]
    private bool TryAdapter(nint adapter, out WindowsNpuDxCoreAdapter value)
        => WindowsNpuDxCoreIdentity.TryAdapter(_api, adapter, out value);
    [ResourceManager.ReadWrite.RuntimeRead(ResourceManager.ReadWrite.ReadVolatilityKind.Fixed)]
    private bool TryPhysicalCount(nint adapter, out uint count)
        => WindowsNpuDxCoreIdentity.TryPhysicalCount(_api, adapter, out count);
    [ResourceManager.ReadWrite.RuntimeReadTransport]
    private bool TryProperty<T>(nint adapter, WindowsNpuDxCoreProperty property, out T value) where T : unmanaged
        => WindowsNpuDxCoreIdentity.TryProperty(_api, adapter, property, out value);

    private unsafe WindowsNpuDxCoreMemoryUsage? Query(nint adapter, WindowsNpuDxCoreMemoryType memoryType, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var input = new WindowsNpuDxCoreMemoryQueryInput { PhysicalAdapterIndex = 0, MemoryType = memoryType };
        WindowsNpuDxCoreMemoryUsage output = default;
        try
        {
            var status = _api.QueryState(adapter, WindowsNpuDxCoreState.AdapterMemoryUsageBytes,
                checked((nuint)sizeof(WindowsNpuDxCoreMemoryQueryInput)), (nint)(&input),
                checked((nuint)sizeof(WindowsNpuDxCoreMemoryUsage)), (nint)(&output));
            token.ThrowIfCancellationRequested();
            return status >= 0 ? output : null;
        }
        catch (Exception exception) when (ExpectedNativeFailure(exception)) { return null; }
    }

    private static bool ExpectedNativeFailure(Exception exception) => exception is ExternalException or DllNotFoundException
        or EntryPointNotFoundException or BadImageFormatException or OverflowException;
    private static NpuMemoryRead Empty(string key, bool changed = false) => new(key, null, null, null, null, changed);
    private sealed record Candidate(nint Adapter, WindowsNpuDxCoreAdapter Native, string Key, NpuMemoryFields Fields);
}
