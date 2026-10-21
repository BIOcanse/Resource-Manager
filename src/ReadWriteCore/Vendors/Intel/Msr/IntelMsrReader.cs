using ResourceManager.ReadWrite.Native;
using ResourceManager.ReadWrite.Windows.Cpu.Models;
using System.ComponentModel;
using System.Runtime.InteropServices;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.ReadWrite.Vendors.Intel;
using ResourceManager.ReadWrite.Windows.Cpu;

namespace ResourceManager.ReadWrite.Vendors.Intel.Msr;

public sealed record IntelMsrMetricRead(string DeviceIdentity, IReadOnlyDictionary<MetricFieldCode, double> Values,
    bool RequiresTopologyRefresh = false)
{
    public IReadOnlyDictionary<MetricFieldCode, bool> BooleanValues { get; init; } = new Dictionary<MetricFieldCode, bool>();
    public IReadOnlyDictionary<MetricFieldCode, NativeMetricSnapshotCounterPair> CounterPairs { get; init; } = new Dictionary<MetricFieldCode, NativeMetricSnapshotCounterPair>();
    public IReadOnlyDictionary<MetricFieldCode, ulong> UnsignedValues { get; init; } = new Dictionary<MetricFieldCode, ulong>();
}

public sealed class IntelMsrReader
{
    private readonly Func<IIntelCpuHardware> open;
    private readonly Func<ICpuThreadCpuidAccess> createAccess;

