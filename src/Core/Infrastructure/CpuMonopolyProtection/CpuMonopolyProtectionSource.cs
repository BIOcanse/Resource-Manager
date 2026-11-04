using System.Globalization;
using System.Runtime.CompilerServices;
using ResourceManager.App.Application.CpuMonopolyProtection;
using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Application.ResourceBreakdown;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Domain.ProcessAttribution;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.ReadWrite.ProcessFields;

namespace ResourceManager.App.Infrastructure.CpuMonopolyProtection;

// CPU is the existing all-logical-processors percentage, delivered by its named subscription owner.
// Priority has no process-field address today: borrow the existing exact-instance platform reader on each delivery.
public sealed class CpuMonopolyProtectionSource(IResourceBreakdownPushSource resources,
    ISchedulingProcessFactObservationSource foundations, IProcessResourcePolicyWriter priorities) : ICpuMonopolyProtectionSource
{
    internal const string CpuField = "process_all_cpu_usage_actual";
    private static readonly ResourceBreakdownSampleRequest Request = new(
        [CpuField], new Dictionary<string, string>(), ProcessSampleDetailLevel.ResourceTableBasic)
        { PublicationDatasetIds = [SamplingDatasetIds.ProcessInventory, SamplingDatasetIds.ProcessAttribution] };

    public async IAsyncEnumerable<CpuMonopolyReading> SubscribeAsync(string holder, TimeSpan interval,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var snapshot in resources.SubscribeAsync(holder + ":processes", Request, interval, cancellationToken))
        {
            var facts = foundations.ReadLatest(new(SchedulingProcessMetricMask.None,
                new SchedulingGpuInventorySnapshot(SamplingObservationStatus.NotRequested, 0, 0, 0, 0, 0, 0, [])));
            if (facts?.TryGetCurrentFoundationDataset(SamplingDatasetIds.ProcessInventory, out _) != true)
            {
                yield return new(snapshot.CapturedAt, null, false);
                continue;
            }
            var field = ProcessMetricFieldRegistry.Find(CpuField)!;
            var observation = snapshot.ProcessFields?.Sources.FirstOrDefault(source => source.SourceId == field.SourceId);
            var available = observation?.CapturedAt is not null;
            var self = facts.AttributionProcesses.Where(process => process.SoftwareId == RuntimeAttributionIds.ResourceManagerSelf)
                .Select(process => (process.ProcessId, process.ProcessStartKey)).ToHashSet();
            var processes = new List<CpuMonopolyProcess>(facts.InventoryProcesses.Count);
            foreach (var process in facts.InventoryProcesses)
            {
                var key = process.ProcessStartKey.ToString(CultureInfo.InvariantCulture);
                var cpu = available ? snapshot.ProcessFields?.FindProcess(process.ProcessId, key)?.Values
                    .FirstOrDefault(value => value.Field == CpuField)?.NumericValue : null;
                var read = priorities.ReadPlacementStateForRecovery(process.ProcessId, ProcessPlacementReadFields.PriorityClass);
                var priority = read.Status == RecoveryReadStatus.Found && read.Value is { } current
                    && unchecked((ulong)current.StartedAt.ToFileTime()) == process.ProcessStartKey ? current.PriorityClass : null;
                processes.Add(new(process.ProcessId, key, process.ProcessName, cpu, priority, process.ExecutablePath,
                    process.ProcessId == Environment.ProcessId
                    || ResourceManagerSelfDescriptor.ProcessNames.Contains(process.ProcessName, StringComparer.OrdinalIgnoreCase)
                    || self.Contains((process.ProcessId, process.ProcessStartKey))));
            }
            yield return new(observation?.CapturedAt ?? snapshot.CapturedAt, processes,
                !facts.HasUnidentifiedProcesses && facts.OverflowCount == 0);
        }
    }
}
