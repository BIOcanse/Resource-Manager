using System.Diagnostics;
using System.Reflection;
using Microsoft.Diagnostics.Tracing.Parsers;
using ResourceManager.App.Infrastructure.CpuTopology;
using ResourceManager.App.Infrastructure.Telemetry.Etw;
using ResourceManager.ReadWrite.Windows.Processes;
using Resource_Manager_APP.Tests.KernelEtwFixtures;

namespace Resource_Manager_APP.Tests;

internal static class NativeCpuReaderTestFeed
{
    internal static EtwCpuResidencyCaptureOwner Owner(EtwCpuCoreResidencyReader reader)
        => (EtwCpuResidencyCaptureOwner)typeof(EtwCpuCoreResidencyReader)
            .GetField("captureOwner", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(reader)!;
    internal static NativeKernelEtwTestResource Resource(EtwCpuResidencyCaptureOwner owner)
        => ((INativeKernelEtwTestResourceBroker)typeof(EtwCpuResidencyCaptureOwner)
            .GetField("kernelEtwBroker", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!).Resource;
    internal static void PublishCommands(EtwCpuCoreResidencyReader reader)
    {
        var owner = Owner(reader);
        var gate = typeof(EtwCpuResidencyCaptureOwner).GetField("durationResourceGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
        lock (gate)
        {
            var native = Resource(owner);
            native.Publish(native.Tick); // A controlled buffer boundary; no execution event is fabricated.
        }
    }
    internal static async Task DrainRequestedCommandsUntilAsync(EtwCpuCoreResidencyReader reader, Task operation,
        CancellationToken cancellationToken)
    {
        while (!operation.IsCompleted)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PublishRequestedCommands(reader);
            if (!operation.IsCompleted)
                await Task.WhenAny(operation, Task.Delay(5, cancellationToken));
        }
    }
    private static void PublishRequestedCommands(EtwCpuCoreResidencyReader reader)
    {
        var owner = typeof(EtwCpuCoreResidencyReader).GetField("captureOwner",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(reader) as EtwCpuResidencyCaptureOwner;
        if (owner is null) return;
        var ownerType = typeof(EtwCpuResidencyCaptureOwner);
        var resourceGate = ownerType.GetField("durationResourceGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
        lock (resourceGate)
        {
            object? session;
            var subscriptionGate = ownerType.GetField("subscriptionGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
            lock (subscriptionGate)
                session = ownerType.GetField("activeSession", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner);
            if (session is null) return;
            var nativeReader = session.GetType().GetProperty("Native")!.GetValue(session)!;
            var readerType = nativeReader.GetType();
            ulong ticket;
            NativeKernelEtwSession native;
            lock (readerType.GetField("gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(nativeReader)!)
            {
                ticket = (ulong)readerType.GetField("requiredTicket", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(nativeReader)!;
                native = (NativeKernelEtwSession)readerType.GetProperty("Session", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(nativeReader)!;
            }
            var resource = Resource(owner);
            if (ticket == 0 || !ReferenceEquals(resource.Session, native)
                || KernelEtwNativeReaders.IsResidencyApplied(native, ticket)) return;
            // Observe the existing business request; never submit a sample from
            // this helper. Only the missing consumer buffer boundary is supplied.
            resource.Publish(resource.Tick);
        }
    }
    internal static long ClosedWindow(EtwCpuCoreResidencyReader reader,
        HostManagerSamplingSubscriptionTestFixture sampling, int pid = 100, long birth = 1000,
        int firstTid = 100, long previousEnd = 0, bool publishPrelude = true)
    {
        var plan = sampling.Provider.Current.HostManager.CpuCoreResidency;
        return ClosedWindow(Owner(reader), plan.PhysicalCoreByLogicalProcessor.Keys.Order().ToArray(),
            plan.ObservationWindowMilliseconds, pid, birth, firstTid, previousEnd, publishPrelude: publishPrelude);
    }
    internal static long ClosedWindow(EtwCpuResidencyCaptureOwner owner, int[] processors,
        int windowMilliseconds, int pid = 100, long birth = 1000, int firstTid = 100, long previousEnd = 0,
        long? startQpc = null, bool publishPrelude = true, bool seedIdentities = true)
    {
        // Serialize fixture input with the product's explicit resource operation.
        // IsWindowActive can become visible before its duration Acquire returns.
        var gate = typeof(EtwCpuResidencyCaptureOwner).GetField("durationResourceGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
        lock (gate) return ClosedWindowCore(owner, processors, windowMilliseconds, pid, birth, firstTid, previousEnd, startQpc, publishPrelude, seedIdentities);
    }
    private static long ClosedWindowCore(EtwCpuResidencyCaptureOwner owner, int[] processors,
        int windowMilliseconds, int pid, long birth, int firstTid, long previousEnd, long? startQpc, bool publishPrelude, bool seedIdentities)
    {
        var native = Resource(owner);
        // Each controlled raw hook drains pending commands before input. Some
        // tests separately need a published command-only warming bank; the
        // complete-first-item case deliberately publishes only its closed input.
        if (publishPrelude) native.Publish(native.Tick);
        var start = startQpc ?? Math.Max(Stopwatch.GetTimestamp() + 1, previousEnd + 1);
        var at = DateTimeOffset.UtcNow;
        var lost = native.GetSnapshot().EventsLost;
        if (seedIdentities)
        {
            native.SeedProcess(pid, birth, "recorded", lost);
            for (var i = 0; i < processors.Length; i++) native.SeedThread(pid, birth, firstTid + i, lost);
        }
        for (var i = 0; i < processors.Length; i++)
            native.CSwitch(start, at, i, 0, 0, pid, firstTid + i, "recorded");
        var end = checked(start + Stopwatch.Frequency * windowMilliseconds / 1000);
        for (var i = 0; i < processors.Length; i++)
        {
            try { native.CSwitch(end, at.AddMilliseconds(windowMilliseconds), i, pid, firstTid + i, 0, 0); }
            catch (Exception error)
            {
                var session = typeof(EtwCpuResidencyCaptureOwner).GetField("activeSession",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
                var plan = (CpuResidencyCaptureConfiguration)session.GetType().GetProperty("Plan")!.GetValue(session)!;
                var frequency = native.QpcFrequency;
                var width = checked((frequency * plan.BucketWidthMilliseconds + 999) / 1000);
                var window = checked((frequency * windowMilliseconds + 999) / 1000);
                var buckets = checked((window + width - 1) / width + 2);
                var initialRetainedStart = Math.Max(0, start - window);
                var span = checked((end - 1) / width - initialRetainedStart / width + 1);
                throw new InvalidOperationException($"Controlled CPU close failed: rawIndex={i}; logicalId={processors[i]}; "
                    + $"openedProcessors={processors.Length}; priorCloses={i}; startQpc={start}; endQpc={end}; frequency={frequency}; "
                    + $"bucketQpc={width}; initialRetainedSpanBuckets={span}; bucketCount={buckets}; "
                    + $"planPairLimit={plan.MaximumActivePairs}; bucketSlotBudget={plan.MaximumActivePairs * buckets}; "
                    + $"processorCap={native.Configuration.Residency.MaximumProcessors}; threadCap={native.Configuration.Residency.MaximumThreads}; "
                    + $"cellCap={native.Configuration.Residency.MaximumCells}.", error);
            }
        }
        native.Publish(native.Tick + windowMilliseconds);
        return end;
    }
}

/// <summary>Counts real resource holds; all capture state and published banks belong to NativeCore.</summary>
internal class CpuResidencyHeldTestBroker : IKernelEtwSessionBroker, INativeKernelEtwTestResourceBroker, IDisposable
{
    internal const KernelTraceEventParser.Keywords MetadataKeywords = KernelTraceEventParser.Keywords.Process | KernelTraceEventParser.Keywords.Thread;
    private readonly bool bindOnWorker;
    public NativeKernelEtwTestResource Resource { get; }
    public int Acquires, Releases, MetadataAcquires, MetadataReleases, DurationAcquires, DurationReleases;
    public Action? BeforeLeaseReturn;
    public TaskCompletionSource Bound { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource<bool> BindingCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task? LastBinding { get; private set; }
    public int SubscribeCount => Volatile.Read(ref Acquires);
    public int DisposeCount => Volatile.Read(ref Releases);
    public int ActiveCount => SubscribeCount - DisposeCount;
    public int DurationCount => Volatile.Read(ref DurationAcquires) - Volatile.Read(ref DurationReleases);
    public int SessionStarts => Resource.Creations;
    public List<long> Generations { get; } = [];
    public KernelTraceEventParser.Keywords Keywords => GetSnapshot().EnabledKeywords;
    internal CpuResidencyHeldTestBroker(long generation = 7, DateTimeOffset? startedAt = null, bool bindOnWorker = false,
        NativeKernelEtwSessionConfig? configuration = null,
        NativeKernelEtwTestResource.DiagnosticSourceMode diagnosticSource = NativeKernelEtwTestResource.DiagnosticSourceMode.Recorded)
    {
        // A native owner's generation is supplied by the real session owner.
        // The legacy fake's requested number is intentionally not forged.
        _ = generation;
        this.bindOnWorker = bindOnWorker;
        Resource = new(configuration ?? NativeKernelEtwTestResource.SmallConfiguration(), startedAt ?? DateTimeOffset.UtcNow,
            diagnosticSource: diagnosticSource);
    }
    public IKernelEtwSubscription Acquire(KernelEtwSubscriptionRequest request)
    {
        var duration = request.Keywords.HasFlag(KernelTraceEventParser.Keywords.ContextSwitch);
        Assert.Equal(duration ? KernelTraceEventParser.Keywords.ContextSwitch : MetadataKeywords, request.Keywords);
        var original = request.Bind;
        var held = Resource.Acquire(request with { Bind = (source, context) =>
        {
            Generations.Add(context.Generation);
            if (!bindOnWorker) original(source, context);
            else
            {
                var caller = Environment.CurrentManagedThreadId;
                LastBinding = Task.Factory.StartNew(() => { Assert.NotEqual(caller, Environment.CurrentManagedThreadId); original(source, context); },
                    CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                var completed = LastBinding.Wait(TimeSpan.FromSeconds(1));
                BindingCompleted.TrySetResult(completed);
                if (!completed) throw new TimeoutException("The reader held its lock across resource acquisition.");
            }
        }});
        Interlocked.Increment(ref Acquires);
        if (duration) Interlocked.Increment(ref DurationAcquires); else Interlocked.Increment(ref MetadataAcquires);
        if (!duration) { Bound.TrySetResult(); BeforeLeaseReturn?.Invoke(); }
        return new Lease(this, held, duration);
    }
    public virtual KernelEtwSessionSnapshot GetSnapshot() => Resource.GetSnapshot();
    internal void Rebind() => Resource.Rebind();
    public void Stop() => Resource.Dispose();
    public void Dispose() => Resource.Dispose();
    private sealed class Lease(CpuResidencyHeldTestBroker owner, IKernelEtwSubscription held, bool duration) : IKernelEtwSubscription
    {
        private int released;
        public KernelEtwSessionSnapshot GetSnapshot() => owner.GetSnapshot();
        public void Dispose()
        {
            if (Interlocked.Exchange(ref released, 1) != 0) return;
            held.Dispose();
            Interlocked.Increment(ref owner.Releases);
            if (duration) Interlocked.Increment(ref owner.DurationReleases); else Interlocked.Increment(ref owner.MetadataReleases);
        }
    }
}
