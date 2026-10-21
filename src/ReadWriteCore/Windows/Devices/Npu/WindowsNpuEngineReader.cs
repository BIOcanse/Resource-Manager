using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using ResourceManager.ReadWrite.Windows.Gpu;

namespace ResourceManager.ReadWrite.Windows.Devices.Npu;

/// <summary>A caller-owned typed directory. Custom resource producers may supply their own recorded or native directory.</summary>
public interface IWindowsNpuEngineDirectory : IDisposable
{
    IReadOnlyList<NpuEngineCatalogIdentity> Identities { get; }
    bool IsCurrent(uint effectiveMaximumInstanceCount, CancellationToken cancellationToken = default);
    IDisposable AcquireLease();
}

/// <summary>A numeric engine ID bound to one live DXCore directory, not a permanent engine UUID.</summary>
public sealed class NpuEngineCatalogIdentity
{
    public NpuEngineCatalogIdentity(string pnp, uint engineIndex, string? name,
        Guid directoryId, ulong luid, uint vendorId, uint deviceId, uint engineCount, ulong? driverVersion, string? pnpDriverVersion,
        IWindowsNpuEngineDirectory directory)
    {
        ArgumentNullException.ThrowIfNull(directory);
        if (string.IsNullOrWhiteSpace(pnp) || pnp.Contains('\0'))
            throw new ArgumentException("An exact parent PnP instance is required.", nameof(pnp));
        if (directoryId == Guid.Empty) throw new ArgumentException("A live directory binding token is required.", nameof(directoryId));
        if (luid == 0) throw new ArgumentOutOfRangeException(nameof(luid));
        if (engineIndex >= engineCount) throw new ArgumentOutOfRangeException(nameof(engineIndex));
        PnpInstanceId = pnp.Trim().ToUpperInvariant();
        EngineIndex = engineIndex;
        NameDeclared = name;
        DirectoryId = directoryId;
        AdapterLuid = luid;
        VendorId = vendorId;
        DeviceId = deviceId;
        EngineCount = engineCount;
        DriverVersionDeclared = driverVersion;
        PnpDriverVersionDeclared = pnpDriverVersion;
        Directory = directory;
    }

    public string PnpInstanceId { get; }
    public uint PhysicalAdapterIndex => 0;
    public uint EngineIndex { get; }
    public string? NameDeclared { get; }
    public Guid DirectoryId { get; }
    public ulong AdapterLuid { get; }
    public uint VendorId { get; }
    public uint DeviceId { get; }
    public uint EngineCount { get; }
    public ulong? DriverVersionDeclared { get; }
    public string? PnpDriverVersionDeclared { get; }
    public string ParentDeviceIdentity => "windows-npu|" + PnpInstanceId;
    public string ExactIdentityKey => "windows-npu-engine|" + PnpInstanceId
        + "|directory:" + DirectoryId.ToString("N") + "|physical:0|engine:"
        + EngineIndex.ToString(CultureInfo.InvariantCulture);
    // Includes an actual live resource incarnation. No baseline may cross a new lease.
    public string BindingKey => ExactIdentityKey + "|luid:" + AdapterLuid.ToString("X16", CultureInfo.InvariantCulture)
        + "|vendor:" + VendorId.ToString("X8", CultureInfo.InvariantCulture)
        + "|device:" + DeviceId.ToString("X8", CultureInfo.InvariantCulture)
        + "|engines:" + EngineCount.ToString(CultureInfo.InvariantCulture)
        + "|driver:" + (DriverVersionDeclared?.ToString("X16", CultureInfo.InvariantCulture) ?? "unknown")
        + "|pnp-driver:" + (PnpDriverVersionDeclared ?? "unknown");
    internal IWindowsNpuEngineDirectory Directory { get; }
}

/// <summary>One actual state4 result and the monotonic interval bracketing that same query.</summary>
public sealed record NpuEngineSample(ulong RunningTimeMicroseconds, ulong QueryBeginTicks,
    ulong QueryReturnTicks, ulong TicksPerSecond);

