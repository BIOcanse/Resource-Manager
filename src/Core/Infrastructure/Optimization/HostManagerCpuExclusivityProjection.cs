using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ResourceManager.App.Domain.CpuTopology;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Domain.Software;
using ResourceManager.App.Infrastructure.NativeCore;

namespace ResourceManager.App.Infrastructure.Optimization;

internal sealed record HostManagerCpuReservations(IReadOnlyDictionary<string, IReadOnlySet<string>> BySoftware)
{
    internal IReadOnlySet<string> ExcludedFor(string softwareId)
    {
        BySoftware.TryGetValue(softwareId, out var own);
        return BySoftware.Values.SelectMany(static cores => cores)
            .Where(core => own?.Contains(core) != true).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
    internal bool IsOwner(string softwareId) => BySoftware.TryGetValue(softwareId, out var own) && own.Count != 0;
}

internal sealed class HostManagerCpuExclusivityProjection
{
    internal required NativeCpuExclusivityFrame Frame { get; init; }
    internal required NativeCpuExclusivityCore[] Cores { get; init; }
    internal required NativeCpuExclusivitySoftware[] Software { get; init; }
    internal required NativeCpuExclusivityUsage[] Usage { get; init; }
    internal required NativeCpuExclusivityReservation[] Manual { get; init; }
    internal required NativeCpuExclusivityReservation[] Output { get; init; }
    internal required string[] SoftwareIds { get; init; }
    internal required string[] CoreIds { get; init; }

