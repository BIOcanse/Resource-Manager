using ResourceManager.App.Domain.Metrics;
using ResourceManager.ReadWrite;

namespace Resource_Manager_APP.Tests;

public sealed class AmdSmuLibraryIntegrationTests
{
    private static readonly AmdSmuCpuSensorReadRequest Selected = new(false, true, false, true,
        false, true, false, false, false, false, false, false, false, false, true);
    private static readonly AmdSmuCpuSensorReadRequest None = new(false, false, false, false,
        false, false, false, false, false, false, false, false, false, false, false);

    [Fact]
    public void PublicReaderOwnsTheOriginalMappingAndExplicitSessionLifetime()
    {
        Assert.All(new[] { typeof(AmdSmuCpuSensorReader), typeof(AmdSmuPawnIoSession), typeof(AmdSmuPawnIoOpenOptions),
            typeof(IAmdSmuSession), typeof(AmdSmuSensorIndexResolver), typeof(CpuSensorMetrics) },
            type => Assert.Same(typeof(ReadWriteCore).Assembly, type.Assembly));
        var session = new RecordedSession(); var opens = 0;
        var reader = new AmdSmuCpuSensorReader(() => { opens++; return session; }, 16, TimeSpan.FromSeconds(10), TimeProvider.System);
        Assert.Equal(HardwareSensorProviderStates.NotRequested, reader.Read(None).ProviderState.State);
        Assert.Equal(0, opens);
        var value = reader.Read(Selected);
        Assert.Equal(HardwareSensorProviderStates.Active, value.ProviderState.State);
        Assert.Equal(42, value.ActualPowerWatts);
        Assert.Null(value.PackagePowerWatts);
        Assert.Null(value.PackageCurrentAmps);
        Assert.Equal(75, value.TemperatureCelsius);
        Assert.Equal(4250, value.SmuFrequencyMhz);
        Assert.Equal(1.1, value.CoreVoltageVolts!.Value, 5);
        var description = reader.DescribeSession();
        Assert.True(description.LayoutMapped);
        Assert.Equal("0x00540208", description.TableVersion);
        Assert.Equal(256, description.Sample!.Count);
        Assert.Equal(1, opens);
        Assert.Equal(2, session.Reads);
        reader.Dispose(); reader.Dispose();
        Assert.Equal(1, session.Disposals);
        Assert.Throws<ObjectDisposedException>(() => reader.Read(None));
        Assert.Throws<ObjectDisposedException>(() => reader.DescribeSession());
        Assert.Equal(1, opens);
    }

    [Fact]
    public void UnknownTableVersionDoesNotBorrowAnotherGenerationLayout()
    {
        var session = new RecordedSession { TableVersion = 123 };
        using var reader = new AmdSmuCpuSensorReader(() => session, 16, TimeSpan.Zero, TimeProvider.System);
        var value = reader.Read(Selected);
        Assert.Equal(HardwareSensorProviderStates.Unavailable, value.ProviderState.State);
        Assert.Null(value.ActualPowerWatts); Assert.Null(value.TemperatureCelsius); Assert.Null(value.SmuFrequencyMhz);
        var description = reader.DescribeSession();
        Assert.True(description.SessionOpen); Assert.False(description.LayoutMapped);
        Assert.Null(AmdSmuSensorIndexResolver.Resolve(123));
    }

    [Fact]
    public void CallerClockAndFailureIntervalControlReopeningAndFailedSessionIsReleased()
    {
        var clock = new Clock(); var first = new RecordedSession { Fail = true }; var second = new RecordedSession();
        var opens = 0;
        using var reader = new AmdSmuCpuSensorReader(() => ++opens == 1 ? first : second, 16, TimeSpan.FromSeconds(10), clock);
        Assert.Equal(HardwareSensorProviderStates.Unavailable, reader.Read(Selected).ProviderState.State);
        Assert.Equal(1, first.Disposals);
        Assert.Equal(HardwareSensorProviderStates.Unavailable, reader.Read(Selected).ProviderState.State);
        Assert.Equal(1, opens);
        clock.Now += TimeSpan.FromSeconds(10);
        Assert.Equal(42, reader.Read(Selected).ActualPowerWatts);
        Assert.Equal(2, opens);
        Assert.Equal(1, second.Reads);
    }

    [Fact]
    public void ExplicitlyUnselectedExecutionPathsDoNotOpenAnyNativeExecutor()
    {
        var path = Path.Combine(Path.GetTempPath(), "amd-smu-selected-" + Guid.NewGuid().ToString("N") + ".bin");
        File.WriteAllBytes(path, [0]);
        try { Assert.Throws<AmdSmuProviderUnavailableException>(() => AmdSmuPawnIoSession.Open(new(path, [], false, TimeSpan.Zero))); }
        finally { File.Delete(path); }
    }

    private sealed class Clock : TimeProvider
    {
        internal DateTimeOffset Now = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class RecordedSession : IAmdSmuSession
    {
        public string ExecutorName => "Recorded";
        public string ModulePath => "recorded-module";
        public uint TableVersion { get; set; } = 0x00540208;
        internal int Reads, Disposals;
        internal bool Fail;
        public float[] UpdateAndReadPmTable()
        {
            Reads++;
            if (Fail) throw new IOException("Recorded failure");
            var table = new float[1024]; table[3] = 42; table[11] = 75;
            table[314] = 1.1f; table[346] = 4.25f;
            return table;
        }
        public void Dispose() => Disposals++;
    }
}
