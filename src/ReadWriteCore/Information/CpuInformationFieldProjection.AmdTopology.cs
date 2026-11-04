using System.Globalization;
using ResourceManager.ReadWrite.Windows.Cpu.Models;
using ResourceManager.ReadWrite.Windows.Devices.Models;
using F = ResourceManager.ReadWrite.Information.DeviceInformationFieldRegistry;

namespace ResourceManager.ReadWrite.Information;

public static partial class CpuInformationFieldProjection
{
    private const int MaximumProcessorAddresses = (ushort.MaxValue + 1) * 64;

    private sealed record AmdCoreMembership(string CoreIdentity, string CcdCoordinate,
        string ComplexCoordinate, IReadOnlyList<CpuProcessorAddress> Addresses,
        ushort CcdProcessorCount, ushort ComplexProcessorCount);

    private static void AddAmdTopologyFields(List<DeviceInformationObject> devices,
        DeviceInformationSnapshot current, CpuOsTopologyInformation? information)
    {
        if (TryCreateAmdTopologyObjects(information, out var rows)) devices.AddRange(rows);
        else devices.AddRange(current.Devices.Where(static device => device.ObjectType is "cpu_ccd" or "cpu_ccx")
            .Select(ClearDevice));
    }

    private static bool TryCreateAmdTopologyObjects(CpuOsTopologyInformation? information,
        out IReadOnlyList<DeviceInformationObject> result)
    {
        result = [];
        if (information?.PhysicalCores is not { } cores || information.Packages is not { } packages
            || information.ThreadCpuid is not { } captures || cores.Count > MaximumProcessorAddresses
            || packages.Count > MaximumProcessorAddresses || captures.Count > MaximumProcessorAddresses)
            return false;
        var coreProcessors = new HashSet<int>();
        foreach (var core in cores)
        {
            if (core.LogicalProcessorIds.Count is 0 or > MaximumProcessorAddresses) return false;
            foreach (var id in core.LogicalProcessorIds)
                if (id is < 0 or >= MaximumProcessorAddresses || !coreProcessors.Add(id)) return false;
        }
        var packageByProcessor = new Dictionary<int, string>();
        var packageIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var package in packages)
        {
            if (string.IsNullOrWhiteSpace(package.DeviceIdentity) || !packageIds.Add(package.DeviceIdentity)
                || package.LogicalProcessorIds.Count is 0 or > MaximumProcessorAddresses) return false;
            foreach (var id in package.LogicalProcessorIds)
                if (!coreProcessors.Contains(id) || !packageByProcessor.TryAdd(id, package.DeviceIdentity)) return false;
        }
        if (packageByProcessor.Count != coreProcessors.Count || captures.Count != coreProcessors.Count) return false;
        var dataByProcessor = new Dictionary<int, CpuThreadCpuidData>();
        foreach (var capture in captures)
            if (!coreProcessors.Contains(capture.LogicalProcessorId) || capture.Data is null
                || !dataByProcessor.TryAdd(capture.LogicalProcessorId, capture.Data)) return false;