    /// <summary>Creates a reader without opening a driver or changing affinity. The selected read owns its session.</summary>
    public IntelMsrReader(Func<IIntelCpuHardware> open) : this(open, WindowsCpuThreadCpuidReader.CreateNativeAccess) { }
    internal IntelMsrReader(Func<IIntelCpuHardware> open, Func<ICpuThreadCpuidAccess> createAccess)
    { this.open = open; this.createAccess = createAccess; }
    public IReadOnlyList<IntelMsrMetricRead> Read(IReadOnlyList<IntelMsrCatalogIdentity> devices,
        IReadOnlyDictionary<string, IReadOnlySet<MetricFieldCode>> requested)
    {
        var selected = devices.Where(device => requested.TryGetValue(device.ExactIdentityKey, out var fields)
            && fields.Any(device.AppliesTo)).ToArray();
        if (selected.Length == 0) return [];
        var results = new List<IntelMsrMetricRead>();
        Exception? failure = null;
        var worker = new Thread(() =>
        {
            IIntelCpuHardware? hardware = null;
            try
            {
                var access = createAccess();
                foreach (var device in selected)
                {
                    var values = new Dictionary<MetricFieldCode, double>();
                    var unsigned = new Dictionary<MetricFieldCode, ulong>();
                    var pairs = new Dictionary<MetricFieldCode, NativeMetricSnapshotCounterPair>();
                    var booleans = new Dictionary<MetricFieldCode, bool>();
                    var changed = false;
                    try
                    {
                        if (!access.Select(device.Processor) || access.Current() != device.Processor)
                        { results.Add(new(device.ExactIdentityKey, values)); continue; }
                        var current = WindowsCpuThreadCpuidReader.ReadRegisters(access.Read);
                        var fields = requested[device.ExactIdentityKey].Where(device.AppliesTo).ToHashSet();
                        var feedbackRequested = fields.Any(static field => field is MetricFieldCode.IntelAperfCounter or MetricFieldCode.IntelMperfCounter or MetricFieldCode.IntelPerformanceRatio);
                        var statusRequested = fields.Any(static field => IntelThermalStatusCatalog.Find(field) is not null);
                        var psysRequested = fields.Any(IntelPsysFieldCatalog.Contains);
                        var thermalRequested = fields.Any(static field => IntelRaplFieldCatalog.Find(field) is null && IntelRaplPowerInformation.Find(field) is null
                            && IntelThermalStatusCatalog.Find(field) is null
                            && !IntelPsysFieldCatalog.Contains(field)
                            && field is not (MetricFieldCode.IntelAperfCounter or MetricFieldCode.IntelMperfCounter or MetricFieldCode.IntelPerformanceRatio));
                        var raplRequested = fields.Any(static field => IntelRaplFieldCatalog.Find(field) is not null || IntelRaplPowerInformation.Find(field) is not null);
                        if (current.Identity.Vendor != "GenuineIntel" || current.Signature != device.Signature
                            || current.IntelX2ApicId != device.X2ApicId
                            || (thermalRequested && ((device.IsPackage ? current.IntelPackageThermalSupported : current.IntelDtsSupported) != true
                                || (!device.IsPackage && IntelThermalTargetModels.UsesModuleDts(current.Identity))
                                || (device.SupportsTemperatureTarget && !IntelThermalTargetModels.Supports(current.Identity))))
                            || (raplRequested && IntelRaplModels.Get(current.Identity, device.Kind) != device.RaplProfile)
                            || (psysRequested && (!device.IsPlatform || !IntelPsysModels.Supports(current.Identity)))
                            || (feedbackRequested && current.IntelPerformanceFeedbackSupported != true)
                            || (statusRequested && IntelThermalStatusCatalog.Capabilities(current, device.Kind) != device.ThermalStatusCapabilities))
                        { results.Add(new(device.ExactIdentityKey, values, true)); continue; }
                        if (access.Current() != device.Processor)
                        { results.Add(new(device.ExactIdentityKey, values, true)); continue; }
                        try { hardware ??= open(); }
                        catch (Exception exception) { failure = exception; break; }
                        var targetCode = device.IsPackage ? MetricFieldCode.IntelPackageTemperatureTarget : MetricFieldCode.IntelCoreTemperatureTarget;
                        var temperatureCode = device.IsPackage ? MetricFieldCode.IntelPackageDtsTemperature : MetricFieldCode.IntelCoreDtsTemperature;
                        var distanceCode = device.IsPackage ? MetricFieldCode.IntelPackageDtsDistance : MetricFieldCode.IntelCoreDtsDistance;
                        var statusRead = false; ulong? statusValue = null;
                        ulong? ReadStatus()
                        {
                            if (!statusRead) { statusValue = ReadMsr(hardware, device.IsPackage ? 0x1B1U : 0x19CU); statusRead = true; }
                            return statusValue;
                        }
                        double? target = null, distance = null;
                        if (device.SupportsTemperatureTarget && (fields.Contains(targetCode) || fields.Contains(temperatureCode)))
                        {
                            var raw = ReadMsr(hardware, 0x1A2);
                            if (raw.HasValue && ((raw.Value >> 16) & 255) is var value && value != 0) target = value;
                        }
                        if (fields.Contains(distanceCode) || fields.Contains(temperatureCode))
                        {
                            var raw = ReadStatus();
                            if (raw.HasValue && (device.IsPackage || (raw.Value & (1UL << 31)) != 0))
                                distance = (raw.Value >> 16) & 127;
                        }
                        if (fields.Contains(targetCode) && target.HasValue) values[targetCode] = target.Value;
                        if (fields.Contains(distanceCode) && distance.HasValue) values[distanceCode] = distance.Value;
                        if (fields.Contains(temperatureCode) && target.HasValue && distance.HasValue)
                            values[temperatureCode] = target.Value - distance.Value;
                        if (statusRequested && ReadStatus() is { } rawStatus)
                            IntelThermalStatusCatalog.Decode(device.IsPackage, rawStatus, fields, unsigned, booleans);
                        // Power and energy units share 606; retain only this batch's one read.
                        var unitsRead = false; ulong? unitsValue = null;
                        ulong? ReadRapl(IIntelCpuHardware selectedHardware, uint register)
                        {
                            if (register != IntelRaplEncoding.UnitsRegister) return ReadMsr(selectedHardware, register);
                            if (!unitsRead) { unitsValue = ReadMsr(selectedHardware, register); unitsRead = true; }
                            return unitsValue;
                        }
                        IntelRaplFieldCatalog.Read(hardware, device.RaplProfile, fields, values, unsigned, ReadRapl);
                        IntelRaplPowerInformation.Read(hardware, device.RaplProfile, fields, values, unsigned, ReadRapl);
                        if (psysRequested) IntelPsysFieldCatalog.Read(hardware, fields, values, unsigned, ReadRapl);
                        var ratioRequested = fields.Contains(MetricFieldCode.IntelPerformanceRatio);
                        var mperf = fields.Contains(MetricFieldCode.IntelMperfCounter) || ratioRequested ? ReadMsr(hardware, 0xE7) : null;
                        var aperf = fields.Contains(MetricFieldCode.IntelAperfCounter) || ratioRequested ? ReadMsr(hardware, 0xE8) : null;
                        if (fields.Contains(MetricFieldCode.IntelMperfCounter) && mperf.HasValue) unsigned[MetricFieldCode.IntelMperfCounter] = mperf.Value;
                        if (fields.Contains(MetricFieldCode.IntelAperfCounter) && aperf.HasValue) unsigned[MetricFieldCode.IntelAperfCounter] = aperf.Value;
                        if (ratioRequested && aperf.HasValue && mperf.HasValue) pairs[MetricFieldCode.IntelPerformanceRatio] = new(aperf.Value, mperf.Value);
                        if (access.Current() != device.Processor) { values.Clear(); unsigned.Clear(); pairs.Clear(); booleans.Clear(); changed = true; }
                    }
                    catch (Exception exception) when (Expected(exception)) { values.Clear(); unsigned.Clear(); pairs.Clear(); booleans.Clear(); }
                    results.Add(new(device.ExactIdentityKey, values, changed) { UnsignedValues = unsigned, CounterPairs = pairs, BooleanValues = booleans });
                }
            }
            catch (Exception exception) { failure = exception; }
            finally
            {
                try { hardware?.Dispose(); }
                catch (Exception exception) { failure = exception; }
            }
        }) { IsBackground = true, Name = "Resource Manager Intel MSR capture" };
        try { worker.Start(); worker.Join(); }
        catch (Exception exception) when (exception is ThreadStateException or OutOfMemoryException) { failure = exception; }
        return failure is null ? results : selected.Select(static device => new IntelMsrMetricRead(device.ExactIdentityKey,
            new Dictionary<MetricFieldCode, double>())).ToArray();
    }

    private static ulong? ReadMsr(IIntelCpuHardware hardware, uint register)
    {
        try { return hardware.ReadMsr(register); }
        catch (Exception exception) when (Expected(exception)) { return null; }
    }
    private static bool Expected(Exception exception) => exception is IOException or Win32Exception
        or UnauthorizedAccessException or InvalidOperationException or NotSupportedException or ExternalException;
}
