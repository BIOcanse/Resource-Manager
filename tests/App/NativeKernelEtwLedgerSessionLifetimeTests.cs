using System.Collections;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using ResourceManager.App.Infrastructure.Adaptation;
using ResourceManager.App.Infrastructure.Telemetry.Etw;

namespace Resource_Manager_APP.Tests;

public sealed class NativeKernelEtwLedgerSessionLifetimeTests
{
    [Fact]
    public void FailedRecordedStartReturnsEveryLedgerLeaseAndCommittedPage()
    {
        var plan = HostManagerTestPlanFactory.CreatePlan();
        var configuration = KernelEtwNativeSettings.CreateConfiguration(plan.KernelEtwLimits!, plan.CpuCoreResidency, 2);
        using var ledger = new ResourceManagerLedgerOwner();
        var allocations = new ObservedAllocationProvider(new NativeKernelEtwLedgerRegionProvider(ledger));
        var factory = new NativeKernelEtwSessionRuntimeFactory(allocations, () => configuration, static () => KernelEtwNativeSettings.CreateSessionBuffers(KernelEtwNativeSettings.ProductDefaults));
        var absentInput = Path.Combine(Path.GetTempPath(), $"etw-zig-never-created-{Guid.NewGuid():N}.etl");
        Assert.False(File.Exists(absentInput));
        Assert.Throws<InvalidOperationException>(() => factory.CreateRecorded(absentInput, new(1, DateTimeOffset.UnixEpoch),
            [new(1, "failed-start", KernelTraceEventParser.Keywords.ImageLoad, static (_, _) => { })
                { NativeOutputs = KernelEtwNativeOutputs.Interrupts }]));
        Assert.Equal(0, AcquiredScratchLeases(ledger));
        Assert.Empty(ledger.ReadWorkspaceCapacities());
        Assert.Equal(0UL, ledger.ReadTable().RegisteredBytes);
        var returned = Assert.IsType<KernelEtwAllocatorUsage>(allocations.Created!.GetUsage());
        Assert.Equal(0UL, returned.CommittedBytes);
        Assert.Equal(0, returned.Allocations);
        Assert.Equal(0, returned.PendingReleases);
    }

