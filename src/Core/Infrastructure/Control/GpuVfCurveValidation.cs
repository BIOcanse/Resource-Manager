using System.Collections.Immutable;
using ResourceManager.App.Application.Control;
using ResourceManager.App.Domain.Control;

namespace ResourceManager.App.Infrastructure.Control;

internal static class GpuVfCurveValidation
{
    internal static void Descriptor(GpuVfCurveDescriptor descriptor)
    {
        if (!descriptor.Available || descriptor.Binding is null || descriptor.Points.Count == 0)
            throw new GpuVfCurveRequestException(descriptor.UnavailableReason ?? new("gpu-vf-curve-unavailable"));
        if (descriptor.Points.Select(point => point.PointId).Distinct().Count() != descriptor.Points.Count
            || descriptor.Binding.PointSignature != GpuVfCurveBinding.ComputePointSignature(
                descriptor.Points.Select(point => (point.PointId, point.VoltageMicrovolts))))
            throw Error("gpu-vf-curve-binding-invalid");
        foreach (var point in descriptor.Points)
            if (point.MinimumOffsetKhz is not { } min || point.MaximumOffsetKhz is not { } max
                || !double.IsFinite(min) || !double.IsFinite(max) || min > max
                || point.DefaultOffsetKhz is { } defaultOffset && !double.IsFinite(defaultOffset)
                || !double.IsFinite(point.CurrentOffsetKhz)
                || point.DefaultFrequencyKhz is { } frequency && !double.IsFinite(frequency)
                || point.CurrentFrequencyKhz is { } current && !double.IsFinite(current))
                throw Error("gpu-vf-curve-range-unavailable");
    }

    internal static void Expected(GpuVfCurveDescriptor descriptor, GpuVfCurveBinding expected)
    {
        Descriptor(descriptor);
        if (expected != descriptor.Binding) throw Error("gpu-vf-curve-binding-changed");
    }

    internal static ImmutableArray<GpuVfCurvePoint> Points(IReadOnlyList<GpuVfCurvePoint> points,
        GpuVfCurveDescriptor descriptor)
    {
        Descriptor(descriptor);
        if (points.Count != descriptor.Points.Count) throw Error("gpu-vf-curve-point-sequence-mismatch");
        for (var index = 0; index < points.Count; index++)
        {
            var point = points[index];
            var hardware = descriptor.Points[index];
            if (point.PointId != hardware.PointId || point.VoltageMicrovolts != hardware.VoltageMicrovolts)
                throw Error("gpu-vf-curve-point-sequence-mismatch");
            var (min, max) = Bounds(hardware, descriptor.Rules);
            if (!double.IsFinite(point.FrequencyOffsetKhz) || point.FrequencyOffsetKhz < min || point.FrequencyOffsetKhz > max)
                throw new GpuVfCurveRequestException(new("gpu-vf-curve-offset-out-of-range",
                    new(Minimum: min, Maximum: max, Unit: "kHz", Requested: double.IsFinite(point.FrequencyOffsetKhz) ? point.FrequencyOffsetKhz : null)));
        }
        return points.ToImmutableArray();
    }

    internal static (double Min, double Max) Bounds(GpuVfCurveHardwarePoint point, GpuVfCurveEditorRules rules)
        => (Math.Max(point.MinimumOffsetKhz!.Value, rules.SafetyMinimumOffsetKhz ?? double.NegativeInfinity),
            Math.Min(point.MaximumOffsetKhz!.Value, rules.SafetyMaximumOffsetKhz ?? double.PositiveInfinity));

    internal static GpuVfCurveRequestException Error(string code) => new(new(code));
}
