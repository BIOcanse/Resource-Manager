using ResourceManager.App.Domain.Control;
using ResourceManager.App.Domain.RuntimeSpecialization.FreedomPoints;

namespace ResourceManager.App.Domain.RuntimeSpecialization;

public sealed record CompiledGpuVfCurveSettings(
    [property: System.Text.Json.Serialization.JsonPropertyName("rules")] GpuVfCurveEditorRules Rules)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public double ReadbackToleranceKhz => Rules.ReadbackToleranceKhz;

    public CompiledGpuVfCurveSettings Validate()
    {
        if (Rules is null || !Positive(Rules.FineStepKhz) || !Positive(Rules.CoarseStepKhz)
            || !Positive(Rules.DragQuantizationKhz) || !Positive(Rules.HitRadiusPixels)
            || !double.IsFinite(Rules.AxisPaddingRatio) || Rules.AxisPaddingRatio < 0
            || Rules.FrequencyDecimals < 0 || Rules.VoltageDecimals < 0
            || Rules.FrequencyDecimals > 15 || Rules.VoltageDecimals > 15
            || Rules.MaximumPresetCount < 1 || Rules.MaximumNameLength < 1
            || Rules.MaximumDraftCount < 1 || Rules.UndoCapacity < 1
            || !double.IsFinite(Rules.ReadbackToleranceKhz) || Rules.ReadbackToleranceKhz < 0
            || Rules.ReadbackToleranceKhz > int.MaxValue || Math.Truncate(Rules.ReadbackToleranceKhz) != Rules.ReadbackToleranceKhz
            || Rules.DefaultBindingScope is not (GpuVfCurveBindingScopes.Device or GpuVfCurveBindingScopes.Model)
            || Rules.SafetyMinimumOffsetKhz is { } min && !double.IsFinite(min)
            || Rules.SafetyMaximumOffsetKhz is { } max && !double.IsFinite(max)
            || Rules.SafetyMinimumOffsetKhz > Rules.SafetyMaximumOffsetKhz)
            throw new InvalidDataException("Invalid GPU V/F curve editor freedom-point settings.");
        return this;
    }

    private static bool Positive(double value) => double.IsFinite(value) && value > 0;
}
