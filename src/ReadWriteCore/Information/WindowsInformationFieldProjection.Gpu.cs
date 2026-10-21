using ResourceManager.App.Domain.DeviceTopology;
using F = ResourceManager.ReadWrite.Information.DeviceInformationFieldRegistry;

namespace ResourceManager.ReadWrite.Information;

public static partial class WindowsInformationFieldProjection
{
    public static DeviceInformationSnapshot GpuDeclarations(IReadOnlyList<DeviceTopologyGpuInformation>? windows,
        IReadOnlyList<DeviceTopologyNvidiaGpuInformation>? nvidia, IReadOnlyList<DeviceTopologyAmdGpuInformation>? amd,
        DateTimeOffset? observedAt)
    {
        var devices = new List<DeviceInformationObject>();
        AppendWindowsGpuDeclarations(devices, windows, observedAt);
        AppendGpuDeclarations(devices, nvidia, observedAt);
        AppendAmdGpuDeclarations(devices, amd, observedAt);
        return new(observedAt, Array.AsReadOnly(devices.ToArray()));
    }

    internal static void AppendWindowsGpuDeclarations(List<DeviceInformationObject> devices,
        IReadOnlyList<DeviceTopologyGpuInformation>? declarations, DateTimeOffset? observedAt)
    {
        foreach (var group in (declarations ?? []).Where(static gpu => !string.IsNullOrWhiteSpace(gpu.PnpInstanceId))
            .GroupBy(static gpu => gpu.PnpInstanceId.Trim().ToUpperInvariant(), StringComparer.Ordinal))
        {
            if (group.Count() != 1) continue;
            var gpu = group.Single();
            AppendGpuFields(devices, group.Key, [Text(F.GpuAllAdapterLuid, gpu.AdapterLuid), Text(F.GpuAllName, gpu.Name),
                Text(F.GpuAllPciVendorId, gpu.PciVendorId), Text(F.GpuAllPciDeviceId, gpu.PciDeviceId),
                Text(F.GpuAllDriverVersion, gpu.DriverVersion)], observedAt, [F.PciAllVendorId, F.PciAllDeviceId]);
        }
    }

    internal static void AppendGpuDeclarations(List<DeviceInformationObject> devices,
        IReadOnlyList<DeviceTopologyNvidiaGpuInformation>? declarations, DateTimeOffset? observedAt)
    {
        foreach (var group in (declarations ?? []).Where(static gpu => !string.IsNullOrWhiteSpace(gpu.PnpInstanceId))
            .GroupBy(static gpu => gpu.PnpInstanceId.Trim().ToUpperInvariant(),StringComparer.Ordinal))
        {
            if (group.Count() != 1) continue;
            var gpu = group.Single();
            DeviceInformationField[] fields = [Text(F.GpuNvidiaVbiosVersion,gpu.VbiosVersion),
                Text(F.GpuNvidiaCudaComputeCapability,gpu.CudaComputeCapability)];
            AppendGpuFields(devices, group.Key, fields, observedAt);
        }
    }

    internal static void AppendAmdGpuDeclarations(List<DeviceInformationObject> devices,
        IReadOnlyList<DeviceTopologyAmdGpuInformation>? declarations, DateTimeOffset? observedAt)
    {
        foreach (var group in (declarations ?? []).Where(static gpu => !string.IsNullOrWhiteSpace(gpu.PnpInstanceId))
            .GroupBy(static gpu => gpu.PnpInstanceId.Trim().ToUpperInvariant(),StringComparer.Ordinal))
        {
            if (group.Count() != 1) continue;
            var gpu = group.Single();
            AppendGpuFields(devices, group.Key, [Text(F.GpuAmdVbiosVersion,gpu.VbiosVersion), Text(F.GpuAmdVramType,gpu.VramType),
                Text(F.GpuAmdVbiosPartNumber,gpu.VbiosPartNumber), Text(F.GpuAmdVbiosDateRaw,gpu.VbiosDateRaw)], observedAt);
        }
    }

    private static void AppendGpuFields(List<DeviceInformationObject> devices, string pnp,
        DeviceInformationField[] fields, DateTimeOffset? observedAt,
        IReadOnlyCollection<DeviceInformationDefinition>? replacedDefinitions = null)
    {
        var identity = "windows-pnp|" + pnp;
        var matching = devices.Where(device => device.DeviceIdentity == identity).ToArray();
        if (matching.Length > 1) return;
        if (observedAt is null) fields = fields.Select(static field => field with { Value = null }).ToArray();
        if (matching.Length == 1)
        {
            var original = matching[0];
            devices[devices.IndexOf(original)] = original with { ObjectType = "gpu_adapter",
                Fields = Array.AsReadOnly(original.Fields.Where(field => replacedDefinitions?.Contains(field.Definition) != true)
                    .Concat(fields).ToArray()) };
        }
        else devices.Add(new(identity, "gpu_adapter", Array.AsReadOnly(fields)));
    }
}