    [EnvironmentVariableFact("RESOURCE_MANAGER_KERNEL_ETL_REPLAY_PATH")]
    public async Task LastHoldReturnsAllLedgerLeasesAndCommittedPagesAndNextRunPublishesIdentically()
    {
        var path = Environment.GetEnvironmentVariable("RESOURCE_MANAGER_KERNEL_ETL_REPLAY_PATH")!;
        Assert.True(File.Exists(path), $"Owned IRQ input is absent: {path}");
        using var metadata = new ETWTraceEventSource(path);
        var frequency = Assert.IsType<long>(typeof(TraceEventSource).GetProperty("QPCFreq",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(metadata));
        var plan = HostManagerTestPlanFactory.CreatePlan();
        var configuration = KernelEtwNativeSettings.CreateConfiguration(plan.KernelEtwLimits!,
            plan.CpuCoreResidency, checked((uint)metadata.NumberOfProcessors));
        configuration.Residency.QpcFrequency = frequency;
        configuration.StartedTickMilliseconds = 1000;
        using var ledger = new ResourceManagerLedgerOwner();
        var native = new NativeKernelEtwSessionRuntimeFactory(new NativeKernelEtwLedgerRegionProvider(ledger), () => configuration,
            static () => KernelEtwNativeSettings.CreateSessionBuffers(KernelEtwNativeSettings.ProductDefaults));
        var recorded = new RecordedFactory(path, native);
        using var broker = new KernelEtwSessionOwner(recorded);
        string? first = null;
        for (var round = 0; round < 2; round++)
        {
            NativeKernelEtwSession? session = null;
            string publication;
            // The hold covers this finite replay and its final publication read.
            // The last return below must drain the real production runtime.
            using (broker.Acquire(new("ledger-session-lifetime", KernelTraceEventParser.Keywords.ImageLoad
                | KernelTraceEventParser.Keywords.Interrupt | KernelTraceEventParser.Keywords.DeferedProcedureCalls,
                (source, _) => session = source.NativeSession) { NativeOutputs = KernelEtwNativeOutputs.Interrupts }))
            {
                Assert.NotNull(session);
                await session.ProcessingCompletion.WaitAsync(TimeSpan.FromSeconds(60));
                Assert.Null(session.ProcessingFailure);
                Assert.Null(session.PublicationCallbackFailure);
                Assert.True(Assert.IsType<KernelEtwAllocatorUsage>(session.AllocationUsage).CommittedBytes > 0);
                Assert.True(AcquiredScratchLeases(ledger) > 0);
                ulong sequence = 0;
                Assert.True(KernelEtwNativeReaders.ReadInterrupts(session, ref sequence, CopyPublication,
                    out var value, out var available));
                Assert.True(available);
                publication = Assert.IsType<string>(value);
            }
            Assert.Equal(0, broker.GetSnapshot().SubscriptionCount);
            Assert.Equal("Idle", broker.GetSnapshot().State);
            Assert.Equal(0, AcquiredScratchLeases(ledger));
            Assert.Empty(ledger.ReadWorkspaceCapacities());
            Assert.Equal(0UL, ledger.ReadTable().RegisteredBytes);
            var returned = Assert.IsType<KernelEtwAllocatorUsage>(session!.AllocationUsage);
            Assert.Equal(0UL, returned.CommittedBytes);
            Assert.Equal(0, returned.Allocations);
            Assert.Equal(0, returned.PendingReleases);
            if (round == 0) first = publication;
            else Assert.Equal(first, publication);
        }
        Assert.Equal(2, recorded.Creations);
    }

    private static int AcquiredScratchLeases(ResourceManagerLedgerOwner ledger)
    {
        // Inspect the real product owner's counted scope references, rather than
        // inferring lease release from the allocator's requested-byte counter.
        var resources = Assert.IsAssignableFrom<IEnumerable>(typeof(ResourceManagerLedgerOwner)
            .GetField("computeScratch", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(ledger));
        return resources.Cast<object>().Sum(resource => Assert.IsType<int>(resource.GetType()
            .GetField("Active", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(resource)));
    }

    private static string CopyPublication(KernelEtwNativeReaders.InterruptView view)
    {
        Assert.Equal(1u, view.Flags & 1);
        Assert.True(view.EventCount > 0);
        // This finite recording is shorter than the IRQ rolling-window limit.
        // Its publication must start at the configured replay origin, not boot time.
        Assert.Equal(1000L, view.StartedTick);
        Assert.Equal(view.WindowMilliseconds, view.EndedTick - view.StartedTick);
        var drivers = new object[view.DriverCount];
        for (var index = 0; index < drivers.Length; index++)
        {
            var row = Marshal.PtrToStructure<KernelEtwNativeReaders.InterruptDriver>(view.Drivers
                + index * Marshal.SizeOf<KernelEtwNativeReaders.InterruptDriver>());
            drivers[index] = new { module = KernelEtwNativeReaders.Utf16(row.Module.Path, row.Module.PathLength),
                name = KernelEtwNativeReaders.Utf16(row.Module.Name, row.Module.NameLength), row.TotalDuration,
                row.MaximumDuration, row.CapacityPercent, row.EventCount, row.LongCount };
        }
        var buckets = new KernelEtwNativeReaders.InterruptBucket[view.BucketCount];
        for (var index = 0; index < buckets.Length; index++)
            buckets[index] = Marshal.PtrToStructure<KernelEtwNativeReaders.InterruptBucket>(view.Buckets
                + index * Marshal.SizeOf<KernelEtwNativeReaders.InterruptBucket>());
        // Generation is the distinct session incarnation; every business field
        // and every nested publication row must be equal across the two runs.
        return JsonSerializer.Serialize(new { view.EventsLost, view.StartedTick, view.EndedTick,
            view.WindowMilliseconds, view.ProcessorCount, view.Flags, view.TotalDuration, view.MaximumDuration,
            view.CapacityPercent, view.EventCount, view.LongCount, view.MaximumKind,
            maximumModule = KernelEtwNativeReaders.Utf16(view.MaximumModule.Path, view.MaximumModule.PathLength),
            maximumName = KernelEtwNativeReaders.Utf16(view.MaximumModule.Name, view.MaximumModule.NameLength), drivers, buckets },
            new JsonSerializerOptions { IncludeFields = true });
    }

    private sealed class RecordedFactory(string path, NativeKernelEtwSessionRuntimeFactory native) : IKernelEtwSessionRuntimeFactory
    {
        internal int Creations;
        public void ValidateRequest(KernelEtwSubscriptionRequest request) => native.ValidateRequest(request);
        public bool TryGetAvailability(out string state, out string message)
        { state = "Available"; message = "Finite recorded input; no provider or elevation."; return true; }
        public IKernelEtwSessionRuntime Create(string name, KernelTraceEventParser.Keywords keywords,
            KernelEtwSessionContext context, IReadOnlyList<KernelEtwSubscriptionRegistration> subscriptions)
        { Creations++; return native.CreateRecorded(path, context, subscriptions); }
    }

    private sealed class ObservedAllocationProvider(NativeKernelEtwLedgerRegionProvider inner)
        : IKernelEtwRegionProvider, IKernelEtwAllocationProvider
    {
        internal NativeKernelEtwHostAllocator? Created;
        public NativeKernelEtwHostAllocator CreateAllocator(string sessionName, ulong byteCap)
            => Created = inner.CreateAllocator(sessionName, byteCap);
        public IKernelEtwRegionLease Allocate(string owner, nuint minimumLength) => inner.Allocate(owner, minimumLength);
    }
}
