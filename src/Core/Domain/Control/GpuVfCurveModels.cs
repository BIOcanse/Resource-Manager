using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace ResourceManager.App.Domain.Control;

public static class GpuVfCurveCapabilities
{
    public const string Core = "gpu.core-vf-curve";
}

public static class GpuVfCurveBindingScopes
{
    public const string Device = "device";
    public const string Model = "model";
}

/// <summary>The signature covers the complete native point-id/voltage sequence, in native order.</summary>
public sealed record GpuVfCurveBinding(string Vendor, string DeviceIdentity, string Model,
    string PointSignature, string Scope)
{
    public static string ComputePointSignature(IEnumerable<(int PointId, long VoltageMicrovolts)> points)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join(";",
            points.Select(static point => FormattableString.Invariant($"{point.PointId}:{point.VoltageMicrovolts}"))))));

    public bool Matches(GpuVfCurveBinding other)
        => Vendor == other.Vendor && PointSignature == other.PointSignature
           && (Scope == GpuVfCurveBindingScopes.Device ? DeviceIdentity == other.DeviceIdentity
               : Scope == GpuVfCurveBindingScopes.Model && !string.IsNullOrWhiteSpace(Model) && Model == other.Model);
}

public sealed record GpuVfCurvePoint(int PointId, long VoltageMicrovolts, double FrequencyOffsetKhz);
public sealed record GpuVfCurveHardwarePoint(int PointId, long VoltageMicrovolts,
    double? DefaultFrequencyKhz, double? CurrentFrequencyKhz, double? DefaultOffsetKhz,
    double CurrentOffsetKhz, double? MinimumOffsetKhz, double? MaximumOffsetKhz);

public sealed record GpuVfCurveEditorRules(
    [property: JsonPropertyName("fineStepKhz")] double FineStepKhz,
    [property: JsonPropertyName("coarseStepKhz")] double CoarseStepKhz,
    [property: JsonPropertyName("dragQuantizationKhz")] double DragQuantizationKhz,
    [property: JsonPropertyName("frequencyDecimals")] int FrequencyDecimals,
    [property: JsonPropertyName("voltageDecimals")] int VoltageDecimals,
    [property: JsonPropertyName("axisPaddingRatio")] double AxisPaddingRatio,
    [property: JsonPropertyName("hitRadiusPixels")] double HitRadiusPixels,
    [property: JsonPropertyName("maximumPresetCount")] int MaximumPresetCount,
    [property: JsonPropertyName("maximumNameLength")] int MaximumNameLength,
    [property: JsonPropertyName("maximumDraftCount")] int MaximumDraftCount,
    [property: JsonPropertyName("undoCapacity")] int UndoCapacity,
    [property: JsonPropertyName("defaultBindingScope")] string DefaultBindingScope,
    [property: JsonPropertyName("safetyMinimumOffsetKhz")] double? SafetyMinimumOffsetKhz,
    [property: JsonPropertyName("safetyMaximumOffsetKhz")] double? SafetyMaximumOffsetKhz,
    [property: JsonPropertyName("readbackToleranceKhz")] double ReadbackToleranceKhz);

public sealed record GpuVfCurveDescriptor(string ObjectId, bool Available, bool Writable,
    ControlApplyReason? UnavailableReason, GpuVfCurveBinding? Binding,
    IReadOnlyList<GpuVfCurveHardwarePoint> Points, GpuVfCurveEditorRules Rules, DateTimeOffset ReadAt)
{
    /// <summary>A scalar point covers all voltages; its zero voltage is a structural sentinel.</summary>
    public bool ScalarOnly { get; init; }
}

public sealed record GpuVfCurvePreset(string Id, string Name, GpuVfCurveBinding Binding,
    IReadOnlyList<GpuVfCurvePoint> Points, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
public sealed record GpuVfCurveCatalog(IReadOnlyList<GpuVfCurvePreset> Presets)
{
    public static GpuVfCurveCatalog Empty { get; } = new([]);
}
public sealed record GpuVfCurveLibraryItem(GpuVfCurvePreset Preset, bool Applicable,
    ControlApplyReason? UnavailableReason);
public sealed record GpuVfCurveLibraryView(IReadOnlyList<GpuVfCurveLibraryItem> Presets,
    GpuVfCurveEditorRules Rules);
public sealed record GpuVfCurveReference(string Kind, string Id, string ObjectId, string? Name = null);

/// <summary>Accepted input owns a defensive immutable copy; reassertion never resolves the library id again.</summary>
public sealed record GpuVfCurveExecution
{
    public GpuVfCurveBinding Binding { get; }
    public IReadOnlyList<GpuVfCurvePoint> Points { get; }
    [System.Text.Json.Serialization.JsonConstructor]
    public GpuVfCurveExecution(GpuVfCurveBinding binding, IReadOnlyList<GpuVfCurvePoint> points)
    {
        Binding = binding;
        Points = points.ToImmutableArray();
    }
}

public sealed record GpuVfCurveDraft(string Id, string ObjectId, GpuVfCurveBinding Binding,
    IReadOnlyList<GpuVfCurvePoint> Points, GpuVfCurveDescriptor Descriptor, bool CanUndo, bool CanRedo);
public sealed record GpuVfCurveSaveRequest(string ObjectId, string Name, GpuVfCurveBinding ExpectedBinding,
    string? DraftId = null, string? CloneFromId = null, string? BindingScope = null,
    IReadOnlyList<GpuVfCurvePoint>? Points = null);
public sealed record GpuVfCurveEditorCommand(string Operation, string ObjectId,
    GpuVfCurveBinding ExpectedBinding, string? DraftId = null, string? CurveId = null,
    IReadOnlyList<int>? PointIds = null, double? DeltaKhz = null, double? OffsetKhz = null,
    int? Direction = null, bool Coarse = false);
