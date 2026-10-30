using Microsoft.Diagnostics.Tracing.Parsers;
using ResourceManager.App.Infrastructure.Telemetry.Etw;
using Resource_Manager_APP.Tests.KernelEtwFixtures;
using System.Runtime.InteropServices;

namespace Resource_Manager_APP.Tests;

public sealed class NativeKernelEtwTestResourceTests
{
    [Fact]
    public void IndependentControlledFlowsKeepTheirOwnDiagnosticCounters()
    {
        using var left = new NativeKernelEtwTestResource(NativeKernelEtwTestResource.SmallConfiguration(1000, 1));
        using var right = new NativeKernelEtwTestResource(NativeKernelEtwTestResource.SmallConfiguration(1000, 1));
        using var leftHold = left.Acquire(new("left", KernelTraceEventParser.Keywords.Process,
            static (source, _) => source.ConsumerCounter("ProcessStartGroup")) { NativeOutputs = KernelEtwNativeOutputs.Residency });
        using var rightHold = right.Acquire(new("right", KernelTraceEventParser.Keywords.Process,
            static (source, _) => source.ConsumerCounter("renderer-invalidation")) { NativeOutputs = KernelEtwNativeOutputs.Renderer });
        Assert.NotEqual(left.DiagnosticName, right.DiagnosticName);
        left.RawProcess(1, 41, 0, "left.exe");
        right.RawProcess(1, 0, 0, "invalid.exe");
        var sessions = EtwEventDropDiagnostics.Read().Sessions;
        var first = Assert.Single(sessions, value => value.Name == left.DiagnosticName);
        var second = Assert.Single(sessions, value => value.Name == right.DiagnosticName);
        var accepted = Assert.Single(first.Kinds, value => value.Kind == "holder/left/ProcessStartGroup");
        var rejected = Assert.Single(second.Kinds, value => value.Kind == "holder/right/renderer-invalidation");
        Assert.Equal((1L, 0L), (accepted.Received, accepted.Dropped));
        Assert.Equal((1L, 1L), (rejected.Received, rejected.Dropped));
        Assert.DoesNotContain(first.Kinds, value => value.Kind.StartsWith("holder/right/", StringComparison.Ordinal));
        Assert.DoesNotContain(second.Kinds, value => value.Kind.StartsWith("holder/left/", StringComparison.Ordinal));
    }

    [Fact]
    public void RawRecordsProduceActualFileNotificationsAndRendererRevisions()
    {
        using var resource = new NativeKernelEtwTestResource(NativeKernelEtwTestResource.SmallConfiguration(1000, 1));
        KernelEtwEventSource? outputs = null;
        using var hold = resource.Acquire(new("raw", KernelTraceEventParser.Keywords.Process | KernelTraceEventParser.Keywords.FileIO
            | KernelTraceEventParser.Keywords.FileIOInit | KernelTraceEventParser.Keywords.ImageLoad,
            (source, _) => outputs = source) { NativeOutputs = KernelEtwNativeOutputs.File | KernelEtwNativeOutputs.Renderer | KernelEtwNativeOutputs.Notifications });
        resource.RawProcess(0, 41, 2, "fixture.exe", 17);
        resource.RawFileName(1, 100, @"C:\fixture.bin");
        resource.RawFileWrite(2, 41, 51, 0, 100, 4096);
        resource.RawImage(3, 41, 4096, 256, @"C:\fixture.dll");
        resource.Publish(3);
        var records = new List<(uint Kind, string? Path)>();
        ulong sequence = 0;
        Assert.True(KernelEtwNativeNotifications.Read(resource.Session, ref sequence, (view, gap) =>
        {
            Assert.False(gap);
            foreach (var record in KernelEtwNativeNotifications.Records(view))
                records.Add((record.Kind, record.Path == 0 ? null : Marshal.PtrToStringUni(record.Path, checked((int)record.PathLength))));
        }));
        Assert.Contains((7u, "fixture.exe"), records);
        Assert.Contains((4u, @"C:\fixture.bin"), records);
        Assert.Contains((9u, @"C:\fixture.dll"), records);
        var revision = KernelEtwNativeRendererHistory.Read(outputs!, 41)!.Value;
        Assert.Equal(2UL, revision.Value);
        resource.Loss(4, 1);
        resource.Publish(4);
        var lost = KernelEtwNativeRendererHistory.Read(outputs!, 41)!.Value;
        Assert.True(lost.Epoch > revision.Epoch);
        Assert.Equal(1UL, lost.EventsLost); Assert.Equal(0UL, lost.Value);
        resource.Loss(5, 1);
        resource.Publish(5);
        Assert.Equal(lost, KernelEtwNativeRendererHistory.Read(outputs!, 41)!.Value);
        resource.Loss(6, 2);
        resource.Publish(6);
        var nextLoss = KernelEtwNativeRendererHistory.Read(outputs!, 41)!.Value;
        Assert.True(nextLoss.Epoch > lost.Epoch);
        Assert.Equal(2UL, nextLoss.EventsLost); Assert.Equal(0UL, nextLoss.Value);
    }

