using System.Globalization;
using System.Runtime.InteropServices;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.ReadWrite.Windows.Gpu;

namespace ResourceManager.ReadWrite.Vendors.Intel;

/// <summary>A selector bound to one live IGCL directory, not a mechanical fan identity.</summary>
public sealed class IntelGclFanIdentity(string gpuPnpInstanceId, Guid directoryId, uint fanIndex)
{
    public string GpuPnpInstanceId { get; } = gpuPnpInstanceId;
    public Guid DirectoryId { get; } = directoryId;
    public uint FanIndex { get; } = fanIndex;
    internal IntelGclFanDirectoryLease? Directory { get; init; }
    public string ExactIdentityKey => DirectoryId != Guid.Empty
        && WindowsGpuProviderIdentityResolver.TryCanonicalizePnpInstanceId(GpuPnpInstanceId, out var pnp)
            ? "windows-igcl-fan|" + pnp + "|directory:" + DirectoryId.ToString("N")
                + "|fan:" + FanIndex.ToString(CultureInfo.InvariantCulture)
            : throw new ArgumentException("A current directory and canonical physical GPU are required.");
}

public sealed record IntelGclFanReading(IntelGclFanIdentity Fan, int? Rpm);

/// <summary>One directory owns one IGCL session; catalog holders borrow its actual native resources.</summary>
public sealed class IntelGclFanDirectoryLease : IDisposable
{
    internal sealed record Binding(IntPtr Device, IntelGclIdentity Parent, string Pnp, IntPtr Fan);
    private readonly object gate = new();
    private IIntelGclSession? session;
    private readonly Dictionary<IntelGclFanIdentity, Binding> bindings = new();
    private readonly Dictionary<IntPtr, IntPtr[]> fanSets = new();
    private readonly IReadOnlyList<(IntPtr Device, IntelGclIdentity Parent, string Pnp, IntPtr[] Fans)> parents;
    private int references = 1;
    private int initialReleased;
    private System.Runtime.ExceptionServices.ExceptionDispatchInfo? closeFailure;
    internal IntelGclFanDirectoryLease(IIntelGclSession? session,
        IReadOnlyList<(IntPtr Device, IntelGclIdentity Parent, string Pnp, IntPtr[] Fans)> devices)
    {
        this.session = session;
        parents = devices.ToArray();
        var id = Guid.NewGuid();
        var identities = new List<IntelGclFanIdentity>();
        foreach (var device in devices)
        {
            fanSets.Add(device.Device, device.Fans.ToArray());
            for (var index = 0; index < device.Fans.Length; index++)
            {
                var identity = new IntelGclFanIdentity(device.Pnp, id, checked((uint)index)) { Directory = this };
                identities.Add(identity);
                bindings.Add(identity, new(device.Device, device.Parent, device.Pnp, device.Fans[index]));
            }
        }
        Identities = identities.AsReadOnly();
    }
    public IReadOnlyList<IntelGclFanIdentity> Identities { get; }
    public bool IsCurrent(WindowsGpuAdapterInventoryRead inventory, uint maximum, CancellationToken token = default)
    {
        lock (gate)
        {
            token.ThrowIfCancellationRequested();
            if (references == 0 || maximum == 0 || Identities.Count > maximum
                || inventory.Status != SamplingObservationStatus.Current || inventory.Generation == 0 || inventory.ObservedAtUtcTicks <= 0
                || inventory.ObservedCount != inventory.Adapters.Count || inventory.SkippedCount != 0 || inventory.OverflowCount != 0
                || inventory.Adapters.Count > IntelGclNativeSession.MaximumDeviceCount
                || inventory.Adapters.Count(adapter => adapter.VendorId == 0x8086) != parents.Count) return false;
            if (session is null) return parents.Count == 0;
            try
            {
                if (!session.TryGetDevices(out var devices, token) || devices.Length > IntelGclNativeSession.MaximumDeviceCount
                    || devices.Any(handle => handle == IntPtr.Zero) || devices.Distinct().Count() != devices.Length) return false;
                var identities = new List<(IntPtr Device, IntelGclIdentity Identity)>();
                foreach (var device in devices)
                {
                    token.ThrowIfCancellationRequested();
                    if (session.TryGetIdentity(device, out var identity)) identities.Add((device, identity));
                }
                foreach (var parent in parents)
                {
                    token.ThrowIfCancellationRequested();
                    var matches = identities.Where(item => item.Identity == parent.Parent).ToArray();
                    var binding = new Binding(parent.Device, parent.Parent, parent.Pnp, IntPtr.Zero);
                    if (matches.Length != 1 || matches[0].Device != parent.Device || !Matches(binding, inventory, session)
                        || !SameFans(parent.Device, maximum, session, token) || !Matches(binding, inventory, session)) return false;
                }
                token.ThrowIfCancellationRequested();
                return true;
            }
            catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or ExternalException)
            { return false; }
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
        lock (gate)
        {
            closeFailure?.Throw();
            if (Interlocked.Exchange(ref initialReleased, 1) == 0) Release();
        }
    }
    private void Release()
    {
        lock (gate)
        {
            if (--references != 0) return;
            var closing = session;
            session = null;
            try { closing?.Dispose(); }
            catch (Exception failure)
            {
                // The Native close consumed its handle before reporting failure.
                // Its effect is unknown: retain the error, never call close twice.
                closeFailure = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure);
                throw;
            }
        }
    }
    private sealed class Borrow(IntelGclFanDirectoryLease directory) : IDisposable
    {
        private readonly object releaseGate = directory.gate;
        private IntelGclFanDirectoryLease? owner = directory;
        private System.Runtime.ExceptionServices.ExceptionDispatchInfo? releaseFailure;
        public void Dispose()
        {
            lock (releaseGate)
            {
                releaseFailure?.Throw();
                var closing = Interlocked.Exchange(ref owner, null);
                try { closing?.Release(); }
                catch (Exception failure)
                {
                    releaseFailure = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure);
                    throw;
                }
            }
        }
    }
    internal IReadOnlyList<IntelGclFanReading> Read(IReadOnlyList<IntelGclFanIdentity> targets,
        WindowsGpuAdapterInventoryRead inventory, uint maximum, CancellationToken token)
    {
        var empty = targets.Select(static target => new IntelGclFanReading(target, null)).ToArray();
        lock (gate)
        {
            token.ThrowIfCancellationRequested();
            if (references == 0 || session is null || maximum == 0 || Identities.Count > maximum
                || targets.Distinct().Count() != targets.Count || targets.Any(target => !bindings.ContainsKey(target))) return empty;
            var result = empty.ToArray();
            foreach (var parent in targets.Select((target, index) => (Target: target, Index: index)).GroupBy(item => bindings[item.Target].Device))
            {
                var first = bindings[parent.First().Target];
                if (!Matches(first, inventory, session) || !SameFans(first.Device, maximum, session, token)) continue;
                var pending = new List<(int Index, int? Rpm)>();
                foreach (var selected in parent)
                {
                    token.ThrowIfCancellationRequested();
                    var binding = bindings[selected.Target];
                    int? rpm = session.TryReadFanRpm(binding.Fan, out var value) && value >= 0 ? value : null;
                    pending.Add((selected.Index, rpm));
                }
                token.ThrowIfCancellationRequested();
                if (!Matches(first, inventory, session) || !SameFans(first.Device, maximum, session, token)) continue;
                foreach (var value in pending) result[value.Index] = new(targets[value.Index], value.Rpm);
            }
            token.ThrowIfCancellationRequested();
            return result;
        }
    }
    private bool SameFans(IntPtr device, uint maximum, IIntelGclSession active, CancellationToken token)
        => active.TryGetFans(device, maximum, out var current, token)
            && current.Length == fanSets[device].Length
            && current.ToHashSet().SetEquals(fanSets[device]); // Ordering may change; the original handles remain the targets.
    private static bool Matches(Binding binding, WindowsGpuAdapterInventoryRead inventory, IIntelGclSession active)
    {
        var match = WindowsGpuProviderIdentityResolver.ResolveLuid(inventory, binding.Parent.Luid,
            binding.Parent.VendorId, binding.Parent.DeviceId);
        return match.IsCurrent
            && WindowsGpuProviderIdentityResolver.TryCanonicalizePnpInstanceId(match.Binding.PhysicalIdentity.PnpInstanceId, out var pnp)
            && pnp == binding.Pnp && active.TryGetIdentity(binding.Device, out var identity) && identity == binding.Parent;
    }
}

