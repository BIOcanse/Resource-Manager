using System.Runtime.CompilerServices;
using ResourceManager.App.Application.CpuTopology;
using ResourceManager.App.Domain.CpuTopology;
using ResourceManager.App.Infrastructure.CpuTopology;

namespace ResourceManager.ControlDecisionAcceptance;

internal static partial class Program
{
    // Observes the coordinator's existing output; never creates its own demand.
    private sealed class ActualUseSmartResidencyObserver(EtwCpuCoreResidencyReader inner) : ICpuCoreResidencyReader
    {
        private long activeSmartSubscriptions, smartSnapshotOutputs, lastRequestedIntervalTicks;
        private CpuCoreResidencySnapshot? lastSmartSnapshot;
        internal TimeSpan LastRequestedInterval => TimeSpan.FromTicks(Interlocked.Read(ref lastRequestedIntervalTicks));
        internal long ActiveSmartSubscriptions => Interlocked.Read(ref activeSmartSubscriptions);
        internal long SmartSnapshotOutputs => Interlocked.Read(ref smartSnapshotOutputs);
        internal CpuCoreResidencySnapshot? LastSmartSnapshot => Volatile.Read(ref lastSmartSnapshot);
        public CpuCoreResidencySnapshot? Read() => inner.Read();
        public CpuCoreResidencyObservation ReadObservation() => inner.ReadObservation();
        public IDisposable AcquireSubscription(string id, TimeSpan interval) => inner.AcquireSubscription(id, interval);
        public async IAsyncEnumerable<CpuCoreResidencyObservation> SubscribeAsync(string id, TimeSpan interval,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var smart = id == "host-manager-smart-coordinator:cpu-cores";
            if (smart)
            {
                Interlocked.Exchange(ref lastRequestedIntervalTicks, interval.Ticks);
                Interlocked.Increment(ref activeSmartSubscriptions);
            }
            try
            {
                await foreach (var value in inner.SubscribeAsync(id, interval, cancellationToken).WithCancellation(cancellationToken))
                {
                    if (smart && value is CpuCoreResidencySnapshot snapshot)
                    {
                        Volatile.Write(ref lastSmartSnapshot, snapshot);
                        Interlocked.Increment(ref smartSnapshotOutputs);
                    }
                    yield return value;
                }
            }
            finally { if (smart) Interlocked.Decrement(ref activeSmartSubscriptions); }
        }
    }
}