    [Fact]
    public void ScriptedDiagnosticSourceSamplesOnlyAnExplicitCommandAndUsageReadsItsPublishedBank()
    {
        const long now = 133_000_000_000_000_000;
        using var resource = new NativeKernelEtwTestResource(NativeKernelEtwTestResource.SmallConfiguration(),
            diagnosticSource: NativeKernelEtwTestResource.DiagnosticSourceMode.Scripted);
        using var hold = resource.Acquire(new("metadata", KernelTraceEventParser.Keywords.Process | KernelTraceEventParser.Keywords.Thread,
            static (_, _) => { }) { NativeOutputs = KernelEtwNativeOutputs.Residency });
        resource.Publish(0);
        Assert.Equal(new NativeKernelEtwTestResource.DiagnosticUsageSnapshot(0, 0, 0), resource.DiagnosticUsage());
        resource.ConfigureDiagnosticScript(now, [new(41, now - 20_000_000)],
            [new(41, 51, now - 10_000_000, Kernel: 100, User: 200, Description: "scripted", StartAddress: 4096, ModulePath: @"C:\fixture.dll")]);
        resource.Submit(new NativeKernelEtwTestResource.Input { Kind = 13, Unique = 1, ProcessId = 41, Start = now - 20_000_000, TimeTicks = now });
        resource.Publish(1);
        var registered = resource.DiagnosticUsage();
        Assert.True(registered.NativeReads > 0); Assert.Equal(3UL, registered.OpenHandles);
        var inputUsage = resource.ScriptedInputUsage()!.Value;
        Assert.Equal(resource.Configuration.Residency.ByteCap, inputUsage.ByteCap);
        Assert.True(inputUsage.CurrentBytes > 0);
        Assert.InRange(inputUsage.PeakBytes, inputUsage.CurrentBytes, inputUsage.ByteCap);
        Assert.Equal(0UL, inputUsage.FailedAllocations);
        resource.ConfigureDiagnosticScript(now + 1_000_000, [],
            [new(41, 51, now - 10_000_000, Kernel: 150, User: 280, Description: "scripted", StartAddress: 4096, ModulePath: @"C:\fixture.dll")]);
        resource.Publish(2);
        Assert.Equal(registered.NativeReads, resource.DiagnosticUsage().NativeReads);
        resource.Submit(new NativeKernelEtwTestResource.Input { Kind = 19, Unique = 1, TimeTicks = now, Qpc = now + 1_000_000 });
        resource.Publish(3);
        Assert.True(resource.DiagnosticUsage().NativeReads > registered.NativeReads);
        resource.Submit(new NativeKernelEtwTestResource.Input { Kind = 14, Unique = 1 });
        resource.Publish(4);
        Assert.Equal(0UL, resource.DiagnosticUsage().OpenHandles);
    }

