using System.Buffers.Binary;

namespace Resource_Manager_APP.Tests;

public sealed class NvidiaGpuVfTransactionTests
{
    private static readonly NvidiaControlObject Target = new("gpu:simulation", 0);
    private static readonly NvidiaGpuVfCurveTransactionOptions Exact = new(0);

    [Fact]
    public void UniformAndNonuniformCurvesUseOneTransactionAndRestoreTheFirstWholeBaseline()
    {
        var (snapshot, original) = Baseline();
        var adapter = new NvidiaGpuVfCurveSimulatedAdapter(snapshot, original);
        using var resource = Resource(adapter);
        Assert.True(resource.IsVfCurveSimulation);

        var first = resource.WriteVfCurve(Target, snapshot, Offsets(30000, 30000), Exact);
        Assert.True(first.Applied);
        Assert.Equal(30000, first.Readback!.ScalarOffsetKhz);
        Assert.Equal(new[] { 0, 0 }, TableOffsets(adapter.CopyControl()));
        Assert.Equal(new[] { "baseline-read", "clear-table", "write-scalar", "readback" }, adapter.Operations);
        AssertUnknownFieldsEqual(original, adapter.CopyControl());

        var second = resource.WriteVfCurve(Target, snapshot, Offsets(45000, -15000), Exact);
        Assert.True(second.Applied);
        Assert.Equal(0, second.Readback!.ScalarOffsetKhz);
        Assert.Equal(new[] { 45000, -15000 }, TableOffsets(adapter.CopyControl()));
        AssertUnknownFieldsEqual(original, adapter.CopyControl());
        Assert.Equal(new[] { "baseline-read", "clear-scalar", "write-table", "readback" }, adapter.Operations.Skip(4));

        var repeated = resource.WriteVfCurve(Target, snapshot, Offsets(45000, -15000), Exact);
        Assert.True(repeated.Applied);
        Assert.Equal(new[] { 45000, -15000 }, repeated.Readback!.Points.Select(p => p.FrequencyOffsetKhz));

        var released = resource.RestoreVfCurve(Target, Exact);
        Assert.True(released.Applied);
        Assert.Equal(snapshot.ScalarOffsetKhz, released.Readback!.ScalarOffsetKhz);
        Assert.Equal(original, adapter.CopyControl());
        Assert.Equal(snapshot.Points.Select(p => p.FrequencyOffsetKhz), released.Readback.Points.Select(p => p.FrequencyOffsetKhz));
        Assert.False(resource.RestoreVfCurve(Target, Exact).Applied);
    }

    [Theory]
    [InlineData("clear-table", true)]
    [InlineData("write-scalar", true)]
    [InlineData("clear-scalar", false)]
    [InlineData("write-table", false)]
    [InlineData("readback", false)]
    public void EveryPartialMutationAndReadbackFailureRestoresEntireScalarAndUnknownTable(string operation, bool uniform)
    {
        var (snapshot, original) = Baseline();
        var adapter = new NvidiaGpuVfCurveSimulatedAdapter(snapshot, original)
        {
            FailOperation = op => op == operation ? "injected-" + operation : null
        };
        using var resource = Resource(adapter);
        var result = resource.WriteVfCurve(Target, snapshot, uniform ? Offsets(30000, 30000) : Offsets(30000, -15000), Exact);
        Assert.False(result.Applied);
        Assert.Equal("injected-" + operation, result.FailureReason);
        Assert.True(result.RestorationAttempted);
        Assert.True(result.RestorationSucceeded);
        Assert.Equal(original, adapter.CopyControl());
        var current = adapter.Read(Target).Snapshot!;
        Assert.Equal(snapshot.ScalarOffsetKhz, current.ScalarOffsetKhz);
        Assert.Equal(snapshot.Points.Select(p => p.FrequencyOffsetKhz), current.Points.Select(p => p.FrequencyOffsetKhz));
        Assert.Contains("restore-table", adapter.Operations);
        Assert.Contains("restore-readback", adapter.Operations);
    }

