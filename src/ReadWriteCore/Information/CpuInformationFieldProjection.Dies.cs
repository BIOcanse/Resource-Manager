using ResourceManager.ReadWrite.Windows.Cpu.Models;
using ResourceManager.ReadWrite.Windows.Devices.Models;
using F = ResourceManager.ReadWrite.Information.DeviceInformationFieldRegistry;

namespace ResourceManager.ReadWrite.Information;

public static partial class CpuInformationFieldProjection
{
    private static void AddDieFields(List<DeviceInformationObject> devices,
        DeviceInformationSnapshot current, CpuOsTopologyInformation? information)
    {
        DeviceInformationField? count = null;
        if (information?.Dies is not { } dies)
        {
            devices.AddRange(current.Devices.Where(static device => device.ObjectType == "cpu_die").Select(ClearDevice));
            var declaration = current.Devices.Where(static device => device.ObjectType == "cpu_topology").SelectMany(static device => device.Fields)
                .FirstOrDefault(static field => field.Definition == F.CpuAllDieCountOsReported);
            if (declaration is not null) count = declaration with { Value = null };
        }
        else
        {
            count = Integer(F.CpuAllDieCountOsReported, (ulong)dies.Count);
            foreach (var die in dies)
                devices.Add(new(die.DeviceIdentity, "cpu_die", Array.AsReadOnly(new[] {
                    Value(F.CpuAllDieLogicalProcessorAddresses, die.LogicalProcessorIds.Order().Select(static id =>
                        new CpuProcessorAddress((ushort)(id / 64), (byte)(id % 64))).ToArray()),
                    die.PackageDeviceIdentity is { } parent ? Value(F.CpuAllDiePackageDeviceIdentity, parent)
                        : new DeviceInformationField(F.CpuAllDiePackageDeviceIdentity, null) })));
        }
        if (count is null) return;
        var index = devices.FindIndex(static device => device.DeviceIdentity == "windows-cpu-topology");
        if (index < 0) devices.Add(new("windows-cpu-topology", "cpu_topology", Array.AsReadOnly(new[] { count })));
        else
        {
            var device = devices[index];
            devices[index] = device with { Fields = Array.AsReadOnly(device.Fields
                .Where(static field => field.Definition != F.CpuAllDieCountOsReported).Append(count).ToArray()) };
        }
    }
}
