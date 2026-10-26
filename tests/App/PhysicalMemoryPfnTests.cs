using System.Runtime.InteropServices;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Infrastructure.ResourceBreakdown;

namespace Resource_Manager_APP.Tests;

public sealed class PhysicalMemoryPfnTests
{
    [Theory]
    [InlineData("memory.usage", true)]
    [InlineData("process_all_private_working_set_actual", true)]
    [InlineData("cpu.usage", false)]
    public void DetailsReuseOnlyASelectedPublishedPfnValueWithItsOwnTime(string metric, bool selected)
    {
        var at = At.AddSeconds(2);
        var residualMetrics = new HashSet<string> { "memory.usage" };
        var hasCounter = metric == "memory.usage";
        var source = new ResourceBreakdownSnapshot(at, hasCounter
            ? [new ResourceBreakdownBar(metric, "B", "capacity", 100, 200, 50, [])] : [])
        {
            Datasets = hasCounter ? [new(SamplingDatasetIds.ForProcessMetric(metric), "ready", 1, 1,
                at, at, at, at.AddSeconds(5), null, null)] : []
        };
        var absent = ResourceManager.App.Endpoints.Transport.ResourceProcessDetailsWireSnapshot.Create(source, [metric], new HashSet<string>(), residualDetailMetricIds: residualMetrics);
        Assert.Null(absent.PhysicalMemoryResidual);
        Assert.Equal(hasCounter ? at : (DateTimeOffset?)null, absent.CapturedAt);

        foreach (var frame in new[] { Frame(), PhysicalMemoryResidualFrame.Unavailable(At, "capture-output-missing") })
        {
            var wire = ResourceManager.App.Endpoints.Transport.ResourceProcessDetailsWireSnapshot.Create(
                source with { PhysicalMemoryResidual = frame }, [metric], new HashSet<string>(), residualDetailMetricIds: residualMetrics);
            Assert.Equal(selected ? frame : null, wire.PhysicalMemoryResidual);
            Assert.Equal(hasCounter ? at : selected ? At : (DateTimeOffset?)null, wire.CapturedAt);
            if (selected) Assert.NotEqual(source.CapturedAt, wire.PhysicalMemoryResidual!.CapturedAt);
        }
    }

