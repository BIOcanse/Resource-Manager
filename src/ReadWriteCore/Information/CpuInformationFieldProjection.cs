using System.Globalization;
using System.Text.Json;
using ResourceManager.ReadWrite.Windows.Cpu.Models;
using ResourceManager.ReadWrite.Windows.Devices.Models;
using F = ResourceManager.ReadWrite.Information.DeviceInformationFieldRegistry;

namespace ResourceManager.ReadWrite.Information;

/// <summary>Projects only verified OS relationships in the existing CPU topology publication.</summary>
public static partial class CpuInformationFieldProjection
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static DeviceInformationSnapshot Create(CpuOsTopologyInformation? information,
        DateTimeOffset capturedAt, DeviceInformationSnapshot current)
    {
        var devices = new List<DeviceInformationObject>();
        if (information?.PhysicalCores is not { } cores)
        {
            RetainEmpty("cpu_topology", "cpu_core", "cpu_thread");
        }
        else
        {
            var ids = cores.SelectMany(static core => core.LogicalProcessorIds).Distinct().ToArray();
            Add("windows-cpu-topology", "cpu_topology", [
                Value(F.CpuAllSmtEnabled, cores.Any(static core => core.SmtRelationshipReported)),
                Integer(F.CpuAllPhysicalCoreCountReported, (ulong)cores.Count),
                Integer(F.CpuAllLogicalProcessorCountReported, (ulong)ids.Length)]);
            foreach (var core in cores)
            {
                var addresses = core.LogicalProcessorIds.Order().Select(static id =>
                    new CpuProcessorAddress((ushort)(id / 64), (byte)(id % 64))).ToArray();
                var identity = CoreDeviceIdentity(core);
                Add(identity, "cpu_core", [
                    Value(F.CpuAllPhysicalCoreId, core.PhysicalCoreId),
                    Integer(F.CpuAllOsEfficiencyClass, core.EfficiencyClass),
                    Value(F.CpuAllCoreLogicalProcessorAddresses, addresses),
                    Value(F.CpuAllSmtRelationshipReported, core.SmtRelationshipReported),
                    BusyPercent(F.CpuAllPhysicalCoreBusyRatio, CoreBusyPercent(core, information.LogicalProcessorBusyPercent))]);
                foreach (var id in core.LogicalProcessorIds.Order())
                {
                    Add($"windows-cpu-thread|{id / 64}:{id % 64}", "cpu_thread", [
                        Value(F.CpuAllPhysicalCoreId, core.PhysicalCoreId),
                        Integer(F.CpuAllLogicalProcessorId, (ulong)id),
                        Integer(F.CpuAllProcessorGroup, (ulong)(id / 64)),
                        Integer(F.CpuAllGroupRelativeProcessorIndex, (ulong)(id % 64)),
                        BusyPercent(F.CpuAllLogicalBusyRatio, ThreadBusyPercent(id, information.LogicalProcessorBusyPercent))]);
                }
            }
        }
        if (information?.Caches is not { } caches)
        {
            RetainEmpty("cpu_cache");
        }
        else
        {
            foreach (var cache in caches)
                Add(cache.DeviceIdentity, "cpu_cache", [
                    Integer(F.CpuAllCacheLevel, (ulong)cache.Level),
                    Integer(F.CpuAllCacheCapacity, cache.CapacityBytes),
                    Integer(F.CpuAllCacheLineSize, cache.LineSizeBytes),
                    Integer(F.CpuAllCacheAssociativity, cache.Associativity),
                    Integer(F.CpuAllCacheType, unchecked((uint)cache.NativeCacheType)),
                    Value(F.CpuAllCacheSharedLogicalProcessorIds, cache.LogicalProcessorIds)]);
        }
        if (information?.NumaNodes is not { } numaNodes)
        {
            RetainEmpty("numa_node");
        }
        else
        {
            foreach (var node in numaNodes)
                Add($"windows-numa-node|{node.NodeNumber}", "numa_node", [
                    Integer(F.CpuAllNumaNodeId, node.NodeNumber),
                    Value(F.CpuAllNumaNodeCpuAffinity, node.LogicalProcessorIds.Order().Select(static id =>
                        new CpuProcessorAddress((ushort)(id / 64), (byte)(id % 64))).ToArray())]);
        }
        AddPackageFields(devices, current, information);
        AddDieFields(devices, current, information);
        AddCpuidFields(devices, current, information);
        AddAmdTopologyFields(devices, current, information);
        return new(capturedAt, Array.AsReadOnly(devices.OrderBy(static device => device.DeviceIdentity,
            StringComparer.Ordinal).ToArray()));

        void Add(string identity, string type, DeviceInformationField[] fields)
            => devices.Add(new(identity, type, Array.AsReadOnly(fields)));
        void RetainEmpty(params string[] types)
            => devices.AddRange(current.Devices.Where(device => types.Contains(device.ObjectType,
                StringComparer.Ordinal)).Select(ClearDevice));
    }

    public static DeviceInformationSnapshot ClearValues(DeviceInformationSnapshot current)
        => new(null, Array.AsReadOnly(current.Devices.Select(ClearDevice).ToArray()));

    private static DeviceInformationObject ClearDevice(DeviceInformationObject device)
        => device with { Fields = Array.AsReadOnly(device.Fields.Select(field => field with { Value = null }).ToArray()) };

    private static string CoreDeviceIdentity(CpuOsPhysicalCore core)
        => $"windows-cpu-core|{string.Join(',', core.LogicalProcessorIds.Order())}";

    private static DeviceInformationField Integer(DeviceInformationDefinition definition, ulong value)
        => Value(definition, value.ToString(CultureInfo.InvariantCulture));

    private static DeviceInformationField BusyPercent(DeviceInformationDefinition definition, double? value)
        => value is { } percent ? Value(definition, percent) : new(definition, null);

    private static double? ThreadBusyPercent(int id, IReadOnlyList<double?>? values)
        => values is not null && id >= 0 && id < values.Count && values[id] is { } value
            && double.IsFinite(value) && value is >= 0 and <= 100 ? value : null;

    private static double? CoreBusyPercent(CpuOsPhysicalCore core, IReadOnlyList<double?>? values)
    {
        if (core.LogicalProcessorIds.Count == 0) return null;
        var total = 0d;
        foreach (var id in core.LogicalProcessorIds)
        {
            if (ThreadBusyPercent(id, values) is not { } value) return null;
            total += value;
        }
        return total / core.LogicalProcessorIds.Count;
    }

    private static DeviceInformationField Value<T>(DeviceInformationDefinition definition, T value)
        => new(definition, JsonSerializer.SerializeToElement(value, JsonOptions));
}