    [Fact]
    public void ActualOwnerRetainsMetadataUntilLastReturnAndRebindsOnlyAtNewGeneration()
    {
        using var resource = new NativeKernelEtwTestResource(NativeKernelEtwTestResource.SmallConfiguration());
        KernelEtwEventSource? metadata = null, duration = null;
        var metadataBinds = 0;
        var metadataLease = resource.Acquire(new("metadata", KernelTraceEventParser.Keywords.Process | KernelTraceEventParser.Keywords.Thread,
            (source, _) => { metadata = source; metadataBinds++; }) { NativeOutputs = KernelEtwNativeOutputs.Residency });
        var first = resource.Generation;
        var durationLease = resource.Acquire(new("duration", KernelTraceEventParser.Keywords.ContextSwitch,
            (source, _) => duration = source) { NativeOutputs = KernelEtwNativeOutputs.Residency });
        Assert.Equal(1, resource.Creations);
        Assert.Equal(first, resource.Generation);
        Assert.Equal(1, metadataBinds);
        Assert.Same(resource.Session, metadata!.NativeSession);
        Assert.False(resource.Session.IsRealtime);
        Assert.Null(resource.ScriptedInputUsage());
        Assert.Equal(2, resource.GetSnapshot().SubscriptionCount);
        Assert.True((resource.GetSnapshot().EnabledKeywords & KernelTraceEventParser.Keywords.ContextSwitch) != 0);
        durationLease.Dispose();
        Assert.False(duration!.IsActive);
        Assert.True(metadata!.IsActive);
        Assert.Equal(0, resource.Stops);
        Assert.Equal(KernelTraceEventParser.Keywords.Process | KernelTraceEventParser.Keywords.Thread, resource.GetSnapshot().EnabledKeywords);
        var oldMetadata = metadata;
        resource.Rebind();
        Assert.Equal(2, metadataBinds);
        Assert.False(oldMetadata!.IsActive);
        Assert.True(metadata!.IsActive);
        Assert.NotEqual(first, resource.Generation);
        Assert.Equal(2, resource.Creations);
        metadataLease.Dispose();
        Assert.Equal(0UL, resource.RegionUsage.CurrentBytes);
        Assert.Equal(2, resource.Stops);
        Assert.Equal(0, resource.ActiveIdentityHandles);
    }

    [Fact]
    public void RealPublicationSignalsReadersAndRetainedReadDelaysNativeAndRegionDestruction()
    {
        using var resource = new NativeKernelEtwTestResource(NativeKernelEtwTestResource.SmallConfiguration());
        var runtime = resource.RuntimeFactory.Create("controlled", KernelTraceEventParser.Keywords.Process,
            new(1, DateTimeOffset.UnixEpoch), [new(1, "read", KernelTraceEventParser.Keywords.Process, (_, _) => { })
                { NativeOutputs = KernelEtwNativeOutputs.Residency }]);
        runtime.StartProcessing(_ => { });
        var session = resource.Session;
        var signals = 0;
        using var observer = session.ObservePublications(() => signals++);
        resource.SeedProcess(41, 100, "fixture");
        resource.SeedThread(41, 100, 51);
        resource.Publish(100);
        Assert.Equal(1, signals);
        Assert.Equal(7, session.ReadOwnerUsage()!.Count);
        Assert.True(session.ReadRegionUsage()!.Value.LiveAllocations > 0);
        var retained = session.AcquireReadLease();
        var failed = runtime.Stop(TimeSpan.Zero);
        Assert.False(failed.IsReleased);
        Assert.True(resource.RegionUsage.CurrentBytes > 0);
        Assert.True(session.ProcessingCompletion.IsCompleted);
        Assert.Throws<ObjectDisposedException>(() => session.AcquireReadLease());
        retained.Dispose();
        Assert.True(runtime.Stop(TimeSpan.Zero).IsReleased);
        Assert.Equal(0UL, resource.RegionUsage.CurrentBytes);
        runtime.Dispose();
    }
}
