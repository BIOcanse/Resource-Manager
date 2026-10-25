using System.Security.Cryptography;
using System.Text;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.ReadWrite.Native;
using ResourceManager.ReadWrite.Windows.Cpu.Models;

namespace ResourceManager.ReadWrite.Vendors.Amd.Smu;

public sealed record AmdCcdCpuTarget(string PackageId, uint Family, uint Model,
    uint Signature, IReadOnlyList<CpuProcessorAddress> ProcessorAddresses)
{
    public string BindingKey => $"{PackageId}|{Family:X}:{Model:X}:{Signature:X8}|{string.Join(',', ProcessorAddresses.Select(static p => $"{p.ProcessorGroup}:{p.GroupRelativeIndex}"))}";
}

public sealed record AmdCcdCatalogIdentity(AmdCcdCpuTarget Target,
    int SensorIndex, uint SmnAddress)
{
    public int DisplayIndex => SensorIndex;
    public string ExactIdentityKey => $"amd-smn-ccd|{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Target.BindingKey)))}|{SmnAddress:X8}";
    public string BindingKey => ExactIdentityKey;
    // An SMN sensor is not a verified OS die/core mapping. Do not attach guessed CPU members.
    public MetricDeviceDescriptor Device => new("cpu_die",
        MetricDeviceAddress.CpuDie(ExactIdentityKey, []) with { PackageId = Target.PackageId, SensorIdentifier = $"amd.smn:{SmnAddress:X8}" },
        null, SensorIndex);
}

public sealed record AmdCcdTopologyRead(NativeMetricSnapshotCatalogTopologyStatus Status,
    IReadOnlyList<AmdCcdCatalogIdentity> Identities);

/// <summary>One existing-provider resource, shared by directory discovery and
/// requested values. Construction opens no hardware; no independent timer or sampler.</summary>
public sealed class AmdCcdMetricResource : IDisposable
{
    private readonly object gate = new();
    private readonly Func<AmdCcdCpuTarget, AmdCcdTemperatureReader> createReader;
    private readonly Func<bool>? isAvailable;
    private AmdCcdCpuTarget? currentTarget;
    private AmdCcdTemperatureReader? reader;
    private bool disposed;
    private HashSet<int> discoveredSlots = [];

    public AmdCcdMetricResource(Func<AmdCcdCpuTarget, AmdCcdTemperatureReader> createReader,
        Func<bool>? isAvailable = null)
    {
        ArgumentNullException.ThrowIfNull(createReader);
        this.createReader = createReader;
        this.isAvailable = isAvailable;
    }

    public AmdCcdTopologyRead Discover(CpuOsTopologyInformation? topology, uint maximumInstanceCount)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (maximumInstanceCount == 0) { Reset(); return new(NativeMetricSnapshotCatalogTopologyStatus.Complete, []); }
            if (maximumInstanceCount > 16) throw new ArgumentOutOfRangeException(nameof(maximumInstanceCount));
            var (status, target) = ResolveTarget(topology);
            if (target is null) { Reset(); return new(status, []); }
            if (isAvailable?.Invoke() == false)
            {
                Reset();
                return new(NativeMetricSnapshotCatalogTopologyStatus.Unavailable, []);
            }
            if (reader is null || currentTarget?.BindingKey != target.BindingKey)
            {
                Reset();
                reader = createReader(target);
                currentTarget = target;
            }
            var sensors = reader.Discover();
            if ((uint)sensors.Count > maximumInstanceCount)
                throw new InvalidOperationException("AMD CCD directory exceeds the configured instance capacity.");
            discoveredSlots = sensors.Select(static sensor => sensor.SensorIndex).ToHashSet();
            return new(NativeMetricSnapshotCatalogTopologyStatus.Complete,
                sensors.Select(sensor => new AmdCcdCatalogIdentity(target, sensor.SensorIndex, sensor.SmnAddress)).ToArray());
        }
    }

    public double? Read(AmdCcdCatalogIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (reader is null || currentTarget is not { } target || target.BindingKey != identity.Target.BindingKey)
                throw new InvalidOperationException("AMD CCD target is not the current discovered package.");
            var format = AmdCcdTemperatureFormat.Resolve(target.Family, target.Model)!;
            if (!discoveredSlots.Contains(identity.SensorIndex) || format.RegisterAddress(identity.SensorIndex) != identity.SmnAddress)
                throw new InvalidOperationException("AMD CCD address does not match the discovered sensor slot.");
            return reader.Read(identity.SensorIndex).TemperatureCelsius;
        }
    }

    internal static (NativeMetricSnapshotCatalogTopologyStatus Status, AmdCcdCpuTarget? Target)
        ResolveTarget(CpuOsTopologyInformation? topology)
    {
        var unavailable = (NativeMetricSnapshotCatalogTopologyStatus.Unavailable, (AmdCcdCpuTarget?)null);
        if (topology?.Packages is not { Count: 1 } packages
            || string.IsNullOrWhiteSpace(packages[0].DeviceIdentity)
            || topology.ThreadCpuid is not { Count: > 0 } threads) return unavailable;
        var ids = packages[0].LogicalProcessorIds.Order().ToArray();
        if (ids.Length == 0 || ids.Any(static id => id < 0 || id > ushort.MaxValue * 64 + 63)
            || ids.Distinct().Count() != ids.Length
            || !ids.SequenceEqual(threads.Select(static thread => thread.LogicalProcessorId).Order())) return unavailable;
        var first = threads[0].Data;
        if (first is not { Signature: not null }) return unavailable;
        if (threads.Any(thread => thread.Data is not { Signature: not null } data
            || data.Identity.Vendor != first.Identity.Vendor || data.Signature != first.Signature
            || data.Identity.Family != first.Identity.Family || data.Identity.Model != first.Identity.Model)) return unavailable;
        if (first.Identity.Vendor != "AuthenticAMD")
            return (NativeMetricSnapshotCatalogTopologyStatus.Complete, null);
        if (first.Identity.Family < 0 || first.Identity.Model < 0
            || AmdCcdTemperatureFormat.Resolve((uint)first.Identity.Family, (uint)first.Identity.Model) is null) return unavailable;
        return (NativeMetricSnapshotCatalogTopologyStatus.Complete,
            new(packages[0].DeviceIdentity, (uint)first.Identity.Family, (uint)first.Identity.Model,
                first.Signature.Value, Array.AsReadOnly(ids.Select(static id =>
                    new CpuProcessorAddress((ushort)(id / 64), (byte)(id % 64))).ToArray())));
    }

    private void Reset()
    {
        var owned = reader; reader = null; currentTarget = null; discoveredSlots.Clear();
        owned?.Dispose();
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            Reset();
        }
    }
}
