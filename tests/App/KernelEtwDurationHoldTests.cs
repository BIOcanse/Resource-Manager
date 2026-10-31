using System.Collections.Frozen;
using System.Reflection;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.SystemHealth.Interrupts;
using ResourceManager.ReadWrite.Windows.Processes;

namespace Resource_Manager_APP.Tests;

public sealed class KernelEtwDurationHoldTests
{
    private const KernelTraceEventParser.Keywords CpuMetadata = KernelTraceEventParser.Keywords.Process | KernelTraceEventParser.Keywords.Thread;
    private const KernelTraceEventParser.Keywords IrqDuration = KernelTraceEventParser.Keywords.Interrupt | KernelTraceEventParser.Keywords.DeferedProcedureCalls;

    [Fact]
    public void CpuWindowReturnsOnlyDurationKeywordsWithoutRebindingMetadataOrRestartingTheSession()
    {
        using var broker = new RecordingNativeKernelEtwBroker();
        using var capture = new EtwCpuResidencyCaptureOwner(broker, Configuration);
        Assert.Null(capture.Read()); Assert.Equal(0, broker.Resource.Creations);
        capture.EnsureStarted();
        Assert.Equal(CpuMetadata, broker.GetSnapshot().EnabledKeywords);
        Assert.Equal(1, broker.GetSnapshot().SubscriptionCount);
        Assert.False(capture.IsWindowActive);
        capture.BeginWindow(TimeSpan.FromSeconds(1));
        Assert.Equal(CpuMetadata | KernelTraceEventParser.Keywords.ContextSwitch, broker.GetSnapshot().EnabledKeywords);
        Assert.Equal(2, broker.GetSnapshot().SubscriptionCount);
        Assert.True(capture.IsWindowActive);
        var generation = broker.GetSnapshot().Generation;
        var session = broker.Resource.Session;
        Assert.Same(session, broker.Sources["cpu-core-residency"].NativeSession);
        Assert.Same(session, broker.Sources["cpu-core-residency-duration"].NativeSession);
        NativeCpuReaderTestFeed.ClosedWindow(capture, [0], 1000);
        Assert.True(capture.Collect()); Assert.NotEmpty(capture.Read()!.Processes);
        for (var cycle = 0; cycle < 3; cycle++)
        {
            capture.PauseWindow();
            Assert.Equal(CpuMetadata, broker.GetSnapshot().EnabledKeywords);
            Assert.Equal(1, broker.GetSnapshot().SubscriptionCount);
            var current = capture.ReadObservation();
            for (var read = 0; read < 3; read++) Assert.Same(current, capture.ReadObservation());
            Assert.Equal(CpuMetadata, broker.GetSnapshot().EnabledKeywords);
            capture.BeginWindow(TimeSpan.FromSeconds(1)); capture.BeginWindow(TimeSpan.FromSeconds(1));
            Assert.Equal(CpuMetadata | KernelTraceEventParser.Keywords.ContextSwitch, broker.GetSnapshot().EnabledKeywords);
            Assert.Equal(2, broker.GetSnapshot().SubscriptionCount);
            Assert.Equal(generation, broker.GetSnapshot().Generation);
            Assert.Equal(1, broker.Binds["cpu-core-residency"]);
            Assert.Same(session, broker.Resource.Session);
            Assert.Equal(1, broker.Resource.Creations); Assert.Equal(0, broker.Resource.Stops);
        }
        capture.PauseWindow();
        Assert.Equal(CpuMetadata, broker.GetSnapshot().EnabledKeywords);
        capture.BeginWindow(TimeSpan.FromSeconds(1));
        Assert.Equal(CpuMetadata | KernelTraceEventParser.Keywords.ContextSwitch, broker.GetSnapshot().EnabledKeywords);
        capture.Stop();
        Assert.Equal(KernelTraceEventParser.Keywords.None, broker.GetSnapshot().EnabledKeywords);
        Assert.Equal(1, broker.Resource.Stops); Assert.Equal(0UL, broker.Resource.RegionUsage.CurrentBytes);
    }

    [Fact]
    public void IdleCpuCaptureKeepsMetadataAndContinuousWindowsKeepOneDurationHold()
    {
        using var broker = new RecordingNativeKernelEtwBroker();
        using var capture = new EtwCpuResidencyCaptureOwner(broker, Configuration);
        capture.EnsureStarted();
        Assert.Equal(CpuMetadata, broker.GetSnapshot().EnabledKeywords);
        Assert.Equal(1, broker.GetSnapshot().SubscriptionCount);
        capture.BeginWindow(TimeSpan.FromSeconds(1));
        for (var window = 0; window < 4; window++) capture.BeginWindow(TimeSpan.FromSeconds(1));
        Assert.Equal(2, broker.GetSnapshot().SubscriptionCount);
        Assert.Equal(1, broker.Binds["cpu-core-residency-duration"]);
        Assert.Equal(1, broker.Binds["cpu-core-residency"]); Assert.Equal(1, broker.Resource.Creations);
    }

    private static CpuResidencyCaptureConfiguration Configuration() => new(1000, 64,
        new Dictionary<int, CpuResidencyCoreAccounting> { [0] = new("core:0", "", 1, 1) }.ToFrozenDictionary(), "native hold fixture");
}