public sealed record NpuEngineRead(NpuEngineCatalogIdentity Engine, NpuEngineSample? Sample,
    bool RequiresTopologyRefresh = false);

/// <summary>Opens explicit directories. It has no cache, timer or managed counter baseline.</summary>
public sealed class WindowsNpuEngineReader
{
    private readonly IWindowsNpuDxCoreEngineApi api;
    private readonly Func<AdapterLuid, uint, uint, uint, WindowsGpuIdentityRead> readIdentity;
    private readonly Func<long> timestamp;
    private readonly Func<long> frequency;
    private readonly uint maximumEngineNameCharacters;

    /// <param name="maximumEngineNameCharacters">Software budget for optional names; zero disables name queries.</param>
    public WindowsNpuEngineReader(uint maximumEngineNameCharacters = 4096)
        : this(new WindowsNpuDxCoreNativeApi(), new WindowsGpuIdentityBridge().Read,
            Stopwatch.GetTimestamp, static () => Stopwatch.Frequency, maximumEngineNameCharacters) { }

    internal WindowsNpuEngineReader(IWindowsNpuDxCoreEngineApi api,
        Func<AdapterLuid, uint, uint, uint, WindowsGpuIdentityRead> readIdentity,
        Func<long> timestamp, Func<long> frequency, uint maximumEngineNameCharacters)
    {
        this.api = api; this.readIdentity = readIdentity; this.timestamp = timestamp; this.frequency = frequency;
        this.maximumEngineNameCharacters = maximumEngineNameCharacters;
    }

