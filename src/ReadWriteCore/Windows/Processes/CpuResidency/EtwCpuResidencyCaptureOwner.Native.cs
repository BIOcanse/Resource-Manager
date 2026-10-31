using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using ResourceManager.App.Domain.CpuTopology;
using ResourceManager.App.Infrastructure.CpuTopology;
using ResourceManager.App.Infrastructure.Telemetry.Etw;
using ResourceManager.ReadWrite.Windows.Devices;
using static ResourceManager.ReadWrite.Windows.Processes.WindowsProcessSystemThreadParser;

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
        // 0 = finite window; n >= 1 = continuous window with n slots. Every window begin re-issues it.
        private int slotsPerWindow;
        internal int SlotsPerWindow { get { lock (gate) return slotsPerWindow; } }
        private CpuResidencyAggregationSnapshot warming = new(Stopwatch.Frequency, 0, 0, null, false, []);
        internal NativeKernelEtwSession Session => session;
        internal long WindowStartQpc { get { lock (gate) return minimumWindowQpc; } }
        internal CpuResidencyAggregationDiagnostics? GetDiagnostics()
            => KernelEtwNativeReaders.ReadResidencyDiagnostics(session);

        internal void Configure(int[] ids, bool enabled, int slots)
        {
            lock (gate)
            {
                minimumWindowQpc = session.IsRealtime ? Stopwatch.GetTimestamp() : 0;
                slotsPerWindow = slots;
                ClearProjection();
                KernelEtwNativeReaders.ConfigureResidency(session, plan.ObservationWindowMilliseconds, plan.MaximumActivePairs);
                KernelEtwNativeReaders.ConfigureProcessors(session, ids);
                KernelEtwNativeReaders.SetEnabled(session, KernelEtwNativeReaders.Residency, enabled, refresh: false);
                requiredTicket = KernelEtwNativeReaders.ResetContinuity(session, minimumWindowQpc, slotsPerWindow);
                session.RequestRefresh(Environment.TickCount64);
            }
        }
        internal void SetEnabled(bool enabled) => KernelEtwNativeReaders.SetEnabled(session, KernelEtwNativeReaders.Residency, enabled);
        internal void BeginWindow(int slots)
        {
            lock (gate)
            {
                slotsPerWindow = slots;
                ResetContinuity();
            }
        }
        internal void ResetContinuity()
        {
            lock (gate)
            {
                minimumWindowQpc = session.IsRealtime ? Stopwatch.GetTimestamp() : 0;
                ClearProjection();
                requiredTicket = KernelEtwNativeReaders.ResetContinuity(session, minimumWindowQpc, slotsPerWindow);
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
                            (pid, birth, name, threadIds) => KernelEtwNativeReaders.SeedProcess(
                                session.Native.Session, pid, birth, name, threadIds, expectedLoss),
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

    internal delegate void ProcessSeedWriter(int pid, long birth, ReadOnlySpan<char> name, ReadOnlySpan<int> threadIds);

    internal sealed class InventorySeed(object gate, Func<bool> current, ProcessSeedWriter seedProcess, Action flush)
    {
        private readonly object seedGate = gate;
        private readonly Func<bool> isCurrent = current;
        private readonly ProcessSeedWriter writeProcess = seedProcess;
        private readonly Action flushCommands = flush;
        internal bool IsCurrent() => isCurrent();
        /// <summary>Writes one process and its threads as one native batch. False when this scan is no longer current.</summary>
        internal bool SeedProcess(int pid, long birth, ReadOnlySpan<char> name, ReadOnlySpan<int> threadIds)
        {
            lock (seedGate)
            {
                if (!IsCurrent()) return false;
                writeProcess(pid, birth, name, threadIds);
                return true;
            }
        }
        internal void Flush()
        {
            lock (seedGate)
                if (IsCurrent()) flushCommands();
        }
    }

    // Thread ids per process that fit on the stack; a larger process rents one array reused for the rest of the seed.
    private const int SeedStackThreads = 256;

    // One SystemProcessInformation table for the whole seed: it grows on a length
    // mismatch, is read in place (no Process objects, no handle per process or
    // thread) and is freed when the seed returns. The table is one consistent
    // snapshot, so each process's CreateTime and thread list belong together.
    private static unsafe void SeedNativeInventory(InventorySeed inventory, Func<bool> shouldSeed)
    {
        if (!shouldSeed()) return;
        using var table = new WindowsProcessInventoryBuffer { ReturnPolicy = static () => new(true, 0) };
        if (!table.TryRead(out var data, out _)) return;
        var headerSize = sizeof(SystemProcessInformationHeader);
        var threadSize = sizeof(SystemThreadInformation);
        int[]? rented = null;
        Span<int> stackIds = stackalloc int[SeedStackThreads];
        try
        {
            fixed (byte* start = data)
            {
                var offset = 0;
                while (offset <= data.Length - headerSize && offset % IntPtr.Size == 0)
                {
                    if (!shouldSeed()) return;
                    var header = MemoryMarshal.Read<SystemProcessInformationHeader>(data.Slice(offset, headerSize));
                    var entryBytes = header.NextEntryOffset == 0 ? data.Length - offset : (long)header.NextEntryOffset;
                    if (entryBytes < headerSize || entryBytes > data.Length - offset) return;
                    var pid = header.UniqueProcessId.ToInt64();
                    // pid 0 is System Idle: it owns no real threads to attribute.
                    if (pid is > 0 and <= int.MaxValue && header.CreationTime > 0
                        && header.NumberOfThreads <= (entryBytes - headerSize) / threadSize)
                    {
                        var name = pid == 4 ? "System".AsSpan() : ImageName(header.ImageName, start, data.Length);
                        var count = (int)header.NumberOfThreads;
                        if (count > stackIds.Length && (rented is null || rented.Length < count))
                        {
                            if (rented is not null) System.Buffers.ArrayPool<int>.Shared.Return(rented);
                            rented = System.Buffers.ArrayPool<int>.Shared.Rent(count);
                        }
                        var ids = count <= stackIds.Length ? stackIds[..count] : rented.AsSpan(0, count);
                        var threads = data.Slice(offset + headerSize, count * threadSize);
                        for (var index = 0; index < count; index++)
                            ids[index] = (int)MemoryMarshal.Read<SystemThreadInformation>(threads.Slice(index * threadSize, threadSize))
                                .ClientThreadId.ToInt64();
                        if (!inventory.SeedProcess((int)pid, header.CreationTime, name, ids)) return;
                    }
                    if (header.NextEntryOffset == 0) break;
                    offset += (int)header.NextEntryOffset;
                }
            }
        }
        finally { if (rented is not null) System.Buffers.ArrayPool<int>.Shared.Return(rented); }
        if (shouldSeed()) inventory.Flush();
    }

    // The image name without its executable extension, as Process.ProcessName reports it; empty when the
    // UNICODE_STRING does not lie inside the table.
    private static unsafe ReadOnlySpan<char> ImageName(UnicodeString image, byte* start, int length)
    {
        if (image.Length == 0 || (image.Length & 1) != 0 || image.Length > image.MaximumLength) return [];
        var imageOffset = unchecked((ulong)image.Buffer.ToInt64() - (ulong)start);
        if (imageOffset > (ulong)length || image.Length > (ulong)length - imageOffset || imageOffset % 2 != 0) return [];
        var characters = new ReadOnlySpan<char>(start + imageOffset, image.Length / 2);
        return characters.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? characters[..^4] : characters;
    }
}