public sealed partial class IntelGclReader
{
    public IntelGclFanDirectoryLease? OpenFanDirectory(WindowsGpuAdapterInventoryRead inventory,
        uint effectiveMaximumInstanceCount, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (effectiveMaximumInstanceCount == 0) return new(null, []);
        if (inventory.Status != SamplingObservationStatus.Current || inventory.Generation == 0 || inventory.ObservedAtUtcTicks <= 0
            || inventory.ObservedCount != inventory.Adapters.Count || inventory.SkippedCount != 0 || inventory.OverflowCount != 0
            || inventory.Adapters.Count > IntelGclNativeSession.MaximumDeviceCount) return null;
        var selected = inventory.Adapters.Where(static adapter => adapter.VendorId == 0x8086).ToArray();
        if (selected.Length == 0) return new(null, []);
        var parents = new Dictionary<IntelGclIdentity, string>();
        foreach (var adapter in selected)
        {
            var match = WindowsGpuProviderIdentityResolver.ResolveLuid(inventory, Pack(adapter.Luid), adapter.VendorId, adapter.DeviceId);
            if (!match.IsCurrent || !WindowsGpuProviderIdentityResolver.TryCanonicalizePnpInstanceId(match.Binding.PhysicalIdentity.PnpInstanceId, out var pnp)
                || !parents.TryAdd(new(Pack(adapter.Luid), adapter.VendorId, adapter.DeviceId), pnp)
                || parents.Values.Count(value => value == pnp) != 1) return null;
        }
        IIntelGclSession? session = null;
        try
        {
            session = open();
            cancellationToken.ThrowIfCancellationRequested();
            if (session is null || !session.TryGetDevices(out var devices, cancellationToken)
                || devices.Length > IntelGclNativeSession.MaximumDeviceCount || devices.Any(static handle => handle == IntPtr.Zero)
                || devices.Distinct().Count() != devices.Length) return null;
            var sdk = new List<(IntPtr Device, IntelGclIdentity Identity)>();
            foreach (var device in devices)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (session.TryGetIdentity(device, out var identity)) sdk.Add((device, identity));
            }
            var allFans = new HashSet<IntPtr>();
            var entries = new List<(IntPtr Device, IntelGclIdentity Parent, string Pnp, IntPtr[] Fans)>();
            foreach (var parent in parents)
            {
                var matches = sdk.Where(row => row.Identity == parent.Key).ToArray();
                if (matches.Length != 1 || !session.TryGetFans(matches[0].Device, effectiveMaximumInstanceCount, out var fans, cancellationToken)
                    || fans.Any(static handle => handle == IntPtr.Zero) || fans.Distinct().Count() != fans.Length
                    || fans.Any(handle => !allFans.Add(handle)) || allFans.Count > effectiveMaximumInstanceCount) return null;
                entries.Add((matches[0].Device, parent.Key, parent.Value, fans));
            }
            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!session.TryGetIdentity(entry.Device, out var after) || after != entry.Parent
                    || !session.TryGetFans(entry.Device, effectiveMaximumInstanceCount, out var afterFans, cancellationToken)
                    || afterFans.Length != entry.Fans.Length || !afterFans.ToHashSet().SetEquals(entry.Fans)) return null;
            }
            cancellationToken.ThrowIfCancellationRequested();
            var directory = new IntelGclFanDirectoryLease(session, entries);
            session = null;
            return directory;
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or ExternalException)
        { return null; }
        finally { session?.Dispose(); }
    }
    public IReadOnlyList<IntelGclFanReading> ReadFans(IReadOnlyList<IntelGclFanIdentity> targets,
        WindowsGpuAdapterInventoryRead inventory, uint effectiveMaximumInstanceCount, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (targets.Count == 0) return [];
        var directory = targets[0].Directory;
        if (directory is null || targets.Any(target => !ReferenceEquals(target.Directory, directory)))
            return targets.Select(static target => new IntelGclFanReading(target, null)).ToArray();
        try { return directory.Read(targets, inventory, effectiveMaximumInstanceCount, cancellationToken); }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or ExternalException)
        { return targets.Select(static target => new IntelGclFanReading(target, null)).ToArray(); }
    }
}