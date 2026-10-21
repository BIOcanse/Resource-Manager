using System.Runtime.InteropServices;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Native.Pdh;
using ResourceManager.ReadWrite.ProcessFields;

namespace Resource_Manager_APP.Tests;
public sealed class ProcessGpuResidencyLibraryTests
{
    private static ProcessMetricFieldSnapshot Inventory(long birth) => new(null, null, [], [new(42, birth.ToString(), [])]);
    private static NativePdhSnapshot Snapshot() => new(DateTimeOffset.UnixEpoch, 7, 1,
        [new() { AdapterLuid = 23, ProcessId = 42, EngineIndex = 1, UsagePercent = 60 },
         new() { AdapterLuid = 23, ProcessId = 42, EngineIndex = 2, UsagePercent = 80 }], [], default)
        { EngineWindows = [Window(1), Window(2)] };
    private static NativePdhGpuEngineWindow Window(uint engine) => new()
        { AdapterLuid = 23, ProcessId = 42, EngineIndex = engine, BeginFileTime = 150, EndFileTime = 190,
          BeginCounter = 10, EndCounter = 20, CounterFrequency = 100 };
    private static NativePdhReadResult Read(NativePdhSnapshot snapshot) => new(snapshot,
        NativePdhProviderAvailability.Available, NativePdhResultCode.Ok, 0);
    [Fact] public void NativeSidecarPreservesLegacySizes()
    { Assert.Equal(64, Marshal.SizeOf<NativePdhGpuEngineWindow>()); Assert.Equal(32, Marshal.SizeOf<NativePdhGpuEngineRow>()); }
    [Fact] public void NewBirthCannotClaimCachedOldPidWindow()
    {
        var rows = WindowsPdhProcessGpuFields.Project(Read(Snapshot()), Inventory(175), _ => 175);
        Assert.Equal(2, rows.Processes.Count);
        Assert.All(rows.Processes, row => { Assert.Null(row.Values.Single().NumericValue); Assert.Null(row.GpuInterval); });
    }
    [Fact] public void PostReadIdentityChangeRejectsAllOldRows()
        => Assert.Empty(WindowsPdhProcessGpuFields.Project(Read(Snapshot()), Inventory(100), _ => 200).Processes);
    [Fact] public void FirstSampleOrUnknownNativeExtensionNeverInventsInterval()
    {
        var result = WindowsPdhProcessGpuFields.Project(Read(Snapshot() with { EngineWindows = [] }), Inventory(100), _ => 100);
        Assert.All(result.Processes, row => Assert.Null(row.Values.Single().NumericValue));
    }
    [Fact] public void SeparateEnginesRemainBoundedAndRetainRawProvenance()
    {
        var result = WindowsPdhProcessGpuFields.Project(Read(Snapshot()), Inventory(150), _ => 150);
        Assert.Equal(new double?[] { 60, 80 }, result.Processes.Select(row => row.Values.Single().NumericValue));
        Assert.Equal(2, result.Processes.Select(row => row.GpuEngine).Distinct().Count());
        Assert.All(result.Processes, row => Assert.Equal(150, row.GpuInterval!.BeginWindowsFileTime100Ns));
    }
    [Fact] public void LastGoodKeepsOldWindowAndCannotRebindNewBirth()
    {
        var last = Snapshot();
        // Rebuild the existing immutable observation, preserving original payload and sidecar.
        last = new(last.CapturedAt, last.Sequence, last.TopologyGeneration, last.EngineRows, last.MemoryRows,
            last.SystemIo, NativePdhFrameFlags.None, NativePdhDatasetObservation.LastGood(last.CapturedAt, 7),
            NativePdhDatasetObservation.Unavailable, NativePdhDatasetObservation.Unavailable, NativePdhDatasetObservation.Unavailable)
            { EngineWindows = last.EngineWindows };
        var old = WindowsPdhProcessGpuFields.Project(Read(last), Inventory(100), _ => 100);
        Assert.All(old.Processes, row => Assert.True(row.GpuInterval!.IsLastGood));
        var reused = WindowsPdhProcessGpuFields.Project(Read(last), Inventory(200), _ => 200);
        Assert.All(reused.Processes, row => Assert.Null(row.Values.Single().NumericValue));
    }
    [Fact] public void DuplicateWindowOrRegressedRawClockIsUnavailable()
    {
        var duplicate = Snapshot() with { EngineWindows = [Window(1), Window(1), Window(2) with { EndCounter = 9 }] };
        Assert.All(WindowsPdhProcessGpuFields.Project(Read(duplicate), Inventory(100), _ => 100).Processes,
            row => Assert.Null(row.Values.Single().NumericValue));
    }
    [Fact] public void OriginalCurrentRetainsGpuScopeSeparatelyFromBasicAndDiskAndBorrowUsesSameOwner()
    {
        var fields = WindowsPdhProcessGpuFields.Project(Read(Snapshot()), Inventory(100), _ => 100);
        var current = new ProcessFieldCurrentState();
        current.Apply([WindowsPdhProcessGpuFields.FieldName], fields);
        using var owner = new ResourceManager.ReadWrite.Windows.Processes.WindowsProcessFieldOwner(current);
        var catalog = owner.Backend.Catalog;
        Assert.Equal(2, catalog.Fields.Length);
        Assert.All(catalog.Fields, field => Assert.Equal(WindowsPdhProcessGpuFields.FieldName, field.Field.FieldName));
        var first = catalog.Fields[0].Field;
        var cached = owner.Backend.ReadCached(first);
        Assert.Equal(FieldObservationStatus.Current, cached.Status);
        Assert.Equal((ulong)100, cached.SampleDurationMilliseconds);
        using var borrow = owner.BorrowSubscriptionCapture();
        Assert.Same(owner.Backend, borrow.Backend); Assert.Same(owner.Publications, borrow.Publications);
        Assert.Equal(ProcessMetricDatasetIds.GpuResidency, borrow.Bind([first]).Single().CollectionItemIds.Single());
        current.Fail([WindowsPdhProcessGpuFields.FieldName]);
        Assert.Equal(FieldObservationStatus.Unavailable, owner.Backend.ReadCached(first).Status);
        Assert.Null(owner.ReadCachedSnapshot()!.Processes[0].GpuInterval);
    }
}