    [Fact]
    public void FalseSuccessfulSetterRollsBackWhenReadbackDiffersBeyondConfiguredTolerance()
    {
        var (snapshot, original) = Baseline();
        var adapter = new NvidiaGpuVfCurveSimulatedAdapter(snapshot, original)
        {
            ReadbackScalarAdjustmentKhz = op => op == "readback" ? 1000 : 0
        };
        using var resource = Resource(adapter);
        var result = resource.WriteVfCurve(Target, snapshot, Offsets(30000, -15000), new(999));
        Assert.False(result.Applied);
        Assert.Equal("nvidia-vf-readback-mismatch", result.FailureReason);
        Assert.True(result.RestorationSucceeded);
        Assert.Equal(original, adapter.CopyControl());
    }

    [Fact]
    public void ReadbackToleranceIsCallerOwnedAndInclusive()
    {
        var (snapshot, original) = Baseline();
        var adapter = new NvidiaGpuVfCurveSimulatedAdapter(snapshot, original)
        {
            ReadbackScalarAdjustmentKhz = op => op == "readback" ? 1000 : 0
        };
        using var resource = Resource(adapter);
        Assert.True(resource.WriteVfCurve(Target, snapshot, Offsets(30000, -15000), new(1000)).Applied);
    }

    [Fact]
    public void RestorationFailureRetainsOriginalErrorAndBaselineUntilExplicitRetrySucceeds()
    {
        var (snapshot, original) = Baseline();
        var adapter = new NvidiaGpuVfCurveSimulatedAdapter(snapshot, original)
        {
            FailOperation = op => op is "readback" or "restore-table" ? "injected-" + op : null
        };
        using var resource = Resource(adapter);
        var result = resource.WriteVfCurve(Target, snapshot, Offsets(30000, -15000), Exact);
        Assert.False(result.Applied);
        Assert.True(result.RestorationAttempted);
        Assert.False(result.RestorationSucceeded);
        Assert.Contains("injected-readback", result.FailureReason);
        Assert.Contains("injected-restore-table", result.FailureReason);
        Assert.Contains("restore-scalar", adapter.Operations);
        Assert.Contains("restore-readback", adapter.Operations);
        adapter.FailOperation = null;
        Assert.True(resource.RestoreVfCurve(Target, Exact).Applied);
        Assert.Equal(original, adapter.CopyControl());
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("voltage")]
    [InlineData("range")]
    [InlineData("binding")]
    public void CompletePreflightRejectsWithoutAnyMutation(string invalid)
    {
        var (snapshot, original) = Baseline();
        var adapter = new NvidiaGpuVfCurveSimulatedAdapter(snapshot, original);
        using var resource = Resource(adapter);
        var points = Offsets(30000, -15000);
        if (invalid == "missing") points = [points[0]];
        if (invalid == "duplicate") points = [points[0], points[0]];
        if (invalid == "voltage") points[1] = points[1] with { VoltageMicrovolts = 999999 };
        if (invalid == "range") points[1] = points[1] with { FrequencyOffsetKhz = int.MaxValue };
        var expected = invalid == "binding" ? snapshot with { DeviceIdentity = "gpu:other" } : snapshot;
        var result = resource.WriteVfCurve(Target, expected, points, Exact);
        Assert.False(result.Applied);
        Assert.False(result.RestorationAttempted);
        Assert.Equal(new[] { "baseline-read" }, adapter.Operations);
        Assert.Equal(original, adapter.CopyControl());
    }

    [Fact]
    public void ScalarOnlyHardwareUsesOneFullDomainPointAndNeverTouchesTheTable()
    {
        var snapshot = new NvidiaGpuVfCurveSnapshot(Target.Id, NvidiaGpuVfCurveAbis.Scalar,
            [new(0, 0, 0, 0, 15000, -100000, 100000)], true, null, 15000);
        var adapter = new NvidiaGpuVfCurveSimulatedAdapter(snapshot);
        using var resource = Resource(adapter);
        var result = resource.WriteVfCurve(Target, snapshot, [new(0, 0, -30000)], Exact);
        Assert.True(result.Applied);
        Assert.Equal(-30000, result.Readback!.ScalarOffsetKhz);
        Assert.Equal(new[] { "baseline-read", "write-scalar", "readback" }, adapter.Operations);
        Assert.True(resource.RestoreVfCurve(Target, Exact).Applied);
    }

