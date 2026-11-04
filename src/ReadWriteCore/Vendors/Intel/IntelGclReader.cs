using ResourceManager.ReadWrite.Windows.Gpu;
using ResourceManager.ReadWrite.Native;
using System.Runtime.InteropServices;
using ResourceManager.App.Domain.Metrics;

namespace ResourceManager.ReadWrite.Vendors.Intel;

public sealed partial class IntelGclReader
{
    private readonly Func<IIntelGclSession?> open;
    public IntelGclReader() : this(IntelGclNativeSession.Create) { }
    internal IntelGclReader(Func<IIntelGclSession?> open) => this.open = open;
    public IReadOnlyList<IntelGclMetricRead> Read(WindowsGpuAdapterInventoryRead inventory,
        IReadOnlyDictionary<int, IReadOnlySet<MetricFieldCode>> requested, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var selected = inventory.Adapters.Where(adapter => adapter.VendorId == 0x8086
            && requested.TryGetValue(adapter.Index, out var codes) && codes.Any(IsSupportedField)
            && WindowsGpuProviderIdentityResolver.ResolveLuid(inventory, Pack(adapter.Luid), adapter.VendorId, adapter.DeviceId).IsCurrent).ToArray();
        if (selected.Length == 0) return [];
        try
        {
            using var session = open();
            cancellationToken.ThrowIfCancellationRequested();
            if (session is null || !session.TryGetDevices(out var devices, cancellationToken)) return [];
            var identities = new List<(IntPtr Handle, IntelGclIdentity Identity)>();
            foreach (var handle in devices)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (session.TryGetIdentity(handle, out var identity)) identities.Add((handle, identity));
            }
            var result = new List<IntelGclMetricRead>(selected.Length);
            foreach (var adapter in selected)
            {
                var expected = new IntelGclIdentity(Pack(adapter.Luid), adapter.VendorId, adapter.DeviceId);
                var matches = identities.Where(candidate => candidate.Identity == expected).ToArray();
                if (matches.Length != 1) continue;
                var handle = matches[0].Handle;
                cancellationToken.ThrowIfCancellationRequested();
                var codes = requested[adapter.Index];
                IntelGclMetricRead? read = null;
                if (codes.Any(IsTelemetryField) && session.TryReadTelemetry(handle, out var telemetry))
                    read = Decode(adapter.Index, telemetry, codes);
                cancellationToken.ThrowIfCancellationRequested();
                if (codes.Any(IsPciStateField) && session.TryReadPciState(handle, out var state))
                    read = DecodePci(adapter.Index, state, codes, read);
                cancellationToken.ThrowIfCancellationRequested();
                if (codes.Any(IsPciPropertiesField) && session.TryReadPciProperties(handle, out var properties))
                    read = DecodePciProperties(adapter.Index, properties, codes, read);
                cancellationToken.ThrowIfCancellationRequested();
                if (read is null) continue;
                if (!session.TryGetIdentity(handle, out var after) || after != expected) continue;
                result.Add(read);
            }
            return result;
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or ExternalException)
        {
            return [];
        }
    }

    private static ulong Pack(AdapterLuid luid) => ((ulong)(uint)luid.HighPart << 32) | luid.LowPart;

    internal static double? ReadNumber(IntelGclTelemetryItem item, IntelGclUnit unit, bool temperature = false)
    {
        if (item.Supported != 1 || item.Unit != unit) return null;
        // Integer-to-double publication is bounded to the range with guaranteed integer precision.
        double value = item.Type switch
        {
            IntelGclDataType.Int8 => item.Int8, IntelGclDataType.UInt8 => item.UInt8,
            IntelGclDataType.Int16 => item.Int16, IntelGclDataType.UInt16 => item.UInt16,
            IntelGclDataType.Int32 => item.Int32, IntelGclDataType.UInt32 => item.UInt32,
            IntelGclDataType.Int64 when item.Int64 is >= -9007199254740992L and <= 9007199254740992L => item.Int64,
            IntelGclDataType.UInt64 when item.UInt64 <= 9007199254740992UL => item.UInt64,
            IntelGclDataType.Float => item.Float, IntelGclDataType.Double => item.Double,
            _ => double.NaN
        };
        return double.IsFinite(value) && value >= (temperature ? -273.15 : 0) ? value : null;
    }

    internal static ulong? ReadCounter(IntelGclTelemetryItem item)
    {
        if (item.Supported != 1 || item.Unit != IntelGclUnit.Bytes) return null;
        return item.Type switch
        {
            IntelGclDataType.UInt8 => item.UInt8, IntelGclDataType.UInt16 => item.UInt16,
            IntelGclDataType.UInt32 => item.UInt32, IntelGclDataType.UInt64 => item.UInt64,
            IntelGclDataType.Int8 when item.Int8 >= 0 => (ulong)item.Int8,
            IntelGclDataType.Int16 when item.Int16 >= 0 => (ulong)item.Int16,
            IntelGclDataType.Int32 when item.Int32 >= 0 => (ulong)item.Int32,
            IntelGclDataType.Int64 when item.Int64 >= 0 => (ulong)item.Int64,
            // Float counters cannot prove full native integer precision.
            _ => null
        };
    }

    private static bool? ReadFlag(byte flag) => flag switch { 0 => false, 1 => true, _ => null };
}

public sealed record IntelGclMetricRead(int Index, IReadOnlyDictionary<MetricFieldCode, double?> Values,
    IReadOnlyDictionary<MetricFieldCode, ulong?> Counters, IReadOnlyDictionary<MetricFieldCode, bool?> Flags)
{
    public IReadOnlyDictionary<MetricFieldCode, NativeMetricSnapshotFloatingCounterPair?> FloatingCounters { get; init; }
        = new Dictionary<MetricFieldCode, NativeMetricSnapshotFloatingCounterPair?>();
}
