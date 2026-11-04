using ResourceManager.ReadWrite.Windows.Cpu.Models;
using ResourceManager.ReadWrite.Windows.Devices.Models;
using F = ResourceManager.ReadWrite.Information.DeviceInformationFieldRegistry;

namespace ResourceManager.ReadWrite.Information;

public static partial class CpuInformationFieldProjection
{
    private static void AddPackageFields(List<DeviceInformationObject> devices,
        DeviceInformationSnapshot current, CpuOsTopologyInformation? information)
    {
        var packages = information?.Packages;
        DeviceInformationField? count = null;
        if (packages is null)
        {
            devices.AddRange(current.Devices.Where(static device => device.ObjectType == "cpu_package")
                .Select(ClearDevice));
            var declaration = current.Devices.Where(static device => device.ObjectType == "cpu_topology")
                .SelectMany(static device => device.Fields)
                .FirstOrDefault(static field => field.Definition == F.CpuAllPackageCountReported);
            if (declaration is not null) count = declaration with { Value = null };
        }
        else
        {
            count = Integer(F.CpuAllPackageCountReported, (ulong)packages.Count);
            foreach (var package in packages)
                devices.Add(new(package.DeviceIdentity, "cpu_package", Array.AsReadOnly(new[] {
                    Value(F.CpuAllPackageLogicalProcessorAddresses, package.LogicalProcessorIds.Order()
                        .Select(static id => new CpuProcessorAddress((ushort)(id / 64), (byte)(id % 64))).ToArray()) })));
        }
        if (count is null) return;
        var index = devices.FindIndex(static device => device.DeviceIdentity == "windows-cpu-topology");
        if (index < 0)
            devices.Add(new("windows-cpu-topology", "cpu_topology", Array.AsReadOnly(new[] { count })));
        else
        {
            var device = devices[index];
            devices[index] = device with { Fields = Array.AsReadOnly(device.Fields
                .Where(static field => field.Definition != F.CpuAllPackageCountReported).Append(count).ToArray()) };
        }
    }
}