        var apics = new HashSet<uint>();
        var hardwareCores = new HashSet<string>(StringComparer.Ordinal);
        var packageToSocket = new Dictionary<string, string>(StringComparer.Ordinal);
        var socketToPackage = new Dictionary<string, string>(StringComparer.Ordinal);
        var packageVendors = new Dictionary<string, string>(StringComparer.Ordinal);
        var socketCounts = new Dictionary<string, ushort>(StringComparer.Ordinal);
        var observedSocketCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var domains = new Dictionary<(int Level, uint Prefix, byte Shift), ushort>();
        var members = new List<AmdCoreMembership>();
        foreach (var core in cores)
        {
            var firstId = core.LogicalProcessorIds[0];
            var data = dataByProcessor[firstId];
            var package = packageByProcessor[firstId];
            if (core.LogicalProcessorIds.Any(id => packageByProcessor[id] != package
                || dataByProcessor[id].Identity.Vendor != data.Identity.Vendor)) return false;
            if (packageVendors.TryGetValue(package, out var vendor) && vendor != data.Identity.Vendor) return false;
            packageVendors[package] = data.Identity.Vendor;
            if (data.Identity.Vendor != "AuthenticAMD") continue;
            if (data.AmdExtendedTopology is not { } first || !ValidAmdLevels(first)) return false;
            foreach (var id in core.LogicalProcessorIds)
            {
                var actual = dataByProcessor[id];
                if (actual.AmdExtendedTopology is not { } topology || !ValidAmdLevels(topology)
                    || (actual.AmdExtendedApicId.HasValue && actual.AmdExtendedApicId != topology.ExtendedApicId)
                    || !apics.Add(topology.ExtendedApicId)
                    || !SameAmdDomain(first.Core, first.ExtendedApicId, topology.Core, topology.ExtendedApicId)
                    || !SameAmdDomain(first.Complex, first.ExtendedApicId, topology.Complex, topology.ExtendedApicId)
                    || !SameAmdDomain(first.Ccd, first.ExtendedApicId, topology.Ccd, topology.ExtendedApicId)
                    || !SameAmdDomain(first.Socket, first.ExtendedApicId, topology.Socket, topology.ExtendedApicId)) return false;
            }
            if (core.LogicalProcessorIds.Count > first.Core.LogicalProcessorCount) return false;
            var levels = new[] { first.Core, first.Complex, first.Ccd, first.Socket };
            for (var index = 0; index < levels.Length; index++)
            {
                var level = levels[index];
                var key = (index, first.ExtendedApicId >> level.ApicShift, level.ApicShift);
                if (domains.TryGetValue(key, out var count) && count != level.LogicalProcessorCount) return false;
                domains[key] = level.LogicalProcessorCount;
            }
            var socket = AmdDomainCoordinate("socket", first.Socket, first.ExtendedApicId);
            if ((packageToSocket.TryGetValue(package, out var oldSocket) && oldSocket != socket)
                || (socketToPackage.TryGetValue(socket, out var oldPackage) && oldPackage != package)
                || (socketCounts.TryGetValue(socket, out var oldCount) && oldCount != first.Socket.LogicalProcessorCount)) return false;
            packageToSocket[package] = socket;
            socketToPackage[socket] = package;
            socketCounts[socket] = first.Socket.LogicalProcessorCount;
            observedSocketCounts[socket] = observedSocketCounts.GetValueOrDefault(socket) + core.LogicalProcessorIds.Count;
            var hardwareCore = socket + "|" + AmdDomainCoordinate("core", first.Core, first.ExtendedApicId);
            if (!hardwareCores.Add(hardwareCore)) return false;
            var ccd = socket + "|" + AmdDomainCoordinate("ccd", first.Ccd, first.ExtendedApicId);
            var complex = ccd + "|" + AmdDomainCoordinate("complex", first.Complex, first.ExtendedApicId);
            members.Add(new(CoreDeviceIdentity(core), ccd, complex,
                Array.AsReadOnly(core.LogicalProcessorIds.Order().Select(static id =>
                    new CpuProcessorAddress((ushort)(id / 64), (byte)(id % 64))).ToArray()),
                first.Ccd.LogicalProcessorCount, first.Complex.LogicalProcessorCount));
        }
        if (observedSocketCounts.Any(pair => pair.Value > socketCounts[pair.Key])) return false;
        foreach (var level in domains.Keys.GroupBy(static domain => domain.Level))
        {
            ulong previousEnd = 0;
            foreach (var domain in level.OrderBy(static domain => (ulong)domain.Prefix << domain.Shift))
            {
                var start = (ulong)domain.Prefix << domain.Shift;
                if (start < previousEnd) return false;
                previousEnd = start + (1UL << domain.Shift);
            }
        }
        var rows = new List<DeviceInformationObject>();
        foreach (var ccd in members.GroupBy(static member => member.CcdCoordinate, StringComparer.Ordinal)
            .OrderBy(static group => group.Key, StringComparer.Ordinal))
        {
            if (ccd.Select(static member => member.CcdProcessorCount).Distinct().Count() != 1
                || ccd.Sum(static member => member.Addresses.Count) > ccd.First().CcdProcessorCount) return false;
            var membership = ccd.OrderBy(static member => member.CoreIdentity, StringComparer.Ordinal)
                .Select(static member => new CpuAmdCcdCoreMembership(member.CoreIdentity,
                    AmdComplexIdentity(member.ComplexCoordinate), member.Addresses)).ToArray();
            rows.Add(new("amd-cpu-ccd|" + ccd.Key, "cpu_ccd", Array.AsReadOnly(new[] {
                Value(F.CpuAmdCcdId, ccd.Key),
                Value(F.CpuAmdCcdCoreMembership, Array.AsReadOnly(membership)) })));
            foreach (var complex in ccd.GroupBy(static member => member.ComplexCoordinate, StringComparer.Ordinal)
                .OrderBy(static group => group.Key, StringComparer.Ordinal))
            {
                if (complex.Select(static member => member.ComplexProcessorCount).Distinct().Count() != 1
                    || complex.Sum(static member => member.Addresses.Count) > complex.First().ComplexProcessorCount) return false;
                rows.Add(new(AmdComplexIdentity(complex.Key), "cpu_ccx", Array.AsReadOnly(new[] {
                    Value(F.CpuAmdCcxId, complex.Key) })));
            }
        }
        result = Array.AsReadOnly(rows.ToArray());
        return true;
    }

    private static bool ValidAmdLevels(CpuAmdExtendedTopology topology)
        => Valid(topology.Core) && Valid(topology.Complex) && Valid(topology.Ccd) && Valid(topology.Socket)
            && topology.Core.ApicShift <= topology.Complex.ApicShift
            && topology.Complex.ApicShift <= topology.Ccd.ApicShift
            && topology.Ccd.ApicShift <= topology.Socket.ApicShift
            && topology.Core.LogicalProcessorCount <= topology.Complex.LogicalProcessorCount
            && topology.Complex.LogicalProcessorCount <= topology.Ccd.LogicalProcessorCount
            && topology.Ccd.LogicalProcessorCount <= topology.Socket.LogicalProcessorCount;

    private static bool Valid(CpuAmdTopologyLevel? level)
        => level is { ApicShift: <= 31, LogicalProcessorCount: > 0 }
            && level.LogicalProcessorCount <= (1UL << level.ApicShift);

    private static bool SameAmdDomain(CpuAmdTopologyLevel left, uint leftApic,
        CpuAmdTopologyLevel right, uint rightApic)
        => left == right && (leftApic >> left.ApicShift) == (rightApic >> right.ApicShift);

    private static string AmdDomainCoordinate(string name, CpuAmdTopologyLevel level, uint apic)
        => string.Create(CultureInfo.InvariantCulture, $"{name}:{apic >> level.ApicShift:X8}/shift:{level.ApicShift}");

    private static string AmdComplexIdentity(string coordinate) => "amd-cpu-ccx|" + coordinate;
}