    [Fact]
    public void ProductionAndArbitraryReadAdaptersCannotExecuteSetOrAcquireHandles()
    {
        var (snapshot, _) = Baseline();
        using var production = new NvidiaGpuControlResource(nvapi: new ForbiddenNativeBridge());
        Assert.False(production.IsVfCurveSimulation);
        Assert.Equal(NvidiaGpuVfCurveAbis.LiveWriteUnavailable,
            production.WriteVfCurve(Target, snapshot, Offsets(30000, 30000), Exact).FailureReason);
        Assert.False(production.RestoreVfCurve(Target, Exact).Applied);
        using var readOnly = Resource(new ReadAdapter(snapshot with { CanWrite = true }));
        Assert.False(readOnly.IsVfCurveSimulation);
        Assert.Equal(NvidiaGpuVfCurveAbis.LiveWriteUnavailable,
            readOnly.WriteVfCurve(Target, snapshot, Offsets(30000, 30000), Exact).FailureReason);
    }

    private static NvidiaGpuControlResource Resource(INvidiaGpuVfCurveAdapter adapter)
        => new(nvapi: new ForbiddenNativeBridge(), vfCurveAdapter: adapter);

    private sealed class ReadAdapter(NvidiaGpuVfCurveSnapshot snapshot) : INvidiaGpuVfCurveAdapter
    {
        public NvidiaGpuVfCurveReadResult Read(NvidiaControlObject target) => new(snapshot);
    }

    private sealed class ForbiddenNativeBridge : NvidiaNvapiControlBridge
    {
        public override IntPtr? FindHandleByAdapterIndex(int adapterIndex) => throw new InvalidOperationException("No hardware calls allowed.");
        public override IntPtr? FindHandleByIdentity(WindowsGpuAdapterInventoryRead inventory, ulong adapterKey)
            => throw new InvalidOperationException("No hardware calls allowed.");
        public override bool TryWriteClockOffsetMhz(IntPtr gpuHandle, uint clockDomain, double offsetMhz)
            => throw new InvalidOperationException("No hardware calls allowed.");
    }

    private static (NvidiaGpuVfCurveSnapshot Snapshot, byte[] Table) Baseline()
    {
        var snapshot = new NvidiaGpuVfCurveSnapshot(Target.Id, NvidiaGpuVfCurveAbis.Modern,
            [new(1, 700123, 1500000, 1514000, 14000, -500000, 500000),
             new(65, 800456, 1600000, 1624000, 24000, -500000, 500000)], true, null, 4000);
        var table = new byte[9248];
        BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(0, 4), 0x00012420);
        BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(4, 4), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(12, 4), 2);
        // Mask bit 255, reserved bytes, unknown union bytes and another point's delta are not ours.
        BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(32, 4), 0x80000000);
        table[40] = 0xA7;
        table[68 + 1 * 36 + 31] = 0xD3;
        table[table.Length - 1] = 0xEF;
        BinaryPrimitives.WriteInt32LittleEndian(table.AsSpan(68 + 1 * 36 + 20, 4), 10000);
        BinaryPrimitives.WriteInt32LittleEndian(table.AsSpan(68 + 65 * 36 + 20, 4), 20000);
        BinaryPrimitives.WriteInt32LittleEndian(table.AsSpan(68 + 7 * 36 + 20, 4), 12345);
        return (snapshot, table);
    }
    private static NvidiaGpuVfCurveOffset[] Offsets(int first, int second) => [new(1, 700123, first), new(65, 800456, second)];
    private static int[] TableOffsets(byte[] table) => [
        BinaryPrimitives.ReadInt32LittleEndian(table.AsSpan(68 + 1 * 36 + 20, 4)),
        BinaryPrimitives.ReadInt32LittleEndian(table.AsSpan(68 + 65 * 36 + 20, 4))];
    private static void AssertUnknownFieldsEqual(byte[] expected, byte[] actual)
    {
        var preserved = (byte[])expected.Clone();
        foreach (var point in new[] { 1, 65 })
            actual.AsSpan(68 + point * 36 + 20, 4).CopyTo(preserved.AsSpan(68 + point * 36 + 20, 4));
        Assert.Equal(preserved, actual);
    }
}
