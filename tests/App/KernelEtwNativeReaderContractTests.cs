using System.Runtime.InteropServices;
using System.Collections.Frozen;
using System.Reflection;
using ResourceManager.App.Infrastructure.CpuTopology;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.Telemetry.Etw;
using ResourceManager.App.Infrastructure.SystemHealth.Interrupts;
using ResourceManager.ReadWrite.Windows.Processes;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Domain.SystemHealth;
using ResourceManager.App.Infrastructure.Monitoring;
using Resource_Manager_APP.Tests.KernelEtwFixtures;

namespace Resource_Manager_APP.Tests;

public sealed class KernelEtwNativeReaderContractTests
{
    [Fact]
    public void CpuProcessorSelectionDoesNotChangeTheCaptureProcessorCountOrInterruptCapacity()
    {
        using var resource = new NativeKernelEtwTestResource(
            NativeKernelEtwTestResource.SmallConfiguration(qpcFrequency: 1000, maximumProcessors: 4));
        using var hold = resource.Acquire(new("cpu-and-interrupt-capacity",
            KernelTraceEventParser.Keywords.Process | KernelTraceEventParser.Keywords.Thread
            | KernelTraceEventParser.Keywords.ContextSwitch | KernelTraceEventParser.Keywords.ImageLoad
            | KernelTraceEventParser.Keywords.Interrupt | KernelTraceEventParser.Keywords.DeferedProcedureCalls,
            static (_, _) => { })
        { NativeOutputs = KernelEtwNativeOutputs.Residency | KernelEtwNativeOutputs.Interrupts });
        KernelEtwNativeReaders.ConfigureProcessors(resource.Session, [0]);
        resource.Publish(0);
        resource.Interrupt(1, 1, 1, 0);
        resource.Publish(10000);
        ulong sequence = 0;
        Assert.True(KernelEtwNativeReaders.ReadInterrupts(resource.Session, ref sequence,
            static view => (view.ProcessorCount, view.CapacityPercent, view.EventCount),
            out var observed, out var available));
        Assert.True(available);
        Assert.Equal(1UL, observed.EventCount);
        Assert.Equal(4u, observed.ProcessorCount);
        Assert.Equal(0.0025, observed.CapacityPercent, 10);
    }

    [Fact]
    public async Task InterruptBindingCannotAcceptThePreBindCompleteBankAtTheSameTick()
    {
        using var fixture = new KernelEtwZigIoEquivalenceTests.SharedFixture();
        fixture.Publish(11000); // An actual complete bank exists before this reader binds.
        using var sampling = new HostManagerSamplingSubscriptionTestFixture();
        var broker = new InterruptLossBroker(fixture.Session);
        var now = DateTimeOffset.UnixEpoch.AddMilliseconds(11000);
        using var source = new EtwSystemInterruptSnapshotSource(broker, sampling.Owner,
            NullLogger<EtwSystemInterruptSnapshotSource>.Instance, () => now, static () => 11000);
        InvokeInterrupt(source, "EnsureStarted"); // Its reset is staged but not yet consumed.
        var publicationSignals = 0;
        using var observation = fixture.Session.ObservePublications(() => Interlocked.Increment(ref publicationSignals));
        var operation = (Task<bool>)InvokeInterrupt(source, "TryPublishCurrentSnapshotAsync", now,
            new SamplingOwnerToken(1, 1), TimeSpan.FromSeconds(5), CancellationToken.None)!;
        Assert.False(operation.IsCompleted, "A pre-bind bank at the same tick cannot acknowledge this reader's reset.");
        fixture.Publish(11000); // Drain the actual reset at that exact same tick.
        Assert.Equal(1, Volatile.Read(ref publicationSignals));
        Assert.True(await operation.WaitAsync(TimeSpan.FromSeconds(5))); // Accepted warming still means sampled.
        Assert.False(source.Read().Available);
        Assert.Equal(SystemInterruptProviderStates.Warming, source.Read().ProviderState.State);
        Assert.Equal(0, source.Read().EventCount);
        Assert.Empty(source.Read().Drivers);
    }

