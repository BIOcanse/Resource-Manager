using System.Runtime.InteropServices;
using Microsoft.Diagnostics.Tracing.Parsers;
using ResourceManager.App.Infrastructure.Telemetry.Etw;
using Resource_Manager_APP.Tests.KernelEtwFixtures;

namespace Resource_Manager_APP.Tests;

public sealed class NativeKernelEtwRecordedAnchorTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [EnvironmentVariableFact("RESOURCE_MANAGER_KERNEL_ETL_REPLAY_PATH")]
    public async Task RecordedResourceUsesTheRawHeaderAnchorForItsCompleteInterruptTimeline()
    {
        var path = Environment.GetEnvironmentVariable("RESOURCE_MANAGER_KERNEL_ETL_REPLAY_PATH");
        Assert.True(File.Exists(path), $"Owned IRQ input does not exist: {path}");
        // Independent Windows SDK pass, with no Zig decoder or TraceEvent dispatch anchor.
        var raw = KernelEtwZigInterruptEquivalenceTests.ReadRawClock(path!);
        Assert.True(raw.HeaderQpc > 0);
        const long startedTick = 1000;
        using var resource = new NativeKernelEtwRecordedTestResource(path!, metadata =>
        {
            var plan = HostManagerTestPlanFactory.CreatePlan();
            var configuration = KernelEtwNativeSettings.CreateConfiguration(plan.KernelEtwLimits!,
                plan.CpuCoreResidency, checked((uint)metadata.ProcessorCount));
            configuration.Residency.QpcFrequency = metadata.QpcFrequency;
            configuration.StartedTickMilliseconds = startedTick;
            return configuration;
        });
        using var metadataHold = resource.Acquire(new("recorded-anchor.irq-metadata",
            KernelTraceEventParser.Keywords.ImageLoad | KernelTraceEventParser.Keywords.Interrupt
                | KernelTraceEventParser.Keywords.DeferedProcedureCalls,
            (source, _) => KernelEtwNativeReaders.SetEnabled(source.NativeSession!, KernelEtwNativeReaders.Residency,
                enabled: false, refresh: false)) { NativeOutputs = KernelEtwNativeOutputs.Interrupts });
        using var durationHold = resource.Acquire(new("recorded-anchor.start-flow", KernelTraceEventParser.Keywords.ContextSwitch,
            static (_, _) => { }) { NativeOutputs = KernelEtwNativeOutputs.Interrupts });
        await resource.ProcessingCompletion.WaitAsync(TimeSpan.FromSeconds(60));
        resource.ThrowIfProcessingFailed();
        using var borrow = resource.Session.AcquireReadLease();
        Assert.Equal(0, Native.Status(borrow.OwnerHandle, out var status, checked((uint)Marshal.SizeOf<TraceStatus>())));
        Assert.Equal(0, Native.Clock(borrow.OwnerHandle, out var clock, checked((uint)Marshal.SizeOf<TraceClock>())));
        ulong sequence = 0;
        Assert.True(KernelEtwNativeReaders.ReadInterrupts(resource.Session, ref sequence,
            static view => new InterruptSummary(view.StartedTick, view.EndedTick, view.EventCount, view.EventsLost),
            out var summary, out var hasPublication));
        Assert.True(hasPublication);
        var irq = Assert.IsType<InterruptSummary>(summary);
        output.WriteLine($"Raw header={raw.HeaderQpc}; native header={clock.HeaderQpc}; records={status.Records}; "
            + $"rejected={status.Rejected}; eventsLost={status.EventsLost}; buffersLost={status.BuffersLost}; "
            + $"IRQ start={irq.StartedTick}; end={irq.EndedTick}; events={irq.Events}; origin={startedTick}.");
        Assert.Equal(raw.Frequency, resource.QpcFrequency);
        Assert.Equal(raw.Frequency, status.Frequency);
        Assert.Equal(raw.Frequency, clock.Frequency);
        Assert.Equal(raw.HeaderQpc, clock.HeaderQpc);
        Assert.Equal(raw.HeaderQpc, resource.Metadata.HeaderQpc);
        Assert.Equal(2u, clock.Flags & 3u);
        Assert.Equal(0u, status.WindowsStatus); Assert.Equal(0, status.OwnerStatus);
        Assert.Equal(raw.Records, status.Records);
        Assert.Equal(0UL, status.Rejected); Assert.Equal(0UL, raw.LostMarkers);
        Assert.Equal(0u, status.EventsLost); Assert.Equal(0u, status.BuffersLost);
        Assert.Equal(0UL, irq.EventsLost); Assert.True(irq.Events > 0);
        Assert.InRange(irq.EndedTick, startedTick, startedTick + 20_000);
    }

    private sealed record InterruptSummary(long StartedTick, long EndedTick, ulong Events, ulong EventsLost);
    [StructLayout(LayoutKind.Sequential)] private struct TraceClock
    { public long Frequency, HeaderQpc, QpcBefore, QpcAfter, Filetime; public uint Flags, Reserved; }
    [StructLayout(LayoutKind.Sequential)] private struct TraceStatus
    {
        public uint WindowsStatus; public int OwnerStatus;
        public ulong Records, Decoded, Rejected, Unhandled, Switches;
        public uint EventsLost, BuffersLost; public long Frequency; public uint ProcessorCount, BufferSize;
    }
    private static class Native
    {
        private const string Library = "ResourceManager.NativeCore";
        [DllImport(Library, EntryPoint = "rm_kernel_etw_shared_status", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int Status(nint owner, out TraceStatus status, uint size);
        [DllImport(Library, EntryPoint = "rm_kernel_etw_shared_clock", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int Clock(nint owner, out TraceClock clock, uint size);
    }
}
