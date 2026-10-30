using System.Diagnostics;
using System.Threading.Channels;
using ResourceManager.App.Domain.CpuTopology;
using ResourceManager.App.Infrastructure.CpuTopology;
using ResourceManager.App.Infrastructure.Telemetry.Etw;
using ResourceManager.ReadWrite.Windows.Devices;

namespace ResourceManager.ReadWrite.Windows.Processes;

public sealed partial class EtwCpuResidencyCaptureOwner
{
    private sealed class NativeResidencyReader(NativeKernelEtwSession session, CpuResidencyCaptureConfiguration plan,
        long generation, KernelEtwEventSource source)
    {
        private readonly object gate = new();
        private ulong sequence;
        private CpuResidencyAggregationSnapshot aggregate = new(Stopwatch.Frequency, 0, 0, null, false, []);
        private CpuResidencyAggregationSnapshot? projectedAggregate;
        private CpuCoreResidencySnapshot? projected;
        private long minimumWindowQpc;
        private int expectedEventsLost;
        private ulong requiredTicket;
        private CpuResidencyAggregationSnapshot warming = new(Stopwatch.Frequency, 0, 0, null, false, []);
        internal NativeKernelEtwSession Session => session;
        internal long WindowStartQpc { get { lock (gate) return minimumWindowQpc; } }
        internal CpuResidencyAggregationDiagnostics? GetDiagnostics()
            => KernelEtwNativeReaders.ReadResidencyDiagnostics(session);

        internal void Configure(int[] ids, bool enabled)
        {
            lock (gate)
            {
                minimumWindowQpc = session.IsRealtime ? Stopwatch.GetTimestamp() : 0;
                ClearProjection();
                KernelEtwNativeReaders.ConfigureResidency(session, plan.ObservationWindowMilliseconds, plan.BucketWidthMilliseconds, plan.MaximumActivePairs);
                KernelEtwNativeReaders.ConfigureProcessors(session, ids);
                KernelEtwNativeReaders.SetEnabled(session, KernelEtwNativeReaders.Residency, enabled, refresh: false);
                requiredTicket = KernelEtwNativeReaders.ResetContinuity(session, minimumWindowQpc);
                session.RequestRefresh(Environment.TickCount64);
            }
        }
        internal void SetEnabled(bool enabled) => KernelEtwNativeReaders.SetEnabled(session, KernelEtwNativeReaders.Residency, enabled);
        internal void ResetContinuity()
        {
            lock (gate)
            {
                minimumWindowQpc = session.IsRealtime ? Stopwatch.GetTimestamp() : 0;
                ClearProjection();
                requiredTicket = KernelEtwNativeReaders.ResetContinuity(session, minimumWindowQpc);
                session.RequestRefresh(Environment.TickCount64);
            }
        }
        internal void RefreshEventLoss(int lost)
        {
            lock (gate) { expectedEventsLost = lost; aggregate = warming; sequence = 0; }
        }
        internal CpuResidencyAggregationSnapshot ReadAggregate()
        {
            lock (gate)
            {
                try
                {
                    aggregate = KernelEtwNativeReaders.ReadResidency(session, ref sequence, expectedEventsLost, requiredTicket, aggregate, Project) ?? warming;
                    // The floor rejects completed output only. An incomplete readiness view
                    // starts before the floor until one window has passed, but its boundary
                    // bit is what extends the waiter's budget to window plus grace.
                    return aggregate.IsComplete && aggregate.WindowStartQpc < minimumWindowQpc ? warming : aggregate;
                }
                catch (KernelEtwNativeReaders.CaptureFailure) { ClearProjection(); throw; }
            }
        }
        internal (CpuResidencyAggregationSnapshot Aggregate, CpuCoreResidencySnapshot? Projected) ReadCurrent()
        {
            lock (gate)
            {
                var current = ReadAggregate();
                return (current, ReferenceEquals(projectedAggregate, current) ? projected : null);
            }
        }
        private void ClearProjection()
        {
            warming = new(Stopwatch.Frequency, 0, 0, null, false, []);
            aggregate = warming; projected = null; projectedAggregate = null; sequence = 0;
        }
        private CpuResidencyAggregationSnapshot Project(KernelEtwNativeReaders.ResidencyView view)
        {
            var through = (view.Flags & 2) != 0 ? new DateTimeOffset(view.TimeTicks, TimeSpan.Zero) : (DateTimeOffset?)null;
            var metadata = new CpuResidencyAggregationSnapshot(view.Frequency, view.StartQpc, view.ThroughQpc, through, (view.Flags & 1) != 0, [])
                { HasBoundaries = (view.Flags & 5) != 0 };
            if (!metadata.IsComplete || view.StartQpc < minimumWindowQpc) return metadata;
            if (through is null) throw new InvalidDataException("A complete native residency window has no civil timestamp.");
            var borrowed = metadata with { Records = new KernelEtwNativeReaders.BorrowedResidencyRecords(view.Rows, view.Count) };
            var window = ToDuration(view.ThroughQpc - view.StartQpc, view.Frequency);
            projected = new(DateTimeOffset.UtcNow, window, generation, through.Value - window, through.Value,
                BuildProcessSnapshot(plan, generation, borrowed)) { LogicalProcessorScopes = CreateScopes(plan) };
            projectedAggregate = metadata;
            return metadata;
        }
        internal async Task<bool> SampleCurrentAsync(TimeSpan budget, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var notices = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
            { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, AllowSynchronousContinuations = false });
            using var observation = source.ObservePublications(() => notices.Writer.TryWrite(0));
            ulong ticket;
            bool pending;
            lock (gate)
            {
                var request = KernelEtwNativeReaders.RequestResidency(session);
                requiredTicket = Math.Max(requiredTicket, request.Ticket);
                ticket = requiredTicket;
                pending = request.Pending;
            }
            // A fixture/EOF publication may already have produced the current
            // component. Do not stage another provider publication in that case.
            if (!pending && KernelEtwNativeReaders.IsResidencyApplied(session, ticket)) return true;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(budget);
            try
            {
                if (pending) session.RequestRefresh(Environment.TickCount64);
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (KernelEtwNativeReaders.IsResidencyApplied(session, ticket)) return true;
                    if (session.ProcessingCompletion.IsCompleted)
                    {
                        if (session.ProcessingFailure is { } failure)
                            throw new InvalidOperationException("Native CPU sampling ended with a capture failure.", failure);
                        return false;
                    }
                    var signal = notices.Reader.ReadAsync(timeout.Token).AsTask();
                    await Task.WhenAny(signal, session.ProcessingCompletion).WaitAsync(timeout.Token).ConfigureAwait(false);
                    if (signal.IsCompleted) await signal.ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            { cancellationToken.ThrowIfCancellationRequested(); throw; }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested) { return false; }
            finally { timeout.Cancel(); notices.Writer.TryComplete(); }
        }