    internal static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-10-26T00:00:00Z");
    internal static PhysicalMemoryResidualFrame Frame(bool negativePrivate = false)
    {
        var aggregation = new PhysicalMemoryPfnAggregation();
        PfnIdentityLayout[] fixture = [new(0 | 6UL << 4), new(0 | 6UL << 4),
            new(1 | 6UL << 4),new(2 | 6UL << 4),new(3 | 6UL << 4),new(4 | 6UL << 4),
            new(5 | 6UL << 4),new(6 | 6UL << 4),new(8 | 6UL << 4),new(10 | 6UL << 4),
            new(11 | 6UL << 4),new(12 | 6UL << 4),new(1 | 3UL << 4),new(5 | 4UL << 4),
            new(1 | 2UL << 4),new(5 | 1UL << 4),new(2),new(6 | 5UL << 4)];
        aggregation.Add(fixture);
        return aggregation.CreateFrame(At, 20 * 4096UL, (negativePrivate ? 3UL : 1UL) * 4096,
            new(null, [new(0x4346744E, 1024)], 1024, "ready"), static _ => { });
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FixturePartitionsUsedResidualAndWithholdsNegativePrivate(bool negative)
    {
        var frame = Frame(negative);
        Assert.Equal("ready", frame.Status);
        Assert.Equal(frame.UsedBytes - frame.AttributedProcessPrivateBytes, frame.ResidualBytes);
        Assert.Equal(frame.ResidualBytes, frame.Parts.Aggregate(frame.UnclassifiedBytes!.Value, (sum, part) => sum + part.Bytes));
        Assert.Equal(2 * 4096UL, Assert.Single(frame.Parts, part => part.Kind == "system-memory/modified").Bytes);
        Assert.Equal(4096UL, frame.Parts.Where(part => part.Kind.StartsWith(PhysicalMemoryResidualKinds.NonPagedPool + "/")).Aggregate(0UL, (sum, part) => sum + part.Bytes));
        Assert.Equal(negative, frame.ReasonCode == "process-private-below-attributed");
        Assert.Equal(!negative, frame.Parts.Any(part => part.Kind == "system-memory/process-private-not-attributed"));
        foreach (var kind in new[] {"mapped-file","shareable","page-table","paged-pool-resident", "system-pte",
            "metafile","driver-locked","kernel-stack","working-set-internals"})
            Assert.Equal(4096UL, Assert.Single(frame.Parts, part => part.Kind == "system-memory/"+kind).Bytes); // standby/free/zero/bad excluded
    }
    [Fact]
    public void OversizePartsAreUnavailableAndNeverScaled()
    {
        var aggregation = new PhysicalMemoryPfnAggregation();
        aggregation.Add([new(1 | 6UL << 4)]);
        var diagnostics = new List<string>();
        var frame = aggregation.CreateFrame(At, 4096, 2048, new(null,null,0,"ready"), diagnostics.Add);
        Assert.Equal("components-exceed-residual", frame.ReasonCode);
        Assert.Equal("unavailable", frame.Status);
        Assert.Null(frame.UsedBytes);
        Assert.Empty(frame.Parts);
        Assert.Equal(["process-private-below-attributed", "components-exceed-residual"], diagnostics);
    }
    [Fact]
    public unsafe void NativeLayoutAndRangeBoundsMatchPhnt()
    {
        Assert.Equal(32, sizeof(SuperfetchInformationLayout));
        Assert.Equal(192, sizeof(PfnRequestHeader));
        Assert.Equal(24, sizeof(PfnIdentityLayout));
        Assert.Equal(40, sizeof(PoolTagLayout));
        Assert.Equal(16, sizeof(PhysicalRangeHeader));
        var envelope = new SuperfetchInformationLayout {Version=45,Magic=0x6b756843,Data=(void*)1,Length=1};
        Assert.True(envelope.IsValid); envelope.Magic=0; Assert.False(envelope.IsValid);
        envelope.Magic=0x6b756843;envelope.Version=1;Assert.False(envelope.IsValid);
        Assert.Equal(5UL, WindowsPhysicalMemoryPfnReader.ValidateRanges([new(){BasePfn=0,PageCount=2},new(){BasePfn=4,PageCount=3}]));
        Assert.Throws<PfnQueryException>(() => WindowsPhysicalMemoryPfnReader.ValidateRanges([new(){PageCount=3},new(){BasePfn=2,PageCount=1}]));
        Assert.Throws<PfnQueryException>(() => WindowsPhysicalMemoryPfnReader.ValidateRanges([new(){PageCount=WindowsPhysicalMemoryPfnReader.MaximumPages+1}]));
        Assert.Throws<PfnQueryException>(() => WindowsPhysicalMemoryPfnReader.ValidateRanges(new PhysicalRangeLayout[4096]));
    }
    [Fact]
    public void FailedFramesReplaceGoodValuesAndDiagnosticsLogOncePerChange()
    {
        var events = new List<string>();
        var next = Frame();
        var reader = new WindowsPhysicalMemoryPfnReader(events.Add, (_,_) => next);
        Assert.Equal("ready",reader.Capture(0,default).Status);
        next = PhysicalMemoryResidualFrame.Unavailable(At,"superfetch-6:denied");
        Assert.Null(reader.Capture(0,default).ResidualBytes);
        reader.Capture(0,default);
        next = Frame(true);
        reader.Capture(0,default);
        reader.Capture(0,default);
        Assert.Equal(["ready","superfetch-6:denied","process-private-below-attributed"],events);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        Assert.Throws<OperationCanceledException>(() => new WindowsPhysicalMemoryPfnReader().Capture(0,canceled.Token));
    }

    [Fact]
    public void PublicationFailureReplacesAFrameAndFirstFailureHasAnEmptyTypedValue()
    {
        var request = ResourceBreakdownSampleRequest.ForResourceTable([PhysicalMemoryResidualFrame.MetricId], new Dictionary<string,string>());
        var state = new ResourceBreakdownSnapshotState();
        state.ApplyDirect(request,new ResourceBreakdownSnapshot(At,[]) {PhysicalMemoryResidual=Frame()},At,
            TimeSpan.FromSeconds(10),TimeSpan.FromSeconds(1));
        Assert.Equal("ready",state.ReadOrCreate(At,request).PhysicalMemoryResidual!.Status);
        state.MarkFailed(request,At.AddSeconds(1),new InvalidOperationException("fixture"));
        var failed=state.ReadOrCreate(At.AddSeconds(1),request).PhysicalMemoryResidual!;
        Assert.Equal("unavailable",failed.Status);
        Assert.Null(failed.UsedBytes);
        Assert.Empty(failed.Parts);
        var first=new ResourceBreakdownSnapshotState();
        first.MarkFailed(request,At,new InvalidOperationException("fixture"));
        Assert.Equal("unavailable",first.ReadOrCreate(At,request).PhysicalMemoryResidual!.Status);
    }

    [ElevatedMemoryFact]
    public void ElevatedRealMachinePfnCaptureRestoresThreadToken()
    {
        // The service is SYSTEM; an elevated test owns SeProfileSingleProcessPrivilege.
        // Zero attributed private keeps the fixture independent of process-access policy while exercising all PFNs.
        using var beforeIdentity = System.Security.Principal.WindowsIdentity.GetCurrent();
        var before = beforeIdentity.ImpersonationLevel;
        var frame = new WindowsPhysicalMemoryPfnReader().Capture(0, default);
        using var afterIdentity = System.Security.Principal.WindowsIdentity.GetCurrent();
        Assert.Equal(before, afterIdentity.ImpersonationLevel);
        // The OS is not frozen during the walk: a churned window must be explicitly unavailable.
        if (frame.Status == "unavailable")
        {
            Assert.Equal("components-exceed-residual",frame.ReasonCode);
            Assert.Null(frame.ResidualBytes);
            Assert.Empty(frame.Parts);
        }
        else
        {
            Assert.NotEmpty(frame.Parts);
            Assert.Equal(frame.ResidualBytes, frame.Parts.Aggregate(frame.UnclassifiedBytes!.Value,(sum,part)=>sum+part.Bytes));
        }
    }
}
