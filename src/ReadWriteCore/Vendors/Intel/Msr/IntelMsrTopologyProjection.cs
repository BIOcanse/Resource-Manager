using ResourceManager.ReadWrite.Native;
using ResourceManager.ReadWrite.Windows.Cpu.Models;

namespace ResourceManager.ReadWrite.Vendors.Intel.Msr;

public static class IntelMsrTopologyProjection
{
    public static IntelMsrTopologyRead Create(CpuOsTopologyInformation? information)
    {
        var targets = new List<IntelMsrCatalogIdentity>();
        Dictionary<int, CpuThreadCpuidInformation>? cpuid = null;
        if (information?.ThreadCpuid is { } cpuidRows)
        {
            cpuid = new();
            foreach (var processor in cpuidRows)
                if (!cpuid.TryAdd(processor.LogicalProcessorId, processor))
                    return new(NativeMetricSnapshotCatalogTopologyStatus.Unavailable,
                        NativeMetricSnapshotCatalogTopologyStatus.Unavailable,
                        NativeMetricSnapshotCatalogTopologyStatus.Unavailable, []);
        }
        var cores = Project(information?.PhysicalCores?.Select(static core =>
            ($"windows-cpu-core|{string.Join(',', core.LogicalProcessorIds.Order())}", core.LogicalProcessorIds)), false);
        var packages = Project(information?.Packages?.Select(static package =>
            (package.DeviceIdentity, package.LogicalProcessorIds)), true);
        var threads = ProjectThreads();
        var dies = ProjectDies();
        var platform = ProjectPlatform();
        return new(cores, packages, threads, targets.OrderBy(static target => target.Kind)
            .ThenBy(static target => target.DisplayIndex).ToArray()) { DieStatus = dies, PlatformStatus = platform };

        NativeMetricSnapshotCatalogTopologyStatus ProjectPlatform()
        {
            if (information?.Packages is not { } packageValues) return NativeMetricSnapshotCatalogTopologyStatus.Unavailable;
            if (packageValues.Count == 0)
                return information.PhysicalCores is { Count: > 0 } || cpuid is { Count: > 0 }
                    ? NativeMetricSnapshotCatalogTopologyStatus.Unavailable : NativeMetricSnapshotCatalogTopologyStatus.Complete;
            if (information.PhysicalCores is not { } coreValues) return NativeMetricSnapshotCatalogTopologyStatus.Unavailable;
            var packageMembers = new HashSet<int>();
            var packageIdentities = new HashSet<string>(StringComparer.Ordinal);
            foreach (var package in packageValues)
            {
                if (string.IsNullOrWhiteSpace(package.DeviceIdentity) || !packageIdentities.Add(package.DeviceIdentity)
                    || package.LogicalProcessorIds.Count == 0) return NativeMetricSnapshotCatalogTopologyStatus.Unavailable;
                foreach (var id in package.LogicalProcessorIds)
                    if (id < 0 || id > ushort.MaxValue * 64 + 63 || !packageMembers.Add(id))
                        return NativeMetricSnapshotCatalogTopologyStatus.Unavailable;
            }
            var coreMembers = new HashSet<int>();
            foreach (var core in coreValues)
            {
                if (core.LogicalProcessorIds.Count == 0) return NativeMetricSnapshotCatalogTopologyStatus.Unavailable;
                foreach (var id in core.LogicalProcessorIds)
                    if (!coreMembers.Add(id)) return NativeMetricSnapshotCatalogTopologyStatus.Unavailable;
            }
            if (!coreMembers.SetEquals(packageMembers)) return NativeMetricSnapshotCatalogTopologyStatus.Unavailable;
            if (packageValues.Count != 1) return NativeMetricSnapshotCatalogTopologyStatus.Complete;
            if (cpuid is null || cpuid.Count != packageMembers.Count) return NativeMetricSnapshotCatalogTopologyStatus.Unavailable;
            foreach (var id in packageMembers)
                if (cpuid.GetValueOrDefault(id)?.Data is not { Signature: not null })
                    return NativeMetricSnapshotCatalogTopologyStatus.Unavailable;
            var representative = cpuid[packageMembers.Min()];
            var data = representative.Data!;
            foreach (var id in packageMembers)
            {
                var member = cpuid[id].Data!;
                if (member.Identity.Vendor != data.Identity.Vendor || member.Identity.Family != data.Identity.Family
                    || member.Identity.Model != data.Identity.Model || member.Signature != data.Signature)
                    return NativeMetricSnapshotCatalogTopologyStatus.Unavailable;
            }
            if (!IntelPsysModels.Supports(data.Identity)) return NativeMetricSnapshotCatalogTopologyStatus.Complete;
            var processorId = representative.LogicalProcessorId;
            targets.Add(new(0, "windows-intel-psys-platform|" + packageValues[0].DeviceIdentity,
                new((ushort)(processorId / 64), (byte)(processorId % 64)), data.Signature!.Value,
                data.IntelX2ApicId, IntelMsrTargetKind.Platform, false, false));
            return NativeMetricSnapshotCatalogTopologyStatus.Complete;
        }

        NativeMetricSnapshotCatalogTopologyStatus ProjectDies()
        {
            if (information?.Dies is not { } values) return NativeMetricSnapshotCatalogTopologyStatus.Unavailable;
            if (values.Count == 0) return NativeMetricSnapshotCatalogTopologyStatus.Complete;
            if (cpuid is null) return NativeMetricSnapshotCatalogTopologyStatus.Unavailable;
            var produced = new List<IntelMsrCatalogIdentity>();
            var ordered = values.OrderBy(static die => die.DeviceIdentity, StringComparer.Ordinal).ToArray();
            for (var index = 0; index < ordered.Length; index++)
            {
                var die = ordered[index];
                var processor = die.LogicalProcessorIds.Order().Select(id => cpuid.GetValueOrDefault(id))
                    .FirstOrDefault(static processor => processor?.Data is not null);
                if (processor?.Data is not { } data) return NativeMetricSnapshotCatalogTopologyStatus.Unavailable;
                var rapl = IntelRaplModels.Get(data.Identity, IntelMsrTargetKind.PhysicalDie);
                if (rapl.Domains == IntelRaplDomains.None) continue;
                if (data.Signature is not { } signature) return NativeMetricSnapshotCatalogTopologyStatus.Unavailable;
                var id = processor.LogicalProcessorId;
                produced.Add(new(index, die.DeviceIdentity, new((ushort)(id / 64), (byte)(id % 64)),
                    signature, data.IntelX2ApicId, IntelMsrTargetKind.PhysicalDie, false, false) { RaplProfile = rapl });
            }
            targets.AddRange(produced);
            return NativeMetricSnapshotCatalogTopologyStatus.Complete;
        }

        NativeMetricSnapshotCatalogTopologyStatus ProjectThreads()
        {
            if (cpuid is null) return NativeMetricSnapshotCatalogTopologyStatus.Unavailable;
            var produced = new List<IntelMsrCatalogIdentity>();
            foreach (var processor in cpuid.Values.OrderBy(static processor => processor.LogicalProcessorId))
            {
                if (processor.Data is not { } data) return NativeMetricSnapshotCatalogTopologyStatus.Unavailable;
                if (data.Identity.Vendor != "GenuineIntel" || data.IntelPerformanceFeedbackSupported != true) continue;
                if (data.Signature is not { } signature) return NativeMetricSnapshotCatalogTopologyStatus.Unavailable;
                var id = processor.LogicalProcessorId;
                produced.Add(new(id, processor.DeviceIdentity, new((ushort)(id / 64), (byte)(id % 64)),
                    signature, data.IntelX2ApicId, IntelMsrTargetKind.LogicalProcessor, false, false)
                    { SupportsPerformanceFeedback = true });
            }
            targets.AddRange(produced);
            return NativeMetricSnapshotCatalogTopologyStatus.Complete;
        }

        NativeMetricSnapshotCatalogTopologyStatus Project(IEnumerable<(string Identity, IReadOnlyList<int> Ids)>? groups, bool isPackage)
        {
            if (groups is null) return NativeMetricSnapshotCatalogTopologyStatus.Unavailable;
            var values = groups.OrderBy(static group => group.Identity, StringComparer.Ordinal).ToArray();
            if (values.Length == 0) return NativeMetricSnapshotCatalogTopologyStatus.Complete;
            if (cpuid is null) return NativeMetricSnapshotCatalogTopologyStatus.Unavailable;
            var produced = new List<IntelMsrCatalogIdentity>();
            for (var index = 0; index < values.Length; index++)
            {
                var group = values[index];
                var processor = group.Ids.Order().Select(id => cpuid.GetValueOrDefault(id))
                    .FirstOrDefault(static processor => processor?.Data is not null);
                if (processor?.Data is not { } data) return NativeMetricSnapshotCatalogTopologyStatus.Unavailable;
                if (data.Identity.Vendor != "GenuineIntel") continue;
                if (data.Signature is not { } signature)
                    return NativeMetricSnapshotCatalogTopologyStatus.Unavailable;
                var supports = isPackage ? data.IntelPackageThermalSupported == true
                    : data.IntelDtsSupported == true && !IntelThermalTargetModels.UsesModuleDts(data.Identity);
                var rapl = isPackage ? IntelRaplModels.Get(data.Identity) : default;
                if (!supports && rapl.Domains == IntelRaplDomains.None) continue;
                var id = processor.LogicalProcessorId;
                produced.Add(new(index, group.Identity, new((ushort)(id / 64), (byte)(id % 64)),
                    signature, data.IntelX2ApicId, isPackage ? IntelMsrTargetKind.Package : IntelMsrTargetKind.PhysicalCore,
                    supports, IntelThermalTargetModels.Supports(data.Identity))
                    { RaplProfile = rapl, ThermalStatusCapabilities = IntelThermalStatusCatalog.Capabilities(data,
                        isPackage ? IntelMsrTargetKind.Package : IntelMsrTargetKind.PhysicalCore) });
            }
            targets.AddRange(produced);
            return NativeMetricSnapshotCatalogTopologyStatus.Complete;
        }
    }
}
