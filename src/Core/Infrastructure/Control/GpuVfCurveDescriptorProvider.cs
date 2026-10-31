using ResourceManager.App.Application.Control;
using ResourceManager.App.Domain.Control;
using ResourceManager.App.Infrastructure.Control.Writers;

namespace ResourceManager.App.Infrastructure.Control;

/// <summary>Explicit live GET projection. No cache and no polling or hardware writes.</summary>
public sealed class GpuVfCurveDescriptorProvider(IControlObjectCatalog catalog,
    IEnumerable<IControlWriter> writers) : IGpuVfCurveDescriptorProvider
{
    private readonly IReadOnlyList<IControlWriter> writers = writers.ToArray();

    [ResourceManager.ReadWrite.RuntimeRead(ResourceManager.ReadWrite.ReadVolatilityKind.Sampled)]
    public async Task<GpuVfCurveDescriptor> GetAsync(string objectId, CancellationToken cancellationToken)
    {
        var rules = GpuVfCurveSettings.ProductDefaults.Rules;
        var target = (await catalog.ReadObjectsAsync(cancellationToken).ConfigureAwait(false)).Objects
            .FirstOrDefault(target => target.Id == objectId);
        GpuVfCurveDescriptor Missing(ControlApplyReason reason) => new(objectId, false, false, reason, null, [], rules, DateTimeOffset.UtcNow);
        if (target is null) return Missing(new("control-object-missing"));
        if (target.Kind != ControlObjectKinds.Gpu) return Missing(new("gpu-vf-curve-object-not-gpu"));
        var capability = target.Capabilities.FirstOrDefault(capability => capability.Id == GpuVfCurveCapabilities.Core);
        if (capability is null) return Missing(new("control-capability-missing"));
        foreach (var writer in writers)
        {
            if (writer is NvidiaGpuControlWriter nvidia && target.Platform.Vendor == ControlVendors.Nvidia
                && target.GpuAttachment == ControlGpuAttachments.Discrete)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = nvidia.ReadVfCurve(target);
                if (read.Snapshot is not { } snapshot) return Missing(new(read.UnavailableReason ?? "nvidia-vf-snapshot-unavailable"));
                var points = snapshot.Points.Select(point => new GpuVfCurveHardwarePoint(point.PointId, point.VoltageMicrovolts,
                    point.DefaultFrequencyKhz, point.CurrentFrequencyKhz, 0, point.FrequencyOffsetKhz,
                    point.MinimumOffsetKhz, point.MaximumOffsetKhz)).ToArray();
                var rangeKnown = points.All(point => point.MinimumOffsetKhz is not null && point.MaximumOffsetKhz is not null);
                var scalarOnly = snapshot.Abi == ResourceManager.ReadWrite.Vendors.Nvidia.NvidiaGpuVfCurveAbis.Scalar;
                if (scalarOnly) points = points.Select(point => point with { DefaultFrequencyKhz = null, CurrentFrequencyKhz = null }).ToArray();
                return new(objectId, rangeKnown, snapshot.CanWrite && rangeKnown,
                    !rangeKnown ? new("gpu-vf-curve-range-unavailable")
                        : snapshot.UnavailableReason is { } reason ? new(reason) : null,
                    Binding(target, points, rules.DefaultBindingScope), points, rules, DateTimeOffset.UtcNow)
                { ScalarOnly = scalarOnly };
            }
            var availability = writer.Probe(target, capability);
            if (!availability.IsMine) continue;
            if (!availability.IsReadable) return Missing(availability.Reason ?? new("control-backend-unavailable"));
            var current = await writer.ReadAsync(target, capability, cancellationToken).ConfigureAwait(false);
            if (current?.Number is not { } number || !double.IsFinite(number))
                return Missing(current?.UnreadableReason ?? new("gpu-vf-curve-scalar-unreadable"));
            var range = availability.Range ?? capability.Range;
            if (range is null || !double.IsFinite(range.Minimum) || !double.IsFinite(range.Maximum))
                return Missing(new("gpu-vf-curve-range-unavailable"));
            double? Khz(double value, string? unit) => ControlUnitAdapter.Convert(value, unit, ControlUnits.Megahertz)
                * GpuVfCurveScalarAdapter.KilohertzPerMegahertz;
            if (Khz(number, current.Unit ?? range.Unit) is not { } offset
                || Khz(range.Minimum, range.Unit) is not { } minimum || Khz(range.Maximum, range.Unit) is not { } maximum)
                return Missing(new("control-unit-mismatch"));
            // Scalar-only interfaces have one logical point covering every voltage, not a measured 0 V sample.
            GpuVfCurveHardwarePoint[] scalar = [new(0, 0, null, null,
                range.DefaultValue is { } value ? Khz(value, range.Unit) : null, offset, minimum, maximum)];
            return new(objectId, true, availability.CanWrite, availability.Reason,
                Binding(target, scalar, rules.DefaultBindingScope), scalar, rules, DateTimeOffset.UtcNow) { ScalarOnly = true };
        }
        return Missing(capability.UnavailableReason ?? new("control-writer-missing"));
    }

    private static GpuVfCurveBinding Binding(ControlObject target, IReadOnlyList<GpuVfCurveHardwarePoint> points, string scope)
        => new(target.Platform.Vendor, target.Id, target.ReportedName ?? string.Empty,
            GpuVfCurveBinding.ComputePointSignature(points.Select(point => (point.PointId, point.VoltageMicrovolts))), scope);
}
