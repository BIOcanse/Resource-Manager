using System.Runtime.InteropServices;
using System.Text.Json;
using ResourceManager.App.Infrastructure.Adaptation;
using ResourceManager.App.Infrastructure.Telemetry.Etw;
using Xunit;
using Xunit.Abstractions;

namespace Resource_Manager_APP.Tests;

/// <summary>Actual production configuration, actual Ledger pages, actual shared
/// create_with_allocator owner. The controlled input adapter borrows that owner.
/// No ETW provider, hardware, timer, process inventory or legacy cap-sized region.</summary>
public sealed unsafe class KernelEtwNativeCommittedMemoryTests(ITestOutputHelper output)
{
    private static NativeKernelEtwSessionConfig ProductionConfiguration()
    {
        var plan = HostManagerTestPlanFactory.CreatePlan(cpuTopology: HostManagerTestPlanFactory.CreateCpuTopology(2));
        var config = KernelEtwNativeSettings.CreateConfiguration(plan.KernelEtwLimits!, plan.CpuCoreResidency, 2);
        config.StampAbi(new(1, DateTimeOffset.UnixEpoch));
        return config;
    }

    [Fact]
    public void ActualLedgerCommitFollowsOutputsAndFiniteResidencyWindowAtLiveCardinality()
    {
        if (!OperatingSystem.IsWindows()) return;
        var config = ProductionConfiguration();
        Assert.Equal(65536u, config.Residency.MaximumPairs);
        using var fixture = new LedgerOwnerFixture(config);
        var rows = new List<object>();
        rows.Add(new { scenario = "no holder", usage = fixture.Allocation.GetUsage() });
        Assert.Equal(0ul, fixture.Allocation.GetUsage().CommittedBytes);
        fixture.Create();
        fixture.Outputs(KernelEtwNativeOutputs.Interrupts);
        fixture.Publish(0);
        var irq = fixture.Allocation.GetUsage();
        rows.Add(new { scenario = "IRQ only", usage = irq });
        Assert.True(irq.CommittedBytes > 0);
        Assert.Equal("VirtualQuery", irq.CommitMeasurement);
        Assert.All(irq.Owners.Where(value => value.Owner is not (0 or 2)), value => Assert.Equal(0ul, value.CommittedBytes));
        Assert.True(irq.CommittedBytes < config.Interrupts.ByteCap);

        fixture.Outputs(KernelEtwNativeOutputs.Residency | KernelEtwNativeOutputs.Interrupts | KernelEtwNativeOutputs.Network);
        fixture.ConfigureProcessors();
        fixture.SeedThreads(12000);
        fixture.Publish(0);
        var between = fixture.Diagnostics();
        Assert.Equal(12000ul, between.CurrentThreadCount);
        Assert.Equal(0ul, between.ActivePairCount);
        Assert.Equal(0ul, between.WindowStorageBytes);
        AssertWindowReturned(fixture.Allocation.GetUsage());
        rows.Add(new { scenario = "tray: CPU metadata + IRQ + network", usage = fixture.Allocation.GetUsage(), diagnostics = between });

        fixture.BeginWindow();
        fixture.ClosePairs(12000, 4056);
        fixture.Publish(2);
        var active = fixture.Diagnostics();
        Assert.Equal(16056ul, active.ActivePairCount);
        Assert.Equal(12000ul, active.CurrentThreadCount);
        Assert.Equal(0L, active.CapacityResetCount);
        Assert.True(active.WindowStorageBytes > 0);
        var activeUsage = fixture.Allocation.GetUsage();
        Assert.Contains(activeUsage.Tags, value => value.Owner == 1 && value.Scope == 1 && value.CommittedBytes > 0);
        rows.Add(new { scenario = "CPU window: 16056 pairs / 12000 threads", usage = activeUsage, diagnostics = active });

        // The cardinality measurement above is deliberately shorter than the
        // production five-second window. Its warming view has no retained rows.
        Assert.Equal(0, Native.AcquireResidency(fixture.Handle, out var warming, out _, out _, out _, out var warmingToken));
        try
        {
            Assert.Equal(0u, warming.Flags & 1);
            Assert.Equal(0u, warming.Count);
            Assert.All(fixture.Allocation.GetUsage().Tags.Where(value => value.Owner == 1 && value.Scope >= 2),
                value => Assert.Equal(0ul, value.CommittedBytes));
        }
        finally { Assert.Equal(0, Native.Release(fixture.Handle, warmingToken)); }

        // Create a separate complete, nonempty publication before testing a
        // holder's lifetime. Empty headers need no immutable child allocation.
        var completedTick = fixture.CompleteWindow();
        fixture.Publish(completedTick);
        Assert.Equal(0, Native.AcquireResidency(fixture.Handle, out var retained, out _, out _, out _, out var retainedToken));
        try
        {
            Assert.NotEqual(0ul, retainedToken);
            Assert.Equal(1u, retained.Flags & 1);
            Assert.True(retained.Count > 0);
            var retainedRows = new KernelEtwNativeReaders.BorrowedResidencyRecords(retained.Rows, retained.Count).ToArray();
            Assert.All(retainedRows, value => Assert.True(value.ExecutionTimeQpc > 0));
            var retainedBytes = fixture.Allocation.GetUsage().Tags
                .Where(value => value.Owner == 1 && value.Scope >= 2).Sum(value => checked((long)value.CommittedBytes));
            Assert.True(retainedBytes > 0);
            rows.Add(new { scenario = "complete CPU publication held", usage = fixture.Allocation.GetUsage(), rowCount = retained.Count });
            fixture.EndWindow(completedTick + 1);
            var ended = fixture.Diagnostics();
            Assert.Equal(0ul, ended.ActivePairCount);
            Assert.Equal(0ul, ended.WindowStorageBytes);
            Assert.Equal(12000ul, ended.CurrentThreadCount);
            AssertWindowReturned(fixture.Allocation.GetUsage());
            fixture.Outputs(KernelEtwNativeOutputs.None);
            fixture.Publish(completedTick + 1);
            Assert.Equal(retainedBytes, fixture.Allocation.GetUsage().Tags
                .Where(value => value.Owner == 1 && value.Scope >= 2).Sum(value => checked((long)value.CommittedBytes)));
            Assert.Equal(retainedRows, new KernelEtwNativeReaders.BorrowedResidencyRecords(retained.Rows, retained.Count).ToArray());
            Assert.All(fixture.Allocation.GetUsage().Owners.Where(value => value.Owner is not (0 or 1)),
                value => Assert.Equal(0ul, value.CommittedBytes));
            rows.Add(new { scenario = "outputs absent with CPU publication held", usage = fixture.Allocation.GetUsage() });
        }
        finally { Assert.Equal(0, Native.Release(fixture.Handle, retainedToken)); }
        fixture.Publish(completedTick + 1);
        Assert.All(fixture.Allocation.GetUsage().Owners.Where(value => value.Owner != 0), value => Assert.Equal(0ul, value.CommittedBytes));
        fixture.Destroy();
        var returned = fixture.Allocation.GetUsage();
        Assert.Equal(0ul, returned.CommittedBytes);
        Assert.Equal(0, returned.Allocations);
        Assert.Equal(0, returned.PendingReleases);
        rows.Add(new { scenario = "no holder after final return", usage = returned });
        output.WriteLine("native-committed-memory=" + JsonSerializer.Serialize(rows, new JsonSerializerOptions { IncludeFields = true }));
    }