    [Fact]
    public void NativeInterruptBankBehindBrokerLossPublishesWarmingWithTheActualLossCount()
    {
        using var fixture = new KernelEtwZigIoEquivalenceTests.SharedFixture();
        using var sampling = new HostManagerSamplingSubscriptionTestFixture();
        var broker = new InterruptLossBroker(fixture.Session);
        using var source = new EtwSystemInterruptSnapshotSource(broker, sampling.Owner,
            NullLogger<EtwSystemInterruptSnapshotSource>.Instance, () => DateTimeOffset.UnixEpoch, static () => 1000);
        InvokeInterrupt(source, "EnsureStarted");
        fixture.Publish(11000);
        Assert.True(PublishInterrupt(source)); Assert.True(source.Read().Available);
        broker.Lost = 1; // The real native bank is still the preceding complete bank.
        Assert.False(PublishInterrupt(source));
        Assert.False(source.Read().Available);
        Assert.Equal(SystemInterruptProviderStates.Warming, source.Read().ProviderState.State);
        Assert.Equal(1, source.Read().ProviderState.EventsLost);
        Assert.Empty(source.Read().Drivers);
    }

    [Fact]
    public void LateBrokerLossClearsThePreviouslyPublishedCompleteNativeInterruptWindow()
    {
        using var fixture = new KernelEtwZigIoEquivalenceTests.SharedFixture();
        using var sampling = new HostManagerSamplingSubscriptionTestFixture();
        var broker = new InterruptLossBroker(fixture.Session);
        using var source = new EtwSystemInterruptSnapshotSource(broker, sampling.Owner,
            NullLogger<EtwSystemInterruptSnapshotSource>.Instance, () => DateTimeOffset.UnixEpoch, static () => 1000);
        InvokeInterrupt(source, "EnsureStarted");
        fixture.Publish(11000);
        Assert.True(PublishInterrupt(source));
        var previous = source.Read(); Assert.True(previous.Available);
        var changeAt = broker.Queries + 1;
        broker.AfterSnapshot = count =>
        {
            if (count != changeAt) return;
            broker.Lost = 1; broker.AfterSnapshot = null;
        };
        // The before-status receipt is still loss0; the after-status receipt
        // sees loss1 after the actual native candidate/cache was already read.
        Assert.False(PublishInterrupt(source));
        Assert.False(source.Read().Available);
        Assert.NotSame(previous, source.Read());
        Assert.Equal(SystemInterruptProviderStates.Warming, source.Read().ProviderState.State);
        Assert.Equal(1, source.Read().ProviderState.EventsLost);
        Assert.Empty(source.Read().Drivers);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public unsafe void RecordedNativeReaderUsesItsRecordedClockAfterConfigureOrReset(bool reset)
    {
        using var fixture = new KernelEtwZigIoEquivalenceTests.SharedFixture();
        Assert.False(fixture.Session.IsRealtime);
        var source = new KernelEtwEventSource(fixture.Session);
        var type = typeof(EtwCpuResidencyCaptureOwner).GetNestedType("NativeResidencyReader", BindingFlags.NonPublic)!;
        var reader = Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null, [fixture.Session, RecordedConfiguration(), 1L, source], null)!;
        type.GetMethod("Configure", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(reader, [new[] { 0 }, true]);
        if (reset) type.GetMethod("ResetContinuity", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(reader, null);
        fixture.Publish(1000); // The tracked map/config/reset barrier is now applied.
        var at = DateTimeOffset.UnixEpoch;
        RecordedInput[] inputs =
        [
            new() { Kind = 11, Flags = 1, ProcessId = 100, Start = 1000 },
            new() { Kind = 12, ProcessId = 100, Start = 1000, ThreadId = 10 },
            new() { Kind = 7, Qpc = 0, TimeTicks = at.UtcTicks, ProcessId = 100, ThreadId = 10, Processor = 0 },
            new() { Kind = 7, Qpc = 1000, TimeTicks = at.AddSeconds(1).UtcTicks, OldProcessId = 100, OldThreadId = 10, Processor = 0 }
        ];
        using (var owner = fixture.Session.AcquireReadLease())
        fixed (RecordedInput* pointer = inputs)
            Assert.Equal(0, SubmitRecorded(owner.OwnerHandle, pointer, checked((uint)inputs.Length)));
        fixture.Publish(2000);
        var aggregate = Assert.IsType<CpuResidencyAggregationSnapshot>(
            type.GetMethod("ReadAggregate", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(reader, null));
        Assert.True(aggregate.IsComplete, "A recorded QPC window must not be rejected against today's Stopwatch timestamp.");
        Assert.Equal(1000, aggregate.QpcFrequency);
        Assert.Equal(0, aggregate.WindowStartQpc);
        Assert.Equal(1000, aggregate.MeasuredThroughQpc);
    }

    [Fact]
    public void RecordedNativeBindingDoesNotCreateALiveProcessInventorySeedTask()
    {
        using var fixture = new KernelEtwZigIoEquivalenceTests.SharedFixture();
        Assert.False(fixture.Session.IsRealtime);
        using var owner = new EtwCpuResidencyCaptureOwner(new UnstartedBroker(), RecordedConfiguration);
        var source = new KernelEtwEventSource(fixture.Session);
        var ownerType = typeof(EtwCpuResidencyCaptureOwner);
        var sessionType = ownerType.GetNestedType("ResidencySessionState", BindingFlags.NonPublic)!;
        var session = Activator.CreateInstance(sessionType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null, [1L, 0L, RecordedConfiguration(), fixture.Session, source], null)!;
        var gate = ownerType.GetField("subscriptionGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
        var active = ownerType.GetField("activeSession", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Task? attempted;
        lock (gate)
        {
            active.SetValue(owner, session);
            try
            {
                ownerType.GetMethod("StartNativeProcessInventorySeed", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(owner, [session]);
                // Holding this same gate prevents an incorrectly started task's
                // finally from clearing the evidence before we inspect it.
                attempted = (Task?)sessionType.GetField("InventorySeedTask")!.GetValue(session);
            }
            finally { active.SetValue(owner, null); }
        }
        attempted?.GetAwaiter().GetResult(); // Drain the old behavior even when this assertion fails.
        Assert.Null(attempted);
    }

    [Fact]
    public void IncompleteNativeInterruptWindowPublishesTheExistingWarmingState()
    {
        var now = DateTimeOffset.Parse("2026-10-28T12:00:00Z");
        var snapshot = EtwSystemInterruptSnapshotSource.CopyNativeSnapshot(now,
            new KernelEtwNativeReaders.InterruptView
            {
                Generation = 7, EndedTick = 3000, WindowMilliseconds = 3000,
                ProcessorCount = 2, Flags = 0, EventsLost = 2
            });
        Assert.False(snapshot.Available);
        Assert.Equal(ResourceManager.App.Domain.SystemHealth.SystemInterruptProviderStates.Warming,
            snapshot.ProviderState.State);
        Assert.Equal(2, snapshot.ProviderState.EventsLost);
        Assert.Equal(now, snapshot.CapturedAt);
        Assert.Equal(TimeSpan.FromSeconds(3), snapshot.Window);
        Assert.Empty(snapshot.Drivers);
        Assert.Equal(0, snapshot.EventCount);
    }

    [Fact]
    public void PendingDiagnosticRequestWaitsForItsExplicitSourceWithoutLegacyReadsOrSessionAcquisition()
    {
        var broker = new UnstartedBroker();
        using var owner = new EtwCpuResidencyCaptureOwner(broker,
            () => throw new InvalidOperationException("No capture plan is read before the controlled run starts."));
        using var request = owner.RequestThreadDiagnostics(123, 456);
        Assert.Null(request.ReadWindow(null));
        Assert.Equal(1, owner.DiagnosticRequestCount);
        Assert.False(owner.HasSessionLease);
        Assert.Null(owner.Read());
        Assert.Equal(0, broker.Acquires);
    }

    [Fact]
    public void PublishedRowLayoutsMatchTheNativeX64Contract()
    {
        Assert.Equal(8, IntPtr.Size);
        Assert.Equal(64, Marshal.SizeOf<KernelEtwNativeReaders.ResidencyView>());
        Assert.Equal(64, Marshal.SizeOf<KernelEtwNativeReaders.ResidencyRow>());
        Assert.Equal(160, Marshal.SizeOf<KernelEtwNativeReaders.InterruptView>());
        Assert.Equal(64, Marshal.SizeOf<KernelEtwNativeReaders.InterruptDriver>());
        Assert.Equal(40, Marshal.SizeOf<KernelEtwNativeReaders.InterruptBucket>());
        Assert.Equal(112, Marshal.SizeOf<KernelEtwNativeReaders.DiagnosticThreadView>());
        Assert.Equal(64, Marshal.SizeOf<KernelEtwNativeReaders.DiagnosticRequestView>());
    }

    [Fact]
    public unsafe void InterruptDomainCopyRetainsTextAndClosedBucketTimesAfterNativeStorageChanges()
    {
        var now = DateTimeOffset.Parse("2026-10-28T12:00:00Z");
        var text = "driver.sys".ToCharArray();
        fixed (char* name = text)
        {
            var driver = new KernelEtwNativeReaders.InterruptDriver
            {
                Module = new() { Name = (nint)name, NameLength = checked((uint)text.Length) },
                EventCount = 3, LongCount = 1, TotalDuration = 2, MaximumDuration = 1.25, CapacityPercent = 0.01
            };
            var bucket = new KernelEtwNativeReaders.InterruptBucket
            { StartedTick = 10000, EndedTick = 20000, EventCount = 3, TotalDuration = 2, CapacityPercent = 0.01 };
            var view = new KernelEtwNativeReaders.InterruptView
            {
                Generation = 7, EndedTick = 20000, WindowMilliseconds = 10000, ProcessorCount = 2, Flags = 1,
                MaximumKind = 1, MaximumModule = driver.Module, DriverCount = 1, BucketCount = 1,
                Drivers = (nint)(&driver), Buckets = (nint)(&bucket), EventCount = 3, LongCount = 1,
                TotalDuration = 2, MaximumDuration = 1.25, CapacityPercent = 0.01
            };
            var snapshot = EtwSystemInterruptSnapshotSource.CopyNativeSnapshot(now, view);
            text.AsSpan().Fill('x'); driver.EventCount = 0; bucket.EventCount = 0;
            Assert.Equal("driver.sys", snapshot.MaximumDriverName);
            Assert.Equal("driver.sys", Assert.Single(snapshot.Drivers).ModuleName);
            Assert.Equal(3, snapshot.EventCount);
            Assert.Equal(3, Assert.Single(snapshot.Buckets).EventCount);
            Assert.Equal(now.AddSeconds(-10), snapshot.Buckets[0].StartedAt);
            Assert.Equal(now, snapshot.Buckets[0].EndedAt);
        }
    }

    [Fact]
    public unsafe void ThreadDiagnosticCopyKeepsMetadataCountersAndExplicitNativeFailure()
    {
        var name = "worker".ToCharArray();
        fixed (char* text = name)
        {
            var row = new KernelEtwNativeReaders.DiagnosticThreadView
            {
                Flags = 1 | 4 | 8, Birth = 100, Start = 123, StartSource = 2,
                ReadFrom = 100, ReadThrough = 200, KernelDelta = 20, UserDelta = 30,
                Description = (nint)text, DescriptionLength = checked((uint)name.Length)
            };
            var facts = KernelEtwNativeReaders.CopyDiagnosticFacts(row);
            name.AsSpan().Fill('x');
            Assert.Equal("worker", facts.Description);
            Assert.Equal(CpuThreadStartAddressSource.SystemProcessInformation, facts.StartAddressSource);
            Assert.Equal(20, facts.Counters!.KernelTime100Ns);
            Assert.Equal(30, facts.Counters.UserTime100Ns);
            var failed = KernelEtwNativeReaders.CopyDiagnosticFacts(new() { Flags = 16, NativeError = 5 });
            Assert.Null(failed.CreationFileTime100Ns);
            Assert.Null(failed.Counters);
            Assert.Equal(5, failed.NativeError);
        }
    }

    private static bool PublishInterrupt(EtwSystemInterruptSnapshotSource source)
        => (bool)InvokeInterrupt(source, "TryPublishCurrentSnapshot", DateTimeOffset.UnixEpoch, new SamplingOwnerToken(1, 1))!;
    private static object? InvokeInterrupt(EtwSystemInterruptSnapshotSource source, string method, params object[] arguments)
        => typeof(EtwSystemInterruptSnapshotSource).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(source, arguments);

    private sealed class InterruptLossBroker(NativeKernelEtwSession session) : IKernelEtwSessionBroker
    {
        private readonly Dictionary<long, KernelEtwSubscriptionRequest> holds = [];
        private long next;
        internal int Lost, Queries;
        internal Action<int>? AfterSnapshot;
        public IKernelEtwSubscription Acquire(KernelEtwSubscriptionRequest request)
        {
            var id = ++next;
            var source = new KernelEtwEventSource(session, $"{request.Id}-{id}", request.NativeOutputs, 1,
                static (name, read, supplement) => new(name, read, supplement));
            holds.Add(id, request);
            try { request.Bind(source, new(1, DateTimeOffset.UnixEpoch)); source.Activate(); }
            catch { holds.Remove(id); source.Cancel(); throw; }
            var observation = session.ObservePublications(() => source.DispatchPublication());
            return new Held(this, id, source, observation);
        }
        public KernelEtwSessionSnapshot GetSnapshot()
        {
            var receipt = new KernelEtwSessionSnapshot("Running", "controlled native loss receipt", 1,
                DateTimeOffset.UnixEpoch, holds.Values.Aggregate(KernelTraceEventParser.Keywords.None,
                    static (all, held) => all | held.Keywords), holds.Count, Lost);
            AfterSnapshot?.Invoke(++Queries);
            return receipt;
        }
        private sealed class Held(InterruptLossBroker owner, long id, KernelEtwEventSource source,
            IDisposable observation) : IKernelEtwSubscription
        {
            private int disposed;
            public KernelEtwSessionSnapshot GetSnapshot() => owner.GetSnapshot();
            public void Dispose()
            {
                if (Interlocked.Exchange(ref disposed, 1) != 0) return;
                observation.Dispose(); source.Cancel(); owner.holds.Remove(id);
            }
        }
    }

    private static CpuResidencyCaptureConfiguration RecordedConfiguration() => new(1000, 32,
        new Dictionary<int, CpuResidencyCoreAccounting> { [0] = new("core:0", "", 1, 1) }.ToFrozenDictionary(),
        "recorded native contract", 50);

    [StructLayout(LayoutKind.Sequential)]
    private struct RecordedInput
    {
        public uint Kind, Flags;
        public long Qpc, TimeTicks;
        public int ProcessId, ThreadId, OldProcessId, OldThreadId, Processor;
        public uint Reserved;
        public ulong Unique;
        public long Start;
        public nint Name;
        public uint NameLength, Reserved2;
    }
    [DllImport("ResourceManager.NativeCore", EntryPoint = "rm_kernel_etw_shared_submit", CallingConvention = CallingConvention.Cdecl)]
    private static extern unsafe int SubmitRecorded(nint owner, RecordedInput* inputs, uint count);

    private sealed class UnstartedBroker : IKernelEtwSessionBroker
    {
        public int Acquires;
        public IKernelEtwSubscription Acquire(KernelEtwSubscriptionRequest request)
        { Acquires++; throw new InvalidOperationException("Reading or registering metadata must not acquire an ETW resource."); }
        public KernelEtwSessionSnapshot GetSnapshot()
            => new("Idle", "", 0, DateTimeOffset.MinValue, KernelTraceEventParser.Keywords.None, 0, 0);
    }
}
