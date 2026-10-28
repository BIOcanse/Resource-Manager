using System.Text.Json;
using ResourceManager.ReadWrite.Vendors.Amd;

namespace Resource_Manager_APP.Tests;

public sealed class AmdDesktopGpuControlOwnerLibraryIntegrationTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "rm-amd-control-owner-" + Guid.NewGuid());
    private string Baseline => Path.Combine(folder, "baseline.json");
    private static AmdDesktopGpuControlTarget Gpu => new(1, AmdDesktopGpuControlRoute.WindowsAmdFixedDiscrete);
    private static AmdDesktopGpuControlTarget Fan => Gpu with { Kind = AmdDesktopGpuControlObjectKind.Fan };
    private AmdDesktopGpuControlResource Owner(RecordedHardware io, string? path = null) => new(io, new(path ?? Baseline));
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationDuringNativeReadPreventsBaselineAndSetter(bool restore)
    {
        var io = new RecordedHardware();
        using var owner = Owner(io);
        if (restore)
            await owner.WriteAsync(Gpu, AmdDesktopGpuControlField.PowerLimitOffset, new(Number: 6));
        var original = File.Exists(Baseline) ? File.ReadAllText(Baseline) : null;
        var writes = io.Writes;
        using var cancellation = new CancellationTokenSource();
        io.BeforeRead = cancellation.Cancel;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            if (restore) await owner.RestoreAsync(Gpu, AmdDesktopGpuControlField.PowerLimitOffset,
                new HashSet<AmdDesktopGpuControlField>(), cancellation.Token);
            else await owner.WriteAsync(Gpu, AmdDesktopGpuControlField.PowerLimitOffset, new(Number: 6), cancellation.Token);
        });

        Assert.Equal(writes, io.Writes);
        Assert.Equal(original, File.Exists(Baseline) ? File.ReadAllText(Baseline) : null);
    }
    [Fact]
    public async Task ConstructionAdmissionAndBorrowedLifetimeNeverSetOrDisposeHardware()
    {
        var io = new RecordedHardware();
        var owner = Owner(io);
        Assert.Equal(0, io.Reads);
        Assert.False(File.Exists(Baseline));
        var rejected = Gpu with { Route = AmdDesktopGpuControlRoute.Rejected };
        Assert.False(owner.Probe(rejected, AmdDesktopGpuControlField.PowerLimitOffset).IsAdmitted);
        Assert.False(owner.Probe(Gpu, AmdDesktopGpuControlField.FanZeroRpm).CanWrite);
        Assert.False(owner.Probe(Fan, AmdDesktopGpuControlField.CoreClockOffset).CanWrite);
        Assert.Equal(AmdDesktopGpuControlStatus.Unsupported,
            (await owner.WriteAsync(rejected, AmdDesktopGpuControlField.PowerLimitOffset, new(Number: 6))).Status);
        Assert.Equal(0, io.Reads);
        Assert.Equal(0, io.Writes);
        owner.Dispose(); owner.Dispose();
        Assert.Equal(0, io.Disposals);
        Assert.Throws<ObjectDisposedException>(() => owner.Probe(Gpu, AmdDesktopGpuControlField.PowerLimitOffset));
        using var real = new AmdDesktopGpuControlResource(new AdlxTuningHardware(), new(Baseline));
        // Real hardware construction is passive: no Probe/Read/Write or driver installation here.
    }
    [Fact]
    public async Task PersistFailurePreventsSetterAndFirstBaselineSurvivesRestart()
    {
        Directory.CreateDirectory(folder);
        var occupied = Path.Combine(folder, "occupied"); File.WriteAllText(occupied, "file");
        var io = new RecordedHardware();
        using (var failed = Owner(io, Path.Combine(occupied, "baseline.json")))
        {
            Assert.Equal(AmdDesktopGpuControlStatus.Failed,
                (await failed.WriteAsync(Gpu, AmdDesktopGpuControlField.PowerLimitOffset, new(Number: 6))).Status);
            Assert.Equal(0, io.Writes);
        }
        io.BeforeWrite = () => Assert.Equal(0, LoadBaseline()["PCI\\AMD\n0"]);
        using (var owner = Owner(io))
        {
            Assert.True(owner.Probe(Gpu, AmdDesktopGpuControlField.PowerLimitOffset).CanWrite);
            Assert.False(File.Exists(Baseline));
            Assert.Equal(AmdDesktopGpuControlStatus.Applied,
                (await owner.WriteAsync(Gpu, AmdDesktopGpuControlField.PowerLimitOffset, new(Number: 6))).Status);
            Assert.Equal(AmdDesktopGpuControlStatus.Applied,
                (await owner.WriteAsync(Gpu, AmdDesktopGpuControlField.PowerLimitOffset, new(Number: 10))).Status);
        }
        using var restarted = Owner(io);
        Assert.True(await restarted.RestoreAsync(Gpu, AmdDesktopGpuControlField.PowerLimitOffset, new HashSet<AmdDesktopGpuControlField>()));
        Assert.Equal(0, io.Values[0]);
        Assert.Empty(LoadBaseline());
    }
    [Fact]
    public async Task ExactPnpRejectsBeforeSetterAndChangedPnpCannotReleaseBaseline()
    {
        var io = new RecordedHardware();
        using var owner = Owner(io);
        var exact = Gpu with { ExpectedPnp = "PCI\\OTHER" };
        Assert.False(owner.Probe(exact, AmdDesktopGpuControlField.PowerLimitOffset).CanWrite);
        Assert.Equal(AmdDesktopGpuControlStatus.Failed,
            (await owner.WriteAsync(exact, AmdDesktopGpuControlField.PowerLimitOffset, new(Number: 6))).Status);
        Assert.Equal(0, io.Writes);
        Assert.False(File.Exists(Baseline));
        Assert.Equal(AmdDesktopGpuControlStatus.Applied,
            (await owner.WriteAsync(Gpu, AmdDesktopGpuControlField.PowerLimitOffset, new(Number: 6))).Status);
        io.Pnp = "PCI\\OTHER";
        var writes = io.Writes;
        Assert.False(await owner.RestoreAsync(Gpu, AmdDesktopGpuControlField.PowerLimitOffset, new HashSet<AmdDesktopGpuControlField>()));
        Assert.Equal(writes, io.Writes);
        Assert.Single(LoadBaseline());
        io.Pnp = "PCI\\AMD"; io.ChangePnpAfterWrite = true;
        Assert.Equal(AmdDesktopGpuControlStatus.Failed,
            (await owner.WriteAsync(Gpu, AmdDesktopGpuControlField.PowerLimitOffset, new(Number: 10))).Status);
    }
    [Theory]
    [InlineData(3)] [InlineData(16)] [InlineData(2.5)] [InlineData(double.NaN)] [InlineData(double.PositiveInfinity)]
    public async Task DriverIntegerRangeAndStepAreAdmittedBeforeBaselineOrSetter(double requested)
    {
        var io = new RecordedHardware(); using var owner = Owner(io);
        var probe = owner.Probe(Gpu, AmdDesktopGpuControlField.PowerLimitOffset);
        Assert.Equal(new AmdDesktopGpuControlRange(-10, 14, 2, "%"), probe.Range);
        Assert.Equal(AmdDesktopGpuControlMessage.InvalidRangeOrStep,
            (await owner.WriteAsync(Gpu, AmdDesktopGpuControlField.PowerLimitOffset, new(Number: requested))).Message);
        Assert.Equal(0, io.Writes); Assert.False(File.Exists(Baseline));
    }
    [Fact]
    public async Task TypedToggleAndStrictSiblingRestoreOrderRetainOtherBaseline()
    {
        var io = new RecordedHardware(); io.Values[1] = 1600;
        using var owner = Owner(io);
        Assert.Equal("RPM", owner.Probe(Fan, AmdDesktopGpuControlField.FanTargetRpm).Range!.Unit);
        Assert.Equal(AmdDesktopGpuControlStatus.Failed,
            (await owner.WriteAsync(Fan, AmdDesktopGpuControlField.FanZeroRpm, new(Number: 0))).Status);
        Assert.Equal(AmdDesktopGpuControlStatus.Applied,
            (await owner.WriteAsync(Fan, AmdDesktopGpuControlField.FanZeroRpm, new(Toggle: false))).Status);
        var toggle = await owner.ReadAsync(Fan, AmdDesktopGpuControlField.FanZeroRpm);
        Assert.False(toggle!.Toggle); Assert.Null(toggle.Number);
        Assert.True(await owner.RestoreAsync(Fan, AmdDesktopGpuControlField.FanZeroRpm, new HashSet<AmdDesktopGpuControlField>()));
        Assert.Equal(1, io.Values[7]);
        Assert.Equal(AmdDesktopGpuControlStatus.Applied,
            (await owner.WriteAsync(Gpu, AmdDesktopGpuControlField.CoreClockMinimum, new(Number: 500))).Status);
        Assert.Equal(AmdDesktopGpuControlStatus.Applied,
            (await owner.WriteAsync(Gpu, AmdDesktopGpuControlField.CoreClockMaximum, new(Number: 1000))).Status);
        io.Trace.Clear();
        Assert.False(await owner.RestoreAsync(Gpu, AmdDesktopGpuControlField.CoreClockMinimum,
            new HashSet<AmdDesktopGpuControlField> { AmdDesktopGpuControlField.CoreClockMaximum }));
        Assert.DoesNotContain(io.Trace, item => item.StartsWith("write:"));
        io.Trace.Clear();
        Assert.True(await owner.RestoreAsync(Gpu, AmdDesktopGpuControlField.CoreClockMinimum, new HashSet<AmdDesktopGpuControlField>()));
        Assert.Equal(new[] { "read:1", "read:2", "write:2:2400", "read:2", "write:1:1600", "read:1" }, io.Trace);
        Assert.False(LoadBaseline().ContainsKey("PCI\\AMD\n1"));
        Assert.Equal(2400, LoadBaseline()["PCI\\AMD\n2"]);
        Assert.True(await owner.RestoreAsync(Gpu, AmdDesktopGpuControlField.CoreClockMaximum, new HashSet<AmdDesktopGpuControlField>()));
        Assert.Empty(LoadBaseline());
    }
    [Fact]
    public async Task OffsetGenerationRestoresWithoutAbsoluteSiblingAndUnknownIsNotImplemented()
    {
        var io = new RecordedHardware { Mode = AdlxGfxMode.Offset }; using var owner = Owner(io);
        Assert.False(owner.Probe(Gpu, AmdDesktopGpuControlField.CoreClockMaximum).CanWrite);
        Assert.False(owner.Probe(Gpu, AmdDesktopGpuControlField.CoreVoltage).CanWrite);
        Assert.Equal("MHz", owner.Probe(Gpu, AmdDesktopGpuControlField.CoreClockOffset).Range!.Unit);
        Assert.Equal("mV", owner.Probe(Gpu, AmdDesktopGpuControlField.CoreVoltageOffset).Range!.Unit);
        Assert.Equal(AmdDesktopGpuControlStatus.Applied,
            (await owner.WriteAsync(Gpu, AmdDesktopGpuControlField.CoreClockOffset, new(Number: 150))).Status);
        Assert.Equal(AmdDesktopGpuControlStatus.Applied,
            (await owner.WriteAsync(Gpu, AmdDesktopGpuControlField.CoreClockMinimum, new(Number: 600))).Status);
        io.Trace.Clear();
        Assert.True(await owner.RestoreAsync(Gpu, AmdDesktopGpuControlField.CoreClockMinimum,
            new HashSet<AmdDesktopGpuControlField> { AmdDesktopGpuControlField.CoreClockOffset }));
        Assert.Equal(new[] { "read:1", "write:1:500", "read:1" }, io.Trace);
        io.UnknownGeneration = true;
        Assert.Equal(AmdDesktopGpuControlFailureKind.NotImplemented,
            owner.Probe(Gpu, AmdDesktopGpuControlField.CoreClockOffset).FailureKind);
    }
    [Fact]
    public async Task FailedReadbackOrFailedRemovalDoesNotErasePersistedBaseline()
    {
        var io = new RecordedHardware { IgnoreWrites = true }; using var owner = Owner(io);
        Assert.Equal(AmdDesktopGpuControlMessage.ReadbackMismatch,
            (await owner.WriteAsync(Gpu, AmdDesktopGpuControlField.PowerLimitOffset, new(Number: 6))).Message);
        Assert.Single(LoadBaseline()); io.IgnoreWrites = false;
        Assert.Equal(AmdDesktopGpuControlStatus.Applied,
            (await owner.WriteAsync(Gpu, AmdDesktopGpuControlField.PowerLimitOffset, new(Number: 6))).Status);
        Directory.CreateDirectory(Baseline + ".tmp");
        Assert.False(await owner.RestoreAsync(Gpu, AmdDesktopGpuControlField.PowerLimitOffset, new HashSet<AmdDesktopGpuControlField>()));
        Assert.Single(LoadBaseline()); Assert.Equal(0, io.Values[0]);
        Directory.Delete(Baseline + ".tmp");
        Assert.True(await owner.RestoreAsync(Gpu, AmdDesktopGpuControlField.PowerLimitOffset, new HashSet<AmdDesktopGpuControlField>()));
    }
    [Fact]
    public async Task CancellationDoesNotTouchHardwareOrBaseline()
    {
        var io = new RecordedHardware(); using var owner = Owner(io); using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => owner.WriteAsync(Gpu, AmdDesktopGpuControlField.PowerLimitOffset, new(Number: 6), cancellation.Token));
        Assert.Equal(0, io.Reads); Assert.Equal(0, io.Writes); Assert.False(File.Exists(Baseline));
    }
    [Fact]
    public async Task DisposalDrainsOriginalGateWithoutRestoringOrClosingBorrowedDriver()
    {
        var io = new RecordedHardware(); var owner = Owner(io);
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        io.BeforeRead = () => { entered.Set(); if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException(); };
        var write = Task.Run(() => owner.WriteAsync(Gpu, AmdDesktopGpuControlField.PowerLimitOffset, new(Number: 6)));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
        using var disposalAttempted = new ManualResetEventSlim();
        var dispose = Task.Run(() => { disposalAttempted.Set(); owner.Dispose(); });
        Assert.True(disposalAttempted.Wait(TimeSpan.FromSeconds(10)));
        Assert.False(dispose.IsCompleted);
        release.Set();
        Assert.Equal(AmdDesktopGpuControlStatus.Applied, (await write).Status);
        await dispose;
        Assert.Equal(1, io.Writes); Assert.Equal(6, io.Values[0]); Assert.Equal(0, io.Disposals);
        Assert.Single(LoadBaseline());
        Assert.Throws<ObjectDisposedException>(() => { _ = owner.ReadAsync(Gpu, AmdDesktopGpuControlField.PowerLimitOffset); });
    }
    private Dictionary<string, int> LoadBaseline() => JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(Baseline))!;
    private sealed class RecordedHardware : IAdlxTuningHardware, IDisposable
    {
        public int[] Values = [0, 500, 2400, 1000, 1800, 20, 1000, 1, 100, -50];
        public string Pnp = "PCI\\AMD";
        public AdlxGfxMode Mode = AdlxGfxMode.Absolute;
        public bool IgnoreWrites, ChangePnpAfterWrite, UnknownGeneration;
        public int Reads, Writes, Disposals;
        public Action? BeforeWrite;
        public Action? BeforeRead;
        public List<string> Trace = [];
        public AdlxTuningValue Read(int? adapterIndex, int field)
        {
            BeforeRead?.Invoke(); Assert.Equal(1, adapterIndex); Reads++; Trace.Add("read:" + field);
            if (UnknownGeneration) throw new AdlxUnknownGenerationException();
            if ((field is 2 or 3 && Mode != AdlxGfxMode.Absolute) || (field >= 8 && Mode != AdlxGfxMode.Offset)) throw new IOException("wrong generation semantics");
            return new(Pnp, Values[field], field == 7 ? 0 : field is 1 or 2 ? 100 : -10,
                field == 7 ? 1 : field is 1 or 2 ? 3000 : field >= 8 ? 200 : 14,
                field == 7 || field is 1 or 2 || field >= 8 ? 1 : 2, Mode);
        }
        public void Write(string pnp, int field, int value)
        {
            Assert.Equal(Pnp, pnp); BeforeWrite?.Invoke();
            if (Mode == AdlxGfxMode.Absolute && (field == 1 && value > Values[2] || field == 2 && value < Values[1])) throw new IOException("inverted clocks");
            Writes++; Trace.Add($"write:{field}:{value}");
            if (!IgnoreWrites) Values[field] = value;
            if (ChangePnpAfterWrite) Pnp = "PCI\\OTHER";
        }
        public void Dispose() => Disposals++;
    }
    public void Dispose() { if (Directory.Exists(folder))
        {
            // These fixtures create only files and empty immediate child directories.
            foreach (var file in Directory.EnumerateFiles(folder)) File.Delete(file);
            foreach (var child in Directory.EnumerateDirectories(folder)) Directory.Delete(child);
            Directory.Delete(folder);
        } }
}
