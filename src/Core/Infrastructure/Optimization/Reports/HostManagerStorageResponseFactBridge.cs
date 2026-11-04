using ResourceManager.Adapter;
using ResourceManager.App.Application.DeviceTopology;
using ResourceManager.App.Application.MemoryProtection;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Domain.Software;
using ResourceManager.App.Infrastructure.NativeCore;

namespace ResourceManager.App.Infrastructure.Optimization.Reports;

// Transitional bridge: the current metric/topology owners are still managed.
// It transports their publications; the native coordinator owns every decision
// and counter. Removal batch: storage-fact-owner-zig, the whole-owner migration
// of storage publications and topology facts (including their reserve inputs).
internal sealed class HostManagerStorageResponseFactBridge(
    IDeviceTopologySnapshotProvider? topology = null,
    IHostMemoryProtectionReserve? reserve = null,
    string? dataDirectory = null,
    ResourceManager.App.Infrastructure.RuntimeSpecialization.HardwareMetricCatalogState? hardwareCatalog = null)
{
    internal HardwareMetricSnapshot? Catalog => hardwareCatalog?.Current;
    internal string? CatalogRevision => hardwareCatalog?.Revision;

    internal const string Queue = "storage_all_queue_length_actual";
    internal const string ReadCompletions = "storage_all_read_iops_actual";
    internal const string WriteCompletions = "storage_all_write_iops_actual";
    internal const string Latency = "storage_all_latency_actual";
    internal static readonly string[] FieldNames = [Queue, ReadCompletions, WriteCompletions, Latency];

    internal static bool Supports(HostManagerReportFactKind kind)
        => kind is HostManagerReportFactKind.StoragePendingQueue or HostManagerReportFactKind.StorageLatencyMilliseconds;

    internal HostManagerReportSourceObservation Project(ulong source, ulong coverage,
        IReadOnlyList<CompiledHostManagerReportCoordinatorRulePlan> rules, HardwareMetricSnapshot? hardware,
        IDictionary<ulong, HostManagerReportTargetDescriptor> targets)
    {
        var pending = rules.Any(rule => rule.FactKind == HostManagerReportFactKind.StoragePendingQueue);
        var names = (pending ? new[] { Queue, ReadCompletions, WriteCompletions } : [Latency]).ToHashSet(StringComparer.Ordinal);
        var bindings = hardware?.FieldBindings.Values.Where(binding => names.Contains(binding.Field.Name)).ToArray() ?? [];
        if (hardware is null || bindings.Length == 0)
            return new(source, coverage, 0, hardware?.CapturedAt ?? DateTimeOffset.UnixEpoch,
                NativeReportSourceStatus.Unavailable, 0, []);

        // Event: read cached topology only. Never refresh or query a disk here.
        var state = topology?.ReadState();
        var currentTopology = state is { State: DeviceTopologySnapshotStatus.Ready, Source: DeviceTopologySnapshotSource.Live }
            ? state.Snapshot : null;
        var reserves = reserve?.ReadStatus();
        var fields = MetricDeviceFields.Read(hardware, fields: FieldNames.ToHashSet(StringComparer.Ordinal));
        var facts = new List<HostManagerReportFactValue>();
        var observations = bindings.Select(binding => hardware.Datasets.GetValueOrDefault(binding.RequestAddress))
            .Where(value => value is not null).Select(value => value!).ToArray();
        var generation = observations.Select(value => value.CommittedGeneration).DefaultIfEmpty().Max();
        var ticks = observations.Select(value => Math.Max(value.ObservedAtUtcTicks, value.LastAttemptAtUtcTicks)).DefaultIfEmpty().Max();
        var duration = fields.SelectMany(disk => disk.Fields).Where(field => names.Contains(field.Name))
            .Select(field => field.SampleDurationMilliseconds ?? 0).Where(value => value > 0)
            .DefaultIfEmpty(checked((ulong)rules.Max(rule => rule.MinimumSampleDurationMilliseconds))).Min();
        foreach (var disk in fields)
        {
            var selected = bindings.Where(binding => binding.DeviceIdentity == disk.DeviceIdentity).ToArray();
            if (selected.Length == 0) continue;
            DeviceMetricFieldValue? Field(string name) => disk.Fields.SingleOrDefault(field => field.Name == name);
            double? Number(string name)
            {
                var binding = hardware.FieldBindings.Values.SingleOrDefault(value => value.DeviceIdentity == disk.DeviceIdentity && value.Field.Name == name);
                var value = Field(name)?.NumericValue;
                return binding is not null && hardware.TryGetCurrentDataset(binding.RequestAddress, out _)
                    && value is >= 0 && double.IsFinite(value.Value) ? value : null;
            }
            var handle = AdapterResourceKey.FromString($"report:physical_disk:{disk.DeviceIdentity}");
            var evidence = Evidence(disk.DeviceIdentity, Number(Queue), Number(ReadCompletions), Number(WriteCompletions),
                Number(Latency), currentTopology, reserves, dataDirectory);
            targets[handle] = new(handle, disk.DeviceIdentity, disk.DeviceIdentity, SoftwareKinds.Unattributed,
                SoftwareDisplayKinds.Unattributed, [], [], StorageResponse: evidence);
            foreach (var rule in rules)
            {
                var required = rule.FactKind == HostManagerReportFactKind.StoragePendingQueue
                    ? new[] { Queue, ReadCompletions, WriteCompletions } : [Latency];
                var requiredFields = required.Select(Field).ToArray();
                // Same endpoint timestamp and monotonic duration prove the
                // queue and rates describe one window, rather than old values.
                var available = required.All(name => Number(name) is not null)
                    && requiredFields.All(field => field?.ObservedAt is not null && field.SampleDurationMilliseconds >= checked((ulong)rule.MinimumSampleDurationMilliseconds))
                    && requiredFields.Select(field => field!.ObservedAt).Distinct().Count() == 1
                    && requiredFields.Select(field => field!.SampleDurationMilliseconds).Distinct().Count() == 1;
                var value = available ? Number(rule.FactKind == HostManagerReportFactKind.StoragePendingQueue ? Queue : Latency) : null;
                facts.Add(new(rule, handle, value, null,
                    available && rule.FactKind == HostManagerReportFactKind.StoragePendingQueue
                        ? Number(ReadCompletions) + Number(WriteCompletions) : null));
            }
        }
        return new(source, coverage, checked((long)generation), new DateTimeOffset(Math.Max(ticks, DateTimeOffset.UnixEpoch.UtcTicks), TimeSpan.Zero),
            NativeReportSourceStatus.Complete, checked((long)duration), facts);
    }

    internal static OptimizationStorageResponseEvidence Evidence(string identity, double? queue, double? reads, double? writes,
        double? latency, DeviceTopologySnapshot? topology, HostMemoryReserveStatus? reserves, string? dataDirectory)
    {
        var gaps = new List<string>();
        var disks = topology?.DiskLayoutInformation;
        var candidates = WindowsInformationFieldProjection.PhysicalDiskRelationCandidates(topology?.StorageInformation);
        var matches = disks?.Where(disk => WindowsInformationFieldProjection.DiskLayoutFields(disk, disks,
                topology?.VolumeSpaceInformation, candidates)
            .Single(field => field.Definition.Name == "storage_all_physical_disk_relation_actual").Value?.GetString() == identity).ToArray() ?? [];
        var number = matches.Length == 1 ? matches[0].OsDiskNumberReported : null;
        if (number is null || disks!.Count(disk => disk.OsDiskNumberReported == number) != 1)
        {
            number = null;
            gaps.Add("physical_disk_volume_relation_unavailable");
        }

        bool? Holds(IEnumerable<string?>? paths)
        {
            if (paths is null || number is null || topology?.VolumeSpaceInformation is null) return null;
            var unknown = false;
            foreach (var raw in paths)
            {
                var path = Normalize(raw);
                if (path is null) { unknown = true; continue; }
                var volumes = topology.VolumeSpaceInformation
                    .SelectMany(volume => (volume.MountPaths ?? []).Select(mount => (Volume: volume, Mount: Normalize(mount))))
                    .Where(row => row.Mount is not null && path.StartsWith(row.Mount.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(row => row.Mount!.Length).ToArray();
                if (volumes.Length == 0) { unknown = true; continue; }
                var longest = volumes.Where(row => row.Mount!.Length == volumes[0].Mount!.Length).Select(row => row.Volume).Distinct().ToArray();
                if (longest.Length != 1 || longest[0].DiskExtents is not { Count: > 0 } extents) { unknown = true; continue; }
                if (extents.Any(extent => extent.DiskNumberReported == number)) return true;
            }
            return unknown ? null : false;
        }
        var page = Holds(topology?.PageFileInformation?.Select(file => file.PathReported));
        var protection = Holds(reserves?.Entries.Where(entry => entry.RemainingBytes > 0).Select(entry => entry.Location));
        var data = Holds(dataDirectory is null ? null : [dataDirectory]);
        if (page is null) gaps.Add("page_file_location_unavailable");
        if (protection is null) gaps.Add("memory_protection_reserve_location_unavailable");
        if (data is null) gaps.Add("resource_manager_data_location_unavailable");
        return new(identity, queue, reads, writes, latency, page, protection, data, gaps);
    }

    private static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (path.StartsWith(@"\??\", StringComparison.Ordinal)) path = path[4..];
        try { return Path.IsPathFullyQualified(path) ? Path.GetFullPath(path).TrimEnd('\\') + "\\" : null; }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or IOException) { return null; }
    }
}
