using System.Collections;
using System.Reflection;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.ResourceBreakdown;

namespace Resource_Manager_APP.Tests;

public sealed class SchedulingProcessFactStorageTests
{
    [Fact]
    public void IndependentPartialPublicationsKeepOneFactPerProcessInstance()
    {
        var state = new SchedulingProcessFactSnapshotState();
        var now = DateTimeOffset.UtcNow;
        var nextGeneration = 1;
        var metrics = new[] { SchedulingProcessMetricMask.CpuUsage, SchedulingProcessMetricMask.MemoryUsage,
            SchedulingProcessMetricMask.GpuUsage, SchedulingProcessMetricMask.GpuDedicatedMemory,
            SchedulingProcessMetricMask.RuntimeState };
        for (var round = 1; round <= 8; round++)
        {
            Publish(SchedulingProcessMetricMask.None, SamplingDatasetIds.ProcessInventory, 450);
            Publish(SchedulingProcessMetricMask.None, SamplingDatasetIds.ProcessAttribution, 285);
            foreach (var metric in metrics) Publish(metric, SchedulingProcessFactSnapshot.DatasetIdFor(metric), 450);
            var retained = RetainedFacts(state);
            Assert.Equal(450, retained.Count);
            Assert.Equal(285, retained.Count(static fact => fact.SoftwareId.Length > 0));
            var texts = new HashSet<string>(retained.SelectMany(static fact =>
                new[] { fact.ProcessName, fact.ExecutablePath!, fact.SoftwareId }).Where(static text => text.Length > 0),
                ReferenceEqualityComparer.Instance);
            Assert.Equal(1185, texts.Count);
            // UTF-16 payload: 450 process names + 450 executable paths + 285 software IDs.
            Assert.Equal(33192, texts.Sum(static text => text.Length * sizeof(char)));
            Assert.All(retained, fact => Assert.Equal(metrics.Aggregate(SchedulingProcessMetricMask.None,
                static (mask, metric) => mask | metric), fact.ValidMetricMask));
        }

        var before = RetainedFacts(state);
        var request = Request(SchedulingProcessMetricMask.CpuUsage, SamplingDatasetIds.ProcessCpuUsage);
        var failed = state.PrepareFailureCopy(request, now, new IOException("fixture"));
        Assert.All(RetainedFacts(state), fact => Assert.True(fact.ValidMetricMask.HasFlag(SchedulingProcessMetricMask.CpuUsage)));
        state.CommitPrepared(failed);
        Assert.All(RetainedFacts(state), fact =>
        {
            Assert.False(fact.ValidMetricMask.HasFlag(SchedulingProcessMetricMask.CpuUsage));
            Assert.Equal(0, fact.CpuUsagePercent);
            Assert.True(fact.ValidMetricMask.HasFlag(SchedulingProcessMetricMask.MemoryUsage));
            Assert.Equal(2048, fact.MemoryUsedBytes);
        });
        Assert.Equal(450, before.Count);

        void Publish(SchedulingProcessMetricMask metric, string datasetId, int count)
        {
            var generation = (ulong)(nextGeneration++);
            var facts = Enumerable.Range(1, count).Select(id => Fact(id, metric, generation)).ToArray();
            var sample = new SchedulingProcessFactSnapshot(SamplingObservationStatus.Current, generation,
                now.UtcTicks, (uint)count, (uint)count, 0, 0, metric, metric, facts)
            {
                DatasetObservations = metric == SchedulingProcessMetricMask.None ? new Dictionary<SchedulingProcessMetricMask, SchedulingProcessDatasetObservation>() :
                    new Dictionary<SchedulingProcessMetricMask, SchedulingProcessDatasetObservation>
                    {
                        [metric] = SchedulingProcessDatasetObservation.CreateCurrent(metric, generation,
                            now.UtcTicks, generation, now.UtcTicks,
                            topologyGeneration: metric is SchedulingProcessMetricMask.GpuUsage or SchedulingProcessMetricMask.GpuDedicatedMemory ? 1UL : 0,
                            topologyFingerprint: metric is SchedulingProcessMetricMask.GpuUsage or SchedulingProcessMetricMask.GpuDedicatedMemory ? 1UL : 0)
                    }
            };
            state.ApplyScheduled(Request(metric, datasetId), sample, now,
                new NativeItemSamplingSubscriptionScheduleReceipt(NativeItemSamplingSubscriptionScheduleOrigin.Completion,
                    1, 1, generation, generation, now, TimeSpan.FromSeconds(1), TimeSpan.Zero,
                    now.AddSeconds(1), null, 1, 1, 0, 0));
        }
    }

    private static ResourceBreakdownSampleRequest Request(SchedulingProcessMetricMask metric, string datasetId)
        => new([], new Dictionary<string, string>(), ProcessSampleDetailLevel.SmartSchedulingLite, metric)
        { PublicationDatasetIds = [datasetId] };

    private static SchedulingProcessFact Fact(int id, SchedulingProcessMetricMask mask, ulong generation)
        => new(id, (ulong)id, new string($"process-{id}".ToCharArray()),
            new string($"C:\\Apps\\{id}\\app.exe".ToCharArray()),
            new string($"software-{id}".ToCharArray()), "Software", "other", "other", 1,
            mask, 17, 2048, generation, []);

    // Inspect the retained state graph, rather than counting temporary read projections.
    internal static HashSet<SchedulingProcessFact> RetainedFacts(object root)
    {
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var facts = new HashSet<SchedulingProcessFact>(ReferenceEqualityComparer.Instance);
        var pending = new Queue<object>();
        pending.Enqueue(root);
        while (pending.TryDequeue(out var value))
        {
            if (!seen.Add(value) || value is string) continue;
            if (seen.Count > 100000) throw new InvalidOperationException("Unexpected state graph size.");
            if (value is SchedulingProcessFact fact) facts.Add(fact);
            if (value is IDictionary dictionary)
            {
                foreach (DictionaryEntry entry in dictionary) if (entry.Value is not null) pending.Enqueue(entry.Value);
            }
            else if (value is IEnumerable sequence)
            {
                foreach (var entry in sequence) if (entry is not null) pending.Enqueue(entry);
            }
            else if (value.GetType().Namespace?.StartsWith("ResourceManager.App", StringComparison.Ordinal) == true)
            {
                foreach (var field in value.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                    if (field.GetValue(value) is { } child) pending.Enqueue(child);
            }
        }
        return facts;
    }
}
