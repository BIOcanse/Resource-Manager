using ResourceManager.ReadWrite.Windows.Gpu;
using ResourceManager.App.Application.Control;
using ResourceManager.App.Domain.Control;
using ResourceManager.ReadWrite.Vendors.Nvidia;
using ResourceManager.App.Infrastructure.Monitoring;
namespace ResourceManager.App.Infrastructure.Control.Writers;

/// <summary>Product control IDs/policy/copy adapter over the single original SDK NVIDIA resource.</summary>
public sealed class NvidiaGpuControlWriter : IControlWriter, IControlDetectionCache, IDisposable
{
    private readonly WindowsGpuAdapterOrderReader adapterReader = new();
    internal NvidiaGpuControlResource Resource { get; }
    public NvidiaGpuControlWriter(ILogger<NvidiaGpuControlWriter>? logger = null)
        => Resource = new(describe: NvidiaGpuControlCopy.Describe,
            warning: logger is null ? null : (message, arguments) => logger.LogWarning("{ProviderCode} {Arguments}", message, arguments));
    internal NvidiaGpuControlWriter(NvidiaGpuControlResource resource) => Resource = resource;
    internal const string PowerLimitCapabilityId = "gpu.power-limit";
    internal const string CoreClockMinimumCapabilityId = "gpu.core-clock-minimum";
    internal const string CoreClockMaximumCapabilityId = "gpu.core-clock-maximum";
    internal const string MemoryClockMinimumCapabilityId = "gpu.memory-clock-minimum";
    internal const string MemoryClockMaximumCapabilityId = "gpu.memory-clock-maximum";
    internal const string TemperatureLimitCapabilityId = "gpu.temperature-limit";
    internal const string SlowdownTemperatureCapabilityId = "gpu.slowdown-temperature";
    internal const string ShutdownTemperatureCapabilityId = "gpu.shutdown-temperature";
    internal const string CoreClockOffsetCapabilityId = "gpu.core-clock-offset";
    internal const string MemoryClockOffsetCapabilityId = "gpu.memory-clock-offset";
    private static readonly (string Minimum, string Maximum)[] ClockPairs =
    [ (CoreClockMinimumCapabilityId, CoreClockMaximumCapabilityId), (MemoryClockMinimumCapabilityId, MemoryClockMaximumCapabilityId) ];

    public IReadOnlyDictionary<string, ControlApplyReason> ValidateSettings(ControlObject target, IReadOnlyList<ControlSetting> settings)
    {
        var errors = new Dictionary<string, ControlApplyReason>(StringComparer.Ordinal);
        if (!Target(target).IsDiscreteNvidia) return errors;
        foreach (var pair in ClockPairs)
        {
            var minimum = settings.FirstOrDefault(setting => setting.CapabilityId == pair.Minimum);
            var maximum = settings.FirstOrDefault(setting => setting.CapabilityId == pair.Maximum);
            if (ClockMhz(minimum) is not { } low || ClockMhz(maximum) is not { } high) continue;
            if (high == 0 || low <= high) continue;
            var reason = new ControlApplyReason("nvidia.M36", new(Minimum: low, Maximum: high, Unit: ControlUnits.Megahertz));
            errors[pair.Minimum] = reason;
            errors[pair.Maximum] = reason;
        }
        return errors;
    }

    public IReadOnlyList<ControlSetting> OrderSettings(ControlObject target, IReadOnlyList<ControlSetting> settings)
    {
        if (!Target(target).IsDiscreteNvidia) return settings;
        var ordered = settings.ToArray();
        foreach (var pair in ClockPairs)
        {
            var minimum = Array.FindIndex(ordered, setting => setting.CapabilityId == pair.Minimum);
            var maximum = Array.FindIndex(ordered, setting => setting.CapabilityId == pair.Maximum);
            if (minimum < 0 || maximum < 0 || ClockMhz(ordered[minimum]) is not { } low) continue;
            var maximumFirst = Resource.ShouldWriteClockMaximumFirst(Target(target), pair.Minimum, low);
            if (maximumFirst ? maximum > minimum : minimum > maximum)
                (ordered[minimum], ordered[maximum]) = (ordered[maximum], ordered[minimum]);
        }
        return ordered;
    }