    /// <summary>Null is unavailable; an empty lease is a successful empty directory or no demand.</summary>
    public WindowsNpuEngineDirectoryLease? OpenDirectory(uint effectiveMaximumInstanceCount,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (effectiveMaximumInstanceCount == 0) return new(api, readIdentity, timestamp, frequency, 0, 0, []);
        nint factory = 0, list = 0;
        var bindings = new List<WindowsNpuEngineDirectoryLease.Binding>();
        try
        {
            if (api.CreateFactory(out factory) < 0 || factory == 0
                || api.CreateAdapterList(factory, WindowsNpuDxCoreEnumeration.NpuAttribute, out list) < 0 || list == 0
                || api.IsStale(list)) return null;
            var adapterCount = api.GetAdapterCount(list);
            if (adapterCount > WindowsNpuDxCoreEnumeration.MaximumAdapterCount) return null;
            var luids = new HashSet<ulong>();
            var pnps = new HashSet<string>(StringComparer.Ordinal);
            uint total = 0;
            for (uint index = 0; index < adapterCount; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                nint adapter = 0, adapter1 = 0;
                try
                {
                    if (api.GetAdapter(list, index, out adapter) < 0 || adapter == 0 || !api.IsValid(adapter)) return null;
                    if (!api.IsAttributeSupported(adapter, WindowsNpuDxCoreEnumeration.NpuAttribute)) continue;
                    if (!WindowsNpuDxCoreIdentity.TryProperty<byte>(api, adapter, WindowsNpuDxCoreProperty.IsHardware, out var hardware)) return null;
                    if (hardware == 0) continue;
                    if (!WindowsNpuDxCoreIdentity.TryAdapter(api, adapter, out var native)
                        || !luids.Add(WindowsGpuAdapterIdentity.Pack(native.Luid))
                        || !WindowsNpuDxCoreIdentity.TryPhysicalCount(api, adapter, out var physicalCount) || physicalCount != 1) return null;
                    var bridge = readIdentity(native.Luid, native.VendorId, native.DeviceId,
                        WindowsNpuInformationReader.MaximumPhysicalAdapterCount);
                    if (!WindowsNpuDxCoreIdentity.ValidBridge(bridge) || bridge.Identities.Count != 1
                        || bridge.Identities[0].PhysicalAdapterIndex != 0) return null;
                    var pnp = bridge.Identities[0].PnpInstanceId.Trim().ToUpperInvariant();
                    if (!pnps.Add(pnp) || api.QueryAdapter1(adapter, out adapter1) < 0 || adapter1 == 0
                        || !TryEngineCount(api, adapter1, out var engineCount)) return null;
                    total = checked(total + engineCount);
                    // Enforce the global bound as adapters are accumulated, without truncation.
                    if (total > effectiveMaximumInstanceCount || total > int.MaxValue) return null;
                    var driver = ReadDriver(api, adapter);
                    var names = new string?[checked((int)engineCount)];
                    for (uint engine = 0; engine < engineCount; engine++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        names[checked((int)engine)] = ReadName(api, adapter1, engine, maximumEngineNameCharacters, cancellationToken);
                    }
                    bindings.Add(new(adapter, adapter1, native, pnp, engineCount, driver, bridge.Identities[0].DriverVersion, names));
                    adapter = 0;
                    adapter1 = 0; // Ownership transfers only after the complete binding has been added.
                }
                finally
                {
                    if (adapter1 != 0) api.Release(adapter1);
                    if (adapter != 0) api.Release(adapter);
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            var directory = new WindowsNpuEngineDirectoryLease(api, readIdentity, timestamp, frequency, factory, list, bindings);
            factory = 0;
            list = 0;
            bindings = [];
            try
            {
                if (directory.IsCurrent(effectiveMaximumInstanceCount, cancellationToken)) return directory;
                directory.Dispose();
                return null;
            }
            catch { directory.Dispose(); throw; }
        }
        catch (Exception exception) when (ExpectedNativeFailure(exception)) { return null; }
        finally
        {
            foreach (var binding in bindings)
            {
                api.Release(binding.Adapter1);
                api.Release(binding.Adapter);
            }
            if (list != 0) api.Release(list);
            if (factory != 0) api.Release(factory);
        }
    }

    public IReadOnlyList<NpuEngineRead> Read(IReadOnlyList<NpuEngineCatalogIdentity> devices,
        IReadOnlySet<string> requestedEngineKeys, uint effectiveMaximumInstanceCount,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(devices);
        ArgumentNullException.ThrowIfNull(requestedEngineKeys);
        cancellationToken.ThrowIfCancellationRequested();
        if (effectiveMaximumInstanceCount == 0) return [];
        var selected = devices.Where(device => requestedEngineKeys.Contains(device.ExactIdentityKey)).ToArray();
        if (selected.Length == 0) return [];
        if (selected.Distinct().Count() != selected.Length)
            throw new ArgumentException("A selected engine may only occur once.", nameof(devices));
        if (selected[0].Directory is not WindowsNpuEngineDirectoryLease directory)
            throw new ArgumentException("Native reads require a retained native DXCore directory; custom resources must supply their own read delegate.", nameof(devices));
        if (devices.Count > effectiveMaximumInstanceCount)
            return selected.Select(static engine => new NpuEngineRead(engine, null)).ToArray();
        if (devices.Any(device => !ReferenceEquals(device.Directory, directory)))
            return selected.Select(static engine => new NpuEngineRead(engine, null, true)).ToArray();
        return directory.Read(selected, effectiveMaximumInstanceCount, cancellationToken);
    }

    internal static unsafe bool TryEngineCount(IWindowsNpuDxCoreEngineApi api, nint adapter1, out uint count)
    {
        uint physicalIndex = 0, value = 0;
        var success = api.IsPropertySupported(adapter1, WindowsNpuDxCoreProperty.AdapterEngineCount)
            && api.GetPropertyWithInput(adapter1, WindowsNpuDxCoreProperty.AdapterEngineCount,
                sizeof(uint), (nint)(&physicalIndex), sizeof(uint), (nint)(&value)) >= 0;
        count = value;
        return success;
    }

    internal static ulong? ReadDriver(IWindowsNpuDxCoreApi api, nint adapter)
        => WindowsNpuDxCoreIdentity.TryProperty<ulong>(api, adapter, WindowsNpuDxCoreProperty.DriverVersion, out var driver)
            ? driver : null;

    private static unsafe string? ReadName(IWindowsNpuDxCoreEngineApi api, nint adapter1, uint engine,
        uint maximumCharacters, CancellationToken token)
    {
        // This is a bounded optional declaration, not a directory or classification gate.
        // The bound is caller-configured software policy, not a DXCore ABI limit.
        if (maximumCharacters == 0) return null;
        try
        {
            if (!api.IsPropertySupported(adapter1, WindowsNpuDxCoreProperty.AdapterEngineName)) return null;
            var input = new WindowsNpuDxCoreEngineNameInput
            { AdapterEngineIndex = new() { PhysicalAdapterIndex = 0, EngineIndex = engine } };
            WindowsNpuDxCoreEngineNameOutput output = default;
            // Some implementations return a buffer-size error on the first call while
            // reporting the required length. The bounded output determines whether to retry.
            _ = api.GetPropertyWithInput(adapter1, WindowsNpuDxCoreProperty.AdapterEngineName,
                checked((nuint)sizeof(WindowsNpuDxCoreEngineNameInput)), (nint)(&input),
                checked((nuint)sizeof(WindowsNpuDxCoreEngineNameOutput)), (nint)(&output));
            token.ThrowIfCancellationRequested();
            if (output.EngineNameLength == 0 || output.EngineNameLength > maximumCharacters) return null;
            var capacity = checked(output.EngineNameLength + 1);
            var characters = new char[checked((int)capacity)];
            fixed (char* buffer = characters)
            {
                input.EngineName = (nint)buffer;
                input.EngineNameLength = capacity;
                output = default;
                var status = api.GetPropertyWithInput(adapter1, WindowsNpuDxCoreProperty.AdapterEngineName,
                    checked((nuint)sizeof(WindowsNpuDxCoreEngineNameInput)), (nint)(&input),
                    checked((nuint)sizeof(WindowsNpuDxCoreEngineNameOutput)), (nint)(&output));
                token.ThrowIfCancellationRequested();
                if (status < 0 || output.EngineNameLength == 0 || output.EngineNameLength > capacity) return null;
                var length = checked((int)output.EngineNameLength);
                if (characters[length - 1] == '\0') length--;
                if (length == 0 || characters.AsSpan(0, length).Contains('\0')) return null;
                for (var character = 0; character < length; character++)
                {
                    if (char.IsHighSurrogate(characters[character]))
                    {
                        if (++character == length || !char.IsLowSurrogate(characters[character])) return null;
                    }
                    else if (char.IsLowSurrogate(characters[character])) return null;
                }
                return new string(characters, 0, length);
            }
        }
        catch (Exception exception) when (ExpectedNativeFailure(exception)) { return null; }
    }

    internal static bool ExpectedNativeFailure(Exception exception) => exception is ExternalException or DllNotFoundException
        or EntryPointNotFoundException or BadImageFormatException or OverflowException;
}

/// <summary>Owns one actual factory/list/adapter session. Existing catalog holders borrow this lease.</summary>
public sealed class WindowsNpuEngineDirectoryLease : IWindowsNpuEngineDirectory
{
    internal sealed record Binding(nint Adapter, nint Adapter1, WindowsNpuDxCoreAdapter Native,
        string Pnp, uint EngineCount, ulong? DriverVersion, string? PnpDriverVersion, string?[] Names);
    private enum Validation { Current, Unavailable, Changed }
    private readonly object gate = new();
    private readonly IWindowsNpuDxCoreEngineApi api;
    private readonly Func<AdapterLuid, uint, uint, uint, WindowsGpuIdentityRead> readIdentity;
    private readonly Func<long> timestamp;
    private readonly Func<long> frequency;
    private readonly IReadOnlyList<Binding> bindings;
    private readonly Dictionary<NpuEngineCatalogIdentity, Binding> engines = new();
    private nint factory, list;
    private int references = 1;
    private int initialReleased;
    private bool invalidated;

    internal WindowsNpuEngineDirectoryLease(IWindowsNpuDxCoreEngineApi api,
        Func<AdapterLuid, uint, uint, uint, WindowsGpuIdentityRead> readIdentity,
        Func<long> timestamp, Func<long> frequency, nint factory, nint list, IReadOnlyList<Binding> bindings)
    {
        this.api = api; this.readIdentity = readIdentity; this.timestamp = timestamp; this.frequency = frequency;
        this.factory = factory; this.list = list; this.bindings = bindings.ToArray();
        // This token denotes the live resource acquisition, never each operation or a permanent engine UUID.
        var directoryId = list == 0 ? Guid.Empty : Guid.NewGuid();
        var identities = new List<NpuEngineCatalogIdentity>();
        foreach (var binding in bindings)
        {
            for (uint index = 0; index < binding.EngineCount; index++)
            {
                var identity = new NpuEngineCatalogIdentity(binding.Pnp, index, binding.Names[checked((int)index)], directoryId,
                    WindowsGpuAdapterIdentity.Pack(binding.Native.Luid), binding.Native.VendorId, binding.Native.DeviceId,
                    binding.EngineCount, binding.DriverVersion, binding.PnpDriverVersion, this);
                identities.Add(identity);
                engines.Add(identity, binding);
            }
        }
        Identities = identities.AsReadOnly();
    }

    public IReadOnlyList<NpuEngineCatalogIdentity> Identities { get; }

    public bool IsCurrent(uint effectiveMaximumInstanceCount, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (references == 0 || invalidated || Identities.Count > effectiveMaximumInstanceCount) return false;
            // A no-demand lease observed no hardware. Enabling demand requires an explicit OpenDirectory.
            if (list == 0) return effectiveMaximumInstanceCount == 0;
            try { return Validate(cancellationToken) == Validation.Current; }
            catch (Exception exception) when (WindowsNpuEngineReader.ExpectedNativeFailure(exception)) { return false; }
        }
    }

    public IDisposable AcquireLease()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(references == 0, this);
            references = checked(references + 1);
            return new Borrow(this);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref initialReleased, 1) == 0) Release();
    }

    private void Release()
    {
        lock (gate)
        {
            if (--references != 0) return;
            // Adapter1 QI owns a reference independently of the base pointer's address.
            foreach (var binding in bindings)
            {
                api.Release(binding.Adapter1);
                api.Release(binding.Adapter);
            }
            if (list != 0) { api.Release(list); list = 0; }
            if (factory != 0) { api.Release(factory); factory = 0; }
        }
    }

    private sealed class Borrow(WindowsNpuEngineDirectoryLease directory) : IDisposable
    {
        private WindowsNpuEngineDirectoryLease? owner = directory;
        public void Dispose() => Interlocked.Exchange(ref owner, null)?.Release();
    }

    internal IReadOnlyList<NpuEngineRead> Read(IReadOnlyList<NpuEngineCatalogIdentity> targets,
        uint maximum, CancellationToken token)
    {
        lock (gate)
        {
            token.ThrowIfCancellationRequested();
            NpuEngineRead[] Empty(bool changed = false)
                => targets.Select(engine => new NpuEngineRead(engine, null, changed)).ToArray();
            if (references == 0 || invalidated || targets.Any(target => !engines.ContainsKey(target))) return Empty(true);
            if (maximum == 0 || Identities.Count > maximum) return Empty();
            try
            {
                var before = Validate(token);
                if (before != Validation.Current) return Empty(before == Validation.Changed);
                var pending = new List<NpuEngineRead>(targets.Count);
                foreach (var target in targets)
                {
                    token.ThrowIfCancellationRequested();
                    var binding = engines[target];
                    var sample = api.IsQueryStateSupported(binding.Adapter, WindowsNpuDxCoreState.AdapterEngineRunningTimeMicroseconds)
                        ? Query(binding.Adapter, target.EngineIndex, token) : null;
                    pending.Add(new(target, sample));
                }
                var after = Validate(token);
                token.ThrowIfCancellationRequested();
                return after == Validation.Current ? pending : Empty(after == Validation.Changed);
            }
            catch (Exception exception) when (WindowsNpuEngineReader.ExpectedNativeFailure(exception)) { return Empty(); }
        }
    }

    private Validation Changed() { invalidated = true; return Validation.Changed; }

    private Validation Validate(CancellationToken token)
    {
        if (invalidated || references == 0) return Changed();
        if (list == 0) return bindings.Count == 0 ? Validation.Current : Changed();
        if (api.IsStale(list)) return Changed();
        foreach (var binding in bindings)
        {
            token.ThrowIfCancellationRequested();
            if (!api.IsValid(binding.Adapter)
                || !api.IsAttributeSupported(binding.Adapter, WindowsNpuDxCoreEnumeration.NpuAttribute)) return Changed();
            if (!WindowsNpuDxCoreIdentity.TryProperty<byte>(api, binding.Adapter, WindowsNpuDxCoreProperty.IsHardware, out var hardware)
                || !WindowsNpuDxCoreIdentity.TryAdapter(api, binding.Adapter, out var native)
                || !WindowsNpuDxCoreIdentity.TryPhysicalCount(api, binding.Adapter, out var physicalCount)) return Validation.Unavailable;
            if (hardware == 0 || native != binding.Native || physicalCount != 1) return Changed();
            var bridge = readIdentity(native.Luid, native.VendorId, native.DeviceId,
                WindowsNpuInformationReader.MaximumPhysicalAdapterCount);
            if (!WindowsNpuDxCoreIdentity.ValidBridge(bridge) || bridge.Identities.Count != 1
                || bridge.Identities[0].PhysicalAdapterIndex != 0
                || !string.Equals(bridge.Identities[0].PnpInstanceId.Trim(), binding.Pnp, StringComparison.OrdinalIgnoreCase)) return Changed();
            if (binding.PnpDriverVersion is not null && bridge.Identities[0].DriverVersion is null) return Validation.Unavailable;
            if (!string.Equals(bridge.Identities[0].DriverVersion, binding.PnpDriverVersion, StringComparison.Ordinal)) return Changed();
            if (!WindowsNpuEngineReader.TryEngineCount(api, binding.Adapter1, out var count)) return Validation.Unavailable;
            var driver = WindowsNpuEngineReader.ReadDriver(api, binding.Adapter);
            if (binding.DriverVersion.HasValue && !driver.HasValue) return Validation.Unavailable;
            if (count != binding.EngineCount || driver != binding.DriverVersion
                || !api.IsValid(binding.Adapter)) return Changed();
        }
        token.ThrowIfCancellationRequested();
        return api.IsStale(list) ? Changed() : Validation.Current;
    }

    private unsafe NpuEngineSample? Query(nint adapter, uint engine, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            var input = new WindowsNpuDxCoreEngineIndex { PhysicalAdapterIndex = 0, EngineIndex = engine };
            ulong output = 0;
            var ticksPerSecond = frequency();
            var begin = timestamp();
            var status = api.QueryState(adapter, WindowsNpuDxCoreState.AdapterEngineRunningTimeMicroseconds,
                checked((nuint)sizeof(WindowsNpuDxCoreEngineIndex)), (nint)(&input), sizeof(ulong), (nint)(&output));
            var end = timestamp();
            var afterFrequency = frequency();
            token.ThrowIfCancellationRequested();
            return status >= 0 && ticksPerSecond > 0 && afterFrequency == ticksPerSecond && begin >= 0 && end >= begin
                ? new(output, checked((ulong)begin), checked((ulong)end), checked((ulong)ticksPerSecond)) : null;
        }
        catch (Exception exception) when (WindowsNpuEngineReader.ExpectedNativeFailure(exception)) { return null; }
    }
}
