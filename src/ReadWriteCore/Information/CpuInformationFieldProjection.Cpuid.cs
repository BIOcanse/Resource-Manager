using System.Collections.Frozen;
using ResourceManager.ReadWrite.Windows.Cpu.Models;
using ResourceManager.ReadWrite.Windows.Devices.Models;
using F = ResourceManager.ReadWrite.Information.DeviceInformationFieldRegistry;

namespace ResourceManager.ReadWrite.Information;

public static partial class CpuInformationFieldProjection
{
    private static readonly DeviceInformationDefinition[] CommonCpuidDefinitions =
        [F.CpuAllCpuidVendor, F.CpuAllBrand, F.CpuAllCpuidFamily, F.CpuAllCpuidModel, F.CpuAllCpuidStepping,
         F.CpuAllInstructionFeature, F.CpuAllCpuidSignature, F.CpuAllInitialApicId, F.CpuAllCpuidIdentityRegisters,
         F.CpuAllSupportedXcr0StateMask, F.CpuAllPhysicalAddressWidthDeclaredBits, F.CpuAllLinearAddressWidthDeclaredBits];
    private static readonly DeviceInformationDefinition[] IntelCpuidDefinitions =
        [F.CpuIntelHardwareCoreType, F.CpuIntelNativeModelId, F.CpuIntelX2ApicId, F.CpuIntelSupportedXssStateMask,
         F.CpuIntelAvx10Version, F.CpuIntelVmxSupported, F.CpuIntelSmxSupported,
         F.CpuIntelProcessorBaseFrequencyDeclaredMhz, F.CpuIntelProcessorMaximumFrequencyDeclaredMhz,
         F.CpuIntelBusReferenceFrequencyDeclaredMhz, F.CpuIntelNominalCoreCrystalFrequencyDeclaredHz,
         F.CpuIntelTscToCoreCrystalRatioDeclared];
    private static readonly DeviceInformationDefinition[] AmdCpuidDefinitions =
        [F.CpuAmdExtendedApicId, F.CpuAmdSupportedXssStateMask, F.CpuAmdNestedGuestPhysicalAddressWidthBits,
         F.CpuAmdSvmSupported, F.CpuAmdSvmRevision, F.CpuAmdSvmAsidCountDeclared, F.CpuAmdSvmFeature];
    private static readonly FrozenSet<DeviceInformationDefinition> CpuidDefinitions = CommonCpuidDefinitions
        .Concat(IntelCpuidDefinitions).Concat(AmdCpuidDefinitions).ToFrozenSet();

    public static DeviceInformationSnapshot Cpuid(IReadOnlyList<CpuThreadCpuidInformation> captures, DateTimeOffset? observedAt)
    {
        var devices = captures.OrderBy(static capture => capture.DeviceIdentity, StringComparer.Ordinal)
            .Select(capture => new DeviceInformationObject(capture.DeviceIdentity, "cpu_thread",
                Array.AsReadOnly(capture.Data is { } data ? ProjectCpuid(data).ToArray()
                    : CommonCpuidDefinitions.Select(static definition => new DeviceInformationField(definition, null)).ToArray())))
            .ToArray();
        var result = new DeviceInformationSnapshot(observedAt, Array.AsReadOnly(devices));
        return observedAt is null ? ClearValues(result) : result;
    }

    public static DeviceInformationSnapshot OsFeatures(IReadOnlyList<string>? features, ulong? enabledStateMask,
        DateTimeOffset? observedAt)
    {
        var result = new DeviceInformationSnapshot(observedAt, [new("windows-cpu-os-features", "cpu_os_capabilities",
            OsFeatureFields(features, enabledStateMask))]);
        return observedAt is null ? ClearValues(result) : result;
    }

    private static IReadOnlyList<DeviceInformationField> OsFeatureFields(IReadOnlyList<string>? features, ulong? enabledStateMask)
        => [features is not null ? Value(F.CpuAllOsUsableFeature, features) : new(F.CpuAllOsUsableFeature, null),
            Unsigned64(F.CpuAllOsEnabledXStateMask, enabledStateMask)];

    private static void AddCpuidFields(List<DeviceInformationObject> devices, DeviceInformationSnapshot current,
        CpuOsTopologyInformation? information)
    {
        var captures = information?.ThreadCpuid?.ToDictionary(static value => value.DeviceIdentity, StringComparer.Ordinal);
        var prior = current.Devices.ToDictionary(static device => device.DeviceIdentity, StringComparer.Ordinal);
        for (var index = 0; index < devices.Count; index++)
        {
            var device = devices[index];
            if (device.ObjectType != "cpu_thread") continue;
            var fields = new List<DeviceInformationField>();
            if (captures is not null && captures.TryGetValue(device.DeviceIdentity, out var capture))
            {
                if (capture.Data is { } data) fields.AddRange(ProjectCpuid(data));
                else
                {
                    fields.AddRange(CommonCpuidDefinitions.Select(static definition => new DeviceInformationField(definition, null)));
                    if (prior.TryGetValue(device.DeviceIdentity, out var old)) fields.AddRange(old.Fields
                        .Where(static field => IntelCpuidDefinitions.Contains(field.Definition) || AmdCpuidDefinitions.Contains(field.Definition))
                        .Select(static field => field with { Value = null }));
                }
            }
            else if (prior.TryGetValue(device.DeviceIdentity, out var old)) fields.AddRange(old.Fields
                .Where(static field => CpuidDefinitions.Contains(field.Definition)).Select(static field => field with { Value = null }));
            var osFields = device.Fields.Where(static field => !CpuidDefinitions.Contains(field.Definition));
            devices[index] = device with { Fields = Array.AsReadOnly(osFields.Concat(fields).ToArray()) };
        }
        if (information is { } os && (os.OsUsableFeatures is not null || os.OsEnabledXStateMask.HasValue))
            devices.Add(new("windows-cpu-os-features", "cpu_os_capabilities",
                OsFeatureFields(os.OsUsableFeatures, os.OsEnabledXStateMask)));
        else if (prior.TryGetValue("windows-cpu-os-features", out var old)) devices.Add(ClearDevice(old));
    }