    [Fact]
    public void CompiledProductPairCapacityAdmits13165ThreadsOnTwoProcessors()
    {
        if (!OperatingSystem.IsWindows()) return;
        var config = ProductionConfiguration();
        Assert.Equal(65536u, config.Residency.MaximumPairs);
        using var fixture = new LedgerOwnerFixture(config);
        fixture.Create();
        fixture.Outputs(KernelEtwNativeOutputs.Residency | KernelEtwNativeOutputs.Interrupts);
        fixture.ConfigureProcessors();
        fixture.SeedThreads(13165);
        fixture.BeginWindow();
        fixture.ClosePairs(13165, 13165);
        fixture.Publish(4);
        var diagnostics = fixture.Diagnostics();
        Assert.Equal(26330ul, diagnostics.ActivePairCount);
        Assert.Equal(13165ul, diagnostics.CurrentThreadCount);
        Assert.Equal(65536ul, diagnostics.MaximumActivePairs);
        Assert.Equal(0L, diagnostics.CapacityResetCount);
        Assert.Equal(0u, diagnostics.ResetReason);
        Assert.Equal(0, Native.Failure(fixture.Handle));
    }

    [Fact]
    public void OldPairCapResetsOnlyCpuAndKeepsSharedInterruptOutputAlive()
    {
        if (!OperatingSystem.IsWindows()) return;
        var config = ProductionConfiguration();
        config.Residency.MaximumPairs = 16384;
        using var fixture = new LedgerOwnerFixture(config);
        fixture.Create();
        fixture.Outputs(KernelEtwNativeOutputs.Residency | KernelEtwNativeOutputs.Interrupts);
        fixture.ConfigureProcessors();
        fixture.SeedThreads(13165);
        fixture.BeginWindow();
        fixture.ClosePairs(13165, 13165);
        fixture.Publish(4);
        var diagnostics = fixture.Diagnostics();
        Assert.True(diagnostics.CapacityResetCount > 0);
        Assert.Equal(1u, diagnostics.ResetReason); // active-pair-limit
        Assert.Equal(16384ul, diagnostics.ResetMaximumPairs);
        Assert.Equal(16384ul, diagnostics.ResetActivePairs);
        Assert.Equal(13165ul, diagnostics.ResetCurrentThreads);
        Assert.Equal(0, Native.Failure(fixture.Handle));
        // A retained IRQ publication is available after the CPU continuity reset.
        Assert.Equal(0, Native.AcquireInterrupts(fixture.Handle, out _, out _, out var token));
        Assert.NotEqual(0ul, token);
        Assert.Equal(0, Native.Release(fixture.Handle, token));
    }