        internal async Task<bool> WaitForCompleteWindowAsync(TimeSpan boundaryBudget, TimeSpan completeBudget, CancellationToken cancellationToken)
        {
            var notices = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
            { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, AllowSynchronousContinuations = false });
            using var observation = source.ObservePublications(() => notices.Writer.TryWrite(0));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(boundaryBudget);
            // Waiting observes published banks only. It does not open a session,
            // sample a second source, or touch consumer-owned working state.
            try
            {
                var boundariesObserved = false;
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var aggregate = ReadAggregate();
                    if (aggregate.IsComplete) return true;
                    if (!boundariesObserved && aggregate.HasBoundaries)
                    {
                        boundariesObserved = true;
                        timeout.CancelAfter(completeBudget);
                    }
                    var signal = notices.Reader.ReadAsync(timeout.Token).AsTask();
                    if (await Task.WhenAny(signal, session.ProcessingCompletion).WaitAsync(timeout.Token) == session.ProcessingCompletion)
                        return ReadAggregate().IsComplete;
                    await signal;
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested) { return false; }
            finally { timeout.Cancel(); notices.Writer.TryComplete(); }
        }
    }

    private void StartNativeProcessInventorySeed(ResidencySessionState session)
    {
        if (!session.Native.Session.IsRealtime && !hasExplicitInventorySource) return;
        lock (subscriptionGate)
        {
            if (!IsCurrentSessionCore(session) || session.InventorySeedTask is not null) return;
            session.InventorySeedTask = Task.Run(() =>
            {
                while (true)
                {
                    int expectedLoss;
                    lock (subscriptionGate)
                    {
                        if (!IsCurrentSessionCore(session))
                        {
                            session.InventorySeedTask = null;
                            return;
                        }
                        expectedLoss = session.EventsLost;
                    }
                    try
                    {
                        var inventory = new InventorySeed(subscriptionGate,
                            () => IsCurrentSession(session, session.BindingEpoch) && Volatile.Read(ref session.EventsLost) == expectedLoss,
                            (pid, birth, name) => KernelEtwNativeReaders.SeedProcess(session.Native.Session, pid, birth, name, expectedLoss),
                            (pid, birth, tid) => KernelEtwNativeReaders.SeedThread(session.Native.Session, pid, birth, tid, expectedLoss),
                            () => session.Native.Session.RequestRefresh(Environment.TickCount64));
                        seedProcessInventory(inventory, inventory.IsCurrent);
                    }
                    catch (Exception error) { logger.LogWarning(error, "Failed to seed native ETW CPU process inventory."); }
                    lock (subscriptionGate)
                    {
                        if (IsCurrentSessionCore(session) && session.EventsLost != expectedLoss) continue;
                        // Checking for another loss and releasing the scan obligation
                        // are atomic, so a concurrent loss cannot lose its reseed.
                        session.InventorySeedTask = null;
                        return;
                    }
                }
            }, CancellationToken.None);
        }
    }

    internal sealed class InventorySeed(object gate, Func<bool> current,
        Action<int, long, string> seedProcess, Action<int, long, int> seedThread, Action flush)
    {
        private readonly object seedGate = gate;
        private readonly Func<bool> isCurrent = current;
        private readonly Action<int, long, string> writeProcess = seedProcess;
        private readonly Action<int, long, int> writeThread = seedThread;
        private readonly Action flushCommands = flush;
        internal bool IsCurrent() => isCurrent();
        internal ProcessSeed? SeedProcess(int pid, long birth, string name)
        {
            lock (seedGate)
            {
                if (!IsCurrent()) return null;
                writeProcess(pid, birth, name);
                return new ProcessSeed(this, pid, birth);
            }
        }
        internal sealed class ProcessSeed(InventorySeed inventory, int pid, long birth)
        {
            internal bool SeedThread(int tid)
            {
                lock (inventory.seedGate)
                {
                    if (!inventory.IsCurrent()) return false;
                    inventory.writeThread(pid, birth, tid);
                    return true;
                }
            }
        }
        internal void Flush()
        {
            lock (seedGate)
                if (IsCurrent()) flushCommands();
        }
    }

    private static void SeedNativeInventory(InventorySeed inventory, Func<bool> shouldSeed)
    {
        VisitOwnedArray(Process.GetProcesses(), process =>
        {
            if (!shouldSeed()) return false;
            try
            {
                using var identity = WindowsNetworkEndpointProcessLease.Open(unchecked((uint)process.Id));
                if (identity is null || !identity.TryReadCreationFileTime(out var birth) || birth == 0 || birth > long.MaxValue) return true;
                var name = process.ProcessName;
                var handles = process.Threads.Cast<ProcessThread>().ToArray();
                var ids = new List<int>(handles.Length);
                if (!VisitOwnedArray(handles, thread => { if (!shouldSeed()) return false; ids.Add(thread.Id); return true; })) return false;
                if (!identity.TryReadCreationFileTime(out var after) || birth != after || !shouldSeed()) return shouldSeed();
                var seeded = inventory.SeedProcess(process.Id, (long)birth, name);
                if (seeded is null) return false;
                foreach (var tid in ids)
                {
                    if (!shouldSeed()) return false;
                    if (!seeded.SeedThread(tid)) return false;
                }
            }
            catch (System.ComponentModel.Win32Exception) { }
            catch (InvalidOperationException) when (process.HasExited) { }
            return true;
        });
        if (shouldSeed()) inventory.Flush();
    }
}
