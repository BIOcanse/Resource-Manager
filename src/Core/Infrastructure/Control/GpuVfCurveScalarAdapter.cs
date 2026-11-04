using ResourceManager.App.Domain.Control;

namespace ResourceManager.App.Infrastructure.Control;

/// <summary>Translation into an existing scalar-only vendor interface; the product owns one curve capability.</summary>
internal static class GpuVfCurveScalarAdapter
{
    internal const string CapabilityId = "gpu.core-vf-curve";
    // Persisted compatibility boundary, remove only when upgrades from pre-curve releases are unsupported.
    internal const string LegacyCapabilityId = "gpu.core-clock-offset";
    internal const double KilohertzPerMegahertz = 1000; // Unit definition, not a policy value.

    internal static string NativeId(string capabilityId)
        => capabilityId == CapabilityId ? LegacyCapabilityId : capabilityId;

    internal static ControlCapability NativeCapability(ControlCapability capability)
        => capability.Id == CapabilityId ? capability with
        { Id = LegacyCapabilityId, ValueKind = ControlValueKinds.Number } : capability;

    internal static bool TrySetting(ControlSetting setting, out ControlSetting adapted)
    {
        adapted = setting;
        if (setting.CapabilityId != CapabilityId) return true;
        if (setting.GpuVfCurvePresetId is null || setting.GpuVfCurveExecution is not { Points.Count: 1 } execution
            || setting.Number is not null || setting.Toggle is not null || setting.Curve is not null
            || !double.IsFinite(execution.Points[0].FrequencyOffsetKhz)) return false;
        adapted = new(LegacyCapabilityId,
            Number: execution.Points[0].FrequencyOffsetKhz / KilohertzPerMegahertz,
            Unit: ControlUnits.Megahertz);
        return true;
    }
}