    private static void AssertWindowReturned(KernelEtwAllocatorUsage usage)
        => Assert.All(usage.Tags.Where(value => value.Owner == 1 && value.Scope == 1), value => Assert.Equal(0ul, value.CommittedBytes));

    private sealed class LedgerOwnerFixture : IDisposable
    {
        private readonly ResourceManagerLedgerOwner ledger = new();
        private NativeKernelEtwSessionConfig config;
        internal NativeKernelEtwHostAllocator Allocation { get; }
        internal nint Handle { get; private set; }
        private long qpc = 100;
        internal LedgerOwnerFixture(NativeKernelEtwSessionConfig configuration)
        {
            config = configuration;
            Allocation = new NativeKernelEtwLedgerRegionProvider(ledger).CreateAllocator("kernel-etw-committed-memory", config.ByteCap);
            Allocation.ConfigureOwnerByteCaps([config.ByteCap, config.Residency.ByteCap, config.Interrupts.ByteCap,
                config.DiskByteCap, config.NetworkByteCap, config.FileByteCap, config.HistoryByteCap, config.ByteCap, config.ByteCap]);
        }
        internal void Create()
        {
            var callbacks = Allocation.Callbacks;
            Check(Native.Create(ref config, ref callbacks, out var handle));
            Handle = handle;
            Assert.NotEqual(0, Handle);
        }
        internal void Outputs(KernelEtwNativeOutputs outputs) => Check(Native.Outputs(Handle, (uint)outputs));
        internal void ConfigureProcessors()
        {
            int* ids = stackalloc int[] { 0, 1 };
            Check(Native.Configure(Handle, ids, 2));
            Check(Native.Drain(Handle, 0));
        }
        internal void SeedThreads(int count)
        {
            var process = new Input { Kind = 11, Flags = 1, ProcessId = 100, Start = 1 };
            Submit(process);
            for (var index = 0; index < count; index++)
                Submit(new Input { Kind = 12, ProcessId = 100, Start = 1, ThreadId = 1000 + index });
        }
        private void Submit(Input input)
        {
            Check(Native.Submit(Handle, &input, 1));
            Check(Native.Drain(Handle, 0));
        }
        internal void BeginWindow()
        {
            Check(Native.Enabled(Handle, 1, 1));
            Submit(new Input { Kind = 9, Qpc = 100 });
        }
        internal void ClosePairs(int firstProcessor, int secondProcessor)
        {
            // Window storage starts at S, once both expected processors have a known run.
            Switch(0, 0, 0);
            Switch(1, 0, 0);
            for (var processor = 0; processor < 2; processor++)
            {
                var oldTid = 0;
                var count = processor == 0 ? firstProcessor : secondProcessor;
                for (var index = 0; index < count; index++)
                {
                    var nextTid = 1000 + index;
                    Switch(processor, oldTid, nextTid);
                    oldTid = nextTid;
                }
                Switch(processor, oldTid, 0);
            }
        }
        internal long CompleteWindow()
        {
            Switch(0, 0, 1000);
            Switch(1, 0, 1000);
            qpc = checked(qpc + (long)((Int128)config.Residency.QpcFrequency * config.Residency.WindowMilliseconds / 1000));
            Switch(0, 1000, 0);
            Switch(1, 1000, 0);
            return checked((long)((Int128)qpc * 1000 / config.Residency.QpcFrequency));
        }
        private void Switch(int processor, int oldTid, int nextTid)
        {
            ++qpc;
            var input = new Input { Kind = 7, Qpc = qpc,
                TimeTicks = DateTimeOffset.UnixEpoch.UtcTicks + checked((long)((Int128)qpc * TimeSpan.TicksPerSecond / config.Residency.QpcFrequency)),
                Processor = processor, OldProcessId = oldTid == 0 ? 0 : 100, OldThreadId = oldTid,
                ProcessId = nextTid == 0 ? 0 : 100, ThreadId = nextTid };
            Check(Native.CSwitch(Handle, checked((long)((Int128)qpc * 1000 / config.Residency.QpcFrequency)), ref input));
        }
        internal void Publish(long tick) => Check(Native.Publish(Handle, tick));
        internal void EndWindow(long tick)
        {
            Check(Native.Enabled(Handle, 1, 0));
            Publish(tick);
        }
        internal AggregationDiagnostics Diagnostics()
        {
            Check(Native.Diagnostics(Handle, out var diagnostics, checked((uint)sizeof(AggregationDiagnostics))));
            return diagnostics;
        }
        internal void Destroy()
        {
            if (Handle == 0) return;
            Check(Native.Destroy(Handle));
            Handle = 0;
        }
        public void Dispose()
        {
            Destroy(); // Callback context is retained until all native blocks returned.
            Allocation.Dispose();
            ledger.Dispose();
        }
        private void Check(int result)
            => Assert.True(result == 0, $"Native status {result}; allocator failure: {Allocation.LastFailure}");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
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
    [StructLayout(LayoutKind.Sequential)]
    private struct AggregationDiagnostics
    {
        public long QpcFrequency, WindowQpc;
        public ulong MaximumActivePairs, ActivePairCount, SlotsPerWindow, RingSlots;
        public ulong PairRecordBytes, WindowStorageBytes, MaximumWindowStorageBytes;
        public ulong CurrentProcessCount, CurrentThreadCount, RetainedProcessIdentityCount, RetainedThreadIdentityCount;
        public long ContinuityEpoch, CapacityResetCount;
        public uint ResetReason, Reserved;
        public long ResetEventQpc, ResetWindowStartQpc;
        public ulong ResetActivePairs, ResetMaximumPairs, ResetRequiredSlotSpan, ResetRingSlots;
        public ulong ResetCurrentProcesses, ResetCurrentThreads;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct InterruptView
    {
        public ulong Generation, EventsLost;
        public long StartedTick, EndedTick, WindowMilliseconds;
        public uint ProcessorCount, Flags;
        public double TotalDuration, MaximumDuration, CapacityPercent;
        public ulong EventCount, LongCount;
        public uint MaximumKind, Reserved;
        public nint MaximumModulePath, MaximumModuleName;
        public uint MaximumModulePathLength, MaximumModuleNameLength;
        public uint DriverCount, BucketCount;
        public nint Drivers, Buckets;
        public ulong CurrentBytes, PeakBytes;
    }
    private static class Native
    {
        private const string Library = "ResourceManager.NativeCore";
        [DllImport(Library, EntryPoint = "rm_kernel_etw_shared_create_with_allocator", CallingConvention = CallingConvention.Cdecl)] internal static extern int Create(ref NativeKernelEtwSessionConfig config, ref NativeKernelEtwHostAllocatorCallbacks callbacks, out nint owner);
        [DllImport(Library, EntryPoint = "rm_kernel_etw_shared_set_outputs", CallingConvention = CallingConvention.Cdecl)] internal static extern int Outputs(nint owner, uint mask);
        [DllImport(Library, EntryPoint = "rm_kernel_etw_shared_configure_processors", CallingConvention = CallingConvention.Cdecl)] internal static extern int Configure(nint owner, int* ids, uint count);
        [DllImport(Library, EntryPoint = "rm_kernel_etw_shared_set_enabled", CallingConvention = CallingConvention.Cdecl)] internal static extern int Enabled(nint owner, uint mask, uint value);
        [DllImport(Library, EntryPoint = "rm_kernel_etw_shared_submit", CallingConvention = CallingConvention.Cdecl)] internal static extern int Submit(nint owner, Input* input, uint count);
        [DllImport(Library, EntryPoint = "rm_kernel_etw_shared_query_residency_diagnostics", CallingConvention = CallingConvention.Cdecl)] internal static extern int Diagnostics(nint owner, out AggregationDiagnostics diagnostics, uint size);
        [DllImport(Library, EntryPoint = "rm_kernel_etw_shared_query_failure", CallingConvention = CallingConvention.Cdecl)] internal static extern int Failure(nint owner);
        [DllImport(Library, EntryPoint = "rm_kernel_etw_test_owned_drain", CallingConvention = CallingConvention.Cdecl)] internal static extern int Drain(nint owner, long tick);
        [DllImport(Library, EntryPoint = "rm_kernel_etw_test_owned_publish", CallingConvention = CallingConvention.Cdecl)] internal static extern int Publish(nint owner, long tick);
        [DllImport(Library, EntryPoint = "rm_kernel_etw_test_owned_cswitch", CallingConvention = CallingConvention.Cdecl)] internal static extern int CSwitch(nint owner, long tick, ref Input input);
        [DllImport(Library, EntryPoint = "rm_kernel_etw_shared_acquire_interrupts", CallingConvention = CallingConvention.Cdecl)] internal static extern int AcquireInterrupts(nint owner, out InterruptView view, out ulong sequence, out ulong token);
        [DllImport(Library, EntryPoint = "rm_kernel_etw_shared_acquire_residency", CallingConvention = CallingConvention.Cdecl)] internal static extern int AcquireResidency(nint owner, out KernelEtwNativeReaders.ResidencyView view, out ulong sequence, out ulong eventsLost, out ulong appliedTicket, out ulong token);
        [DllImport(Library, EntryPoint = "rm_kernel_etw_shared_release", CallingConvention = CallingConvention.Cdecl)] internal static extern int Release(nint owner, ulong token);
        [DllImport(Library, EntryPoint = "rm_kernel_etw_shared_destroy", CallingConvention = CallingConvention.Cdecl)] internal static extern int Destroy(nint owner);
    }
}