    private static IEnumerable<DeviceInformationField> ProjectCpuid(CpuThreadCpuidData data)
    {
        var identity = data.Identity;
        var fields = new List<DeviceInformationField>
        {
            Text(F.CpuAllCpuidVendor, identity.Vendor), Text(F.CpuAllBrand, identity.Brand),
            Unsigned(F.CpuAllCpuidFamily, data.Signature.HasValue ? (uint)identity.Family : null),
            Unsigned(F.CpuAllCpuidModel, data.Signature.HasValue ? (uint)identity.Model : null),
            Unsigned(F.CpuAllCpuidStepping, data.Signature.HasValue ? (uint)identity.Stepping : null),
            Value(F.CpuAllInstructionFeature, identity.HardwareFeatures),
            Unsigned(F.CpuAllCpuidSignature, data.Signature), Unsigned(F.CpuAllInitialApicId, data.InitialApicId),
            Value(F.CpuAllCpuidIdentityRegisters, data.IdentityRegisters),
            Unsigned64(F.CpuAllSupportedXcr0StateMask, data.SupportedXcr0StateMask),
            Unsigned(F.CpuAllPhysicalAddressWidthDeclaredBits, data.PhysicalAddressWidthDeclaredBits),
            Unsigned(F.CpuAllLinearAddressWidthDeclaredBits, data.LinearAddressWidthDeclaredBits)
        };
        if (identity.Vendor == "GenuineIntel") fields.AddRange([
            Unsigned(F.CpuIntelHardwareCoreType, data.IntelHardwareCoreType),
            Unsigned(F.CpuIntelNativeModelId, data.IntelNativeModelId), Unsigned(F.CpuIntelX2ApicId, data.IntelX2ApicId),
            Unsigned64(F.CpuIntelSupportedXssStateMask, data.IntelSupportedXssStateMask),
            Unsigned(F.CpuIntelAvx10Version, data.IntelAvx10Version),
            Boolean(F.CpuIntelVmxSupported, data.IntelVmxSupported), Boolean(F.CpuIntelSmxSupported, data.IntelSmxSupported),
            Unsigned(F.CpuIntelProcessorBaseFrequencyDeclaredMhz, data.IntelProcessorBaseFrequencyDeclaredMhz),
            Unsigned(F.CpuIntelProcessorMaximumFrequencyDeclaredMhz, data.IntelProcessorMaximumFrequencyDeclaredMhz),
            Unsigned(F.CpuIntelBusReferenceFrequencyDeclaredMhz, data.IntelBusReferenceFrequencyDeclaredMhz),
            Unsigned(F.CpuIntelNominalCoreCrystalFrequencyDeclaredHz, data.IntelNominalCoreCrystalFrequencyDeclaredHz),
            data.IntelTscToCoreCrystalRatioDeclared is { Numerator: > 0, Denominator: > 0 } ratio ? Value(F.CpuIntelTscToCoreCrystalRatioDeclared,
                new DeviceInformationRate(ratio.Numerator, ratio.Denominator, (double)ratio.Numerator / ratio.Denominator))
                : new(F.CpuIntelTscToCoreCrystalRatioDeclared, null)]);
        if (identity.Vendor == "AuthenticAMD") fields.AddRange([Unsigned(F.CpuAmdExtendedApicId, data.AmdExtendedApicId),
            Unsigned(F.CpuAmdSupportedXssStateMask, data.AmdSupportedXssStateMask),
            Unsigned(F.CpuAmdNestedGuestPhysicalAddressWidthBits, data.AmdNestedGuestPhysicalAddressWidthBits),
            Boolean(F.CpuAmdSvmSupported, data.AmdSvmSupported), Unsigned(F.CpuAmdSvmRevision, data.AmdSvmRevision),
            Unsigned(F.CpuAmdSvmAsidCountDeclared, data.AmdSvmAsidCountDeclared),
            data.AmdSvmFeatures is { } svm ? Value(F.CpuAmdSvmFeature, svm) : new(F.CpuAmdSvmFeature, null)]);
        return fields;
    }

    private static DeviceInformationField Unsigned(DeviceInformationDefinition definition, uint? value)
        => value is { } number ? Integer(definition, number) : new(definition, null);

    private static DeviceInformationField Unsigned64(DeviceInformationDefinition definition, ulong? value)
        => value is { } number ? Integer(definition, number) : new(definition, null);

    private static DeviceInformationField Text(DeviceInformationDefinition definition, string? value)
        => string.IsNullOrWhiteSpace(value) ? new(definition, null) : Value(definition, value);

    private static DeviceInformationField Boolean(DeviceInformationDefinition definition, bool? value)
        => value is { } flag ? Value(definition, flag) : new(definition, null);
}