    private static double? ClockMhz(ControlSetting? setting)
        => setting?.Number is { } number && double.IsFinite(number) && number >= 0
            ? ControlUnitAdapter.Convert(number, setting.Unit, ControlUnits.Megahertz) : null;
    public void ResetDetection() => Resource.ResetDetection();
    public void ForgetObject(string objectId) => Resource.ForgetVfCurveOwnership(objectId);
    public void Dispose() => Resource.Dispose();
    internal void RecordWriteResult(string objectId, string capabilityId, int code) => Resource.RecordWriteResult(objectId, capabilityId, code);
    internal bool HasUnsupportedWrite(string objectId, string capabilityId) => Resource.HasUnsupportedWrite(objectId, capabilityId);
    internal int WriteClockRange(int index, uint type, bool minimum, uint wanted, uint maximum,
        Func<uint, uint, int> write, out uint low, out uint high)
        => Resource.WriteClockRange(index, type, minimum, wanted, maximum, write, out low, out high);
    public string? ChannelOf(ControlObject target, string capabilityId) => capabilityId == GpuVfCurveCapabilities.Core
        ? Target(target).IsDiscreteNvidia ? ControlChannels.Nvapi : null
        : Resource.ChannelOf(Target(target), capabilityId) switch
    { NativeMetricSnapshotSources.NvidiaNvml => ControlChannels.Nvml, NativeMetricSnapshotSources.NvidiaNvapi => ControlChannels.Nvapi, _ => null };
    public ControlWriteAvailability Probe(ControlObject target, ControlCapability capability)
    {
        if (capability.Id == GpuVfCurveCapabilities.Core)
        {
            if (!Target(target).IsDiscreteNvidia) return ControlWriteAvailability.NotMine;
            if (Resource.IsVfCurveSimulation) return ControlWriteAvailability.Yes();
            // Get success cannot establish write permission or a verified restoration protocol.
            return ControlWriteAvailability.No(NvidiaGpuVfCurveAbis.LiveWriteUnavailable,
                kind: ControlUnavailableKinds.NotImplemented, canRead: true);
        }
        var value = Resource.Probe(Target(target), Capability(capability));
        return new(value.CanWrite, value.Reason, Range(value.Range), value.UnavailableKind, value.CanRead);
    }
    public async Task<ControlApplyOutcome> WriteAsync(ControlObject target, ControlCapability capability, ControlSetting setting, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (capability.Id == GpuVfCurveCapabilities.Core)
        {
            if (setting.GpuVfCurveExecution is not { } execution || setting.GpuVfCurvePresetId is null
                || setting.Number is not null || setting.Toggle is not null || setting.Curve is not null)
                return new(target.Id, capability.Id, ControlApplyStatuses.Failed, "gpu-vf-curve-execution-missing");
            var read = ReadVfCurve(target);
            if (read.Snapshot is not { } snapshot)
                return new(target.Id, capability.Id, ControlApplyStatuses.Failed, read.UnavailableReason ?? "nvidia-vf-snapshot-unavailable");
            var signature = GpuVfCurveBinding.ComputePointSignature(snapshot.Points.Select(point => (point.PointId, (long)point.VoltageMicrovolts)));
            var observedBinding = new GpuVfCurveBinding(ControlVendors.Nvidia, target.Id, target.ReportedName ?? string.Empty,
                signature, GpuVfCurveBindingScopes.Device);
            if (!execution.Binding.Matches(observedBinding))
                return new(target.Id, capability.Id, ControlApplyStatuses.Failed, "gpu-vf-curve-binding-mismatch");
            var points = new List<NvidiaGpuVfCurveOffset>(execution.Points.Count);
            foreach (var point in execution.Points)
            {
                if (point.VoltageMicrovolts is < uint.MinValue or > uint.MaxValue
                    || !double.IsFinite(point.FrequencyOffsetKhz) || point.FrequencyOffsetKhz != Math.Truncate(point.FrequencyOffsetKhz)
                    || point.FrequencyOffsetKhz is < int.MinValue or > int.MaxValue)
                    return new(target.Id, capability.Id, ControlApplyStatuses.Failed, "gpu-vf-curve-point-invalid");
                points.Add(new(point.PointId, (uint)point.VoltageMicrovolts, (int)point.FrequencyOffsetKhz));
            }
            var transaction = Resource.WriteVfCurve(Target(target), snapshot, points, VfOptions());
            return new(target.Id, capability.Id, transaction.Applied ? ControlApplyStatuses.Applied : ControlApplyStatuses.Failed,
                transaction.FailureReason is { } reason ? new(reason) : null)
            { Evidence = transaction.Applied ? new(ResourceManager.ReadWrite.FieldWriteEvidence.SettingReadbackTolerance) : null };
        }
        var executionTarget = Target(target);
        if (target.AdapterKey is > 0 && capability.Id == PowerLimitCapabilityId)
            executionTarget = executionTarget with { Inventory = adapterReader.ReadInventory() };
        var result = await Resource.WriteAsync(executionTarget, Capability(capability),
            new(setting.CapabilityId, setting.Number, setting.Unit), token);
        return new(result.ObjectId, result.CapabilityId, result.Status,
            new ControlApplyReason(result.Message ?? "nvidia.WriteAccepted", new(
                Minimum: result.Minimum, Maximum: result.Maximum, Unit: result.Unit,
                Requested: result.Requested, Observed: result.Observed)))
        { NativeStatus = result.NativeStatus };
    }
    public async Task<ControlActualValue?> ReadAsync(ControlObject target, ControlCapability capability, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (capability.Id == GpuVfCurveCapabilities.Core)
        {
            // The complete table is consumed by the explicit descriptor GET. The generic
            // actual-value schema has no V/F payload; do not perform an unconsumed live read here.
            return null;
        }
        var value = await Resource.ReadAsync(Target(target), Capability(capability), token);
        return value is null ? null : new(value.ObjectId, value.CapabilityId, Number: value.Number, Unit: value.Unit, UnreadableReason: value.UnreadableReason);
    }
    public Task<bool> RestoreAsync(ControlObject target, ControlCapability capability, IReadOnlySet<string> configured,
        CancellationToken token, ControlSetting? previousSetting = null)
    {
        token.ThrowIfCancellationRequested();
        if (capability.Id == GpuVfCurveCapabilities.Core)
            return Task.FromResult(!Resource.HasVfCurveOwnership(Target(target))
                || Resource.RestoreVfCurve(Target(target), VfOptions()).Applied);
        // Compatibility only: persisted pending releases predate the single curve capability.
        // The SDK verifies the source-defined neutral scalar delta by readback, without inventing a range.
        if (capability.Id == GpuVfCurveScalarAdapter.LegacyCapabilityId && capability.Range is null)
            return Resource.RestoreLegacyCoreOffsetAsync(Target(target), VfOptions(), token);
        var executionTarget = Target(target);
        if (target.AdapterKey is > 0 && capability.Id == PowerLimitCapabilityId)
            executionTarget = executionTarget with { Inventory = adapterReader.ReadInventory() };
        return Resource.RestoreAsync(executionTarget, Capability(capability), configured, token,
            previousSetting is null ? null : new(previousSetting.CapabilityId, previousSetting.Number, previousSetting.Unit));
    }
    private static NvidiaControlObject Target(ControlObject target) => new(target.Id, target.AdapterIndex, target.AdapterKey,
        target.Kind == ControlObjectKinds.Gpu && target.Platform.Vendor == ControlVendors.Nvidia && target.GpuAttachment == ControlGpuAttachments.Discrete);
    internal NvidiaGpuVfCurveReadResult ReadVfCurve(ControlObject target)
    {
        var executionTarget = Target(target);
        if (target.AdapterKey is > 0) executionTarget = executionTarget with { Inventory = adapterReader.ReadInventory() };
        return Resource.ReadVfCurve(executionTarget);
    }
    private static NvidiaGpuVfCurveTransactionOptions VfOptions()
        => new(checked((int)GpuVfCurveSettings.ProductDefaults.ReadbackToleranceKhz));
    private static NvidiaControlCapability Capability(ControlCapability capability) => new(capability.Id,
        capability.Range is { } value ? new(value.Minimum, value.Maximum, value.Step, value.Unit, value.DefaultValue) : null);
    private static ControlNumberRange? Range(NvidiaControlNumberRange? value)
        => value is null ? null : new(value.Minimum, value.Maximum, value.Step, value.Unit, value.DefaultValue);
}
internal static class NvidiaGpuControlCopy
{
    internal static string Describe(NvidiaControlMessage code, IReadOnlyList<object?> arguments)
    {
        return "nvidia." + code.ToString();
    }
}