    internal static HostManagerCpuExclusivityProjection Create(CpuTopologySnapshot topology,
        CpuCoreResidencySnapshot? observation, IReadOnlyList<HostManagerAutomaticPlacementProcess> processes,
        ulong generation, CompiledHostManagerPlacementCoordinatorRecreatePlan capacity)
    {
        var cores = topology.PhysicalCores.OrderBy(static core => core.Index).ThenBy(static core => core.Id, StringComparer.OrdinalIgnoreCase).ToArray();
        var ccdIds = cores.Select(static core => core.CcdId).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        var coreIndexes = cores.Select((core, index) => (core.Id, Index: checked((uint)index)))
            .ToDictionary(static item => item.Id, static item => item.Index, StringComparer.OrdinalIgnoreCase);
        var ccdIndexes = ccdIds.Select((id, index) => (id, Index: checked((uint)index)))
            .ToDictionary(static item => item.id, static item => item.Index, StringComparer.OrdinalIgnoreCase);
        var groups = processes.GroupBy(static process => process.SoftwareId, StringComparer.OrdinalIgnoreCase)
            .Select(group => (Id: group.Key, Key: Key(group.Key.ToUpperInvariant()), Processes: group.OrderBy(static p => p.ProcessId).ThenBy(static p => p.ProcessStartKey).ToArray()))
            .OrderBy(static group => group.Key).ToArray();
        if (cores.Length > capacity.CoreCapacity || ccdIds.Length > capacity.CcdCapacity || groups.Length > capacity.TargetCapacity)
            throw new InvalidDataException("CPU exclusivity facts exceed the published topology/software capacity.");
        if (groups.Select(static group => group.Key).Distinct().Count() != groups.Length)
            throw new InvalidDataException("CPU exclusivity software identity hash collision.");
        var validObservation = observation is { SessionGeneration: > 0 } && observation.MeasuredThrough > observation.MeasuredFrom;
        var resident = validObservation ? observation!.Processes
            .Where(static process => ulong.TryParse(process.ProcessStartKey, NumberStyles.None, CultureInfo.InvariantCulture, out var key) && key > 0)
            .ToDictionary(static process => (process.ProcessId, Start: ulong.Parse(process.ProcessStartKey!, CultureInfo.InvariantCulture))) : [];
        var software = new NativeCpuExclusivitySoftware[groups.Length];
        var usage = new List<NativeCpuExclusivityUsage>();
        var manual = new List<NativeCpuExclusivityReservation>();
        for (var index = 0; index < groups.Length; index++)
        {
            var group = groups[index];
            var values = new Dictionary<uint, double>();
            var observed = validObservation;
            foreach (var process in group.Processes)
            {
                if (!resident.TryGetValue((process.ProcessId, process.ProcessStartKey), out var measured)) { observed = false; continue; }
                foreach (var core in measured.PhysicalCores)
                {
                    if (!coreIndexes.TryGetValue(core.PhysicalCoreId, out var coreIndex) || !double.IsFinite(core.UsagePercent) || core.UsagePercent < 0)
                    { observed = false; break; }
                    values[coreIndex] = values.GetValueOrDefault(coreIndex) + core.UsagePercent;
                }
            }
            software[index] = new NativeCpuExclusivitySoftware
            {
                Key = group.Key,
                InstanceDigest = Key(string.Join(';', group.Processes.Select(static process => $"{process.ProcessId}/{process.ProcessStartKey}"))),
                Priority = group.Processes.Sum(static process => process.CanonicalCpuScore is >= 0 ? process.CanonicalCpuScore.Value : 0),
                Automatic = group.Processes.Any(static process => string.Equals(process.SoftwareKind, SoftwareKinds.Game, StringComparison.OrdinalIgnoreCase)) ? 1U : 0U,
                Observed = observed ? 1U : 0U
            };
            if (observed)
                usage.AddRange(values.OrderBy(static value => value.Key).Select(value => new NativeCpuExclusivityUsage
                    { SoftwareIndex = checked((uint)index), CoreIndex = value.Key, Percent = value.Value }));
            foreach (var coreId in group.Processes.SelectMany(static process => process.Policy.CpuManualExclusivePositionIds).Distinct(StringComparer.OrdinalIgnoreCase))
                if (coreIndexes.TryGetValue(coreId, out var c)) manual.Add(new() { SoftwareIndex = checked((uint)index), CoreIndex = c });
        }
        if (usage.Count > checked((long)capacity.TargetCapacity * capacity.CoreCapacity) || manual.Count > capacity.ReservationCapacity)
            throw new InvalidDataException("CPU exclusivity usage/manual facts exceed the published fact or reservation capacity.");
        var topologyKey = Key(string.Join(';', cores.Select(core => $"{core.Id}/{core.CcdId}/{string.Join(',', core.LogicalProcessorIds)}")));
        return new HostManagerCpuExclusivityProjection
        {
            Frame = new NativeCpuExclusivityFrame
            {
                AbiVersion = NativePlacementCoordinatorAbi.Version, StructSize = NativePlacementCoordinatorSession.SizeOf<NativeCpuExclusivityFrame>(),
                Generation = generation, TopologyKey = topologyKey,
                ObservationSource = validObservation ? checked((ulong)observation!.SessionGeneration) : 0,
                ObservedThrough = validObservation ? checked((ulong)observation!.MeasuredThrough.UtcTicks) : 0,
                CoreCount = checked((uint)cores.Length), CcdCount = checked((uint)ccdIds.Length), SoftwareCount = checked((uint)software.Length),
                UsageCount = checked((uint)usage.Count), ManualCount = checked((uint)manual.Count), OutputCapacity = checked((uint)capacity.ReservationCapacity)
            },
            Cores = cores.Select(core => new NativeCpuExclusivityCore { CcdIndex = ccdIndexes[core.CcdId] }).ToArray(),
            Software = software, Usage = usage.ToArray(), Manual = manual.ToArray(), Output = new NativeCpuExclusivityReservation[capacity.ReservationCapacity],
            SoftwareIds = groups.Select(static group => group.Id).ToArray(), CoreIds = cores.Select(static core => core.Id).ToArray()
        };
    }

    internal HostManagerCpuReservations Map(uint count)
    {
        if (count > Output.Length) throw new InvalidDataException("CPU exclusivity output count exceeds capacity.");
        var result = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in Output.AsSpan(0, checked((int)count)))
        {
            if (row.SoftwareIndex >= SoftwareIds.Length || row.CoreIndex >= CoreIds.Length)
                throw new InvalidDataException("CPU exclusivity output does not identify a submitted software/core.");
            var id = SoftwareIds[row.SoftwareIndex];
            if (!result.TryGetValue(id, out var values)) result.Add(id, values = new(StringComparer.OrdinalIgnoreCase));
            values.Add(CoreIds[row.CoreIndex]);
        }
        return new(result.ToDictionary(static item => item.Key, static item => (IReadOnlySet<string>)item.Value, StringComparer.OrdinalIgnoreCase));
    }

    private static ulong Key(string text)
    {
        var value = BinaryPrimitives.ReadUInt64LittleEndian(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
        return value == 0 ? 1 : value;
    }
}
