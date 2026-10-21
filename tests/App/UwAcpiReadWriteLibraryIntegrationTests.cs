using System.Runtime.InteropServices;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite.Vendors.Mechrevo;

namespace Resource_Manager_APP.Tests;

public sealed class UwAcpiReadWriteLibraryIntegrationTests
{
    [Fact]
    public void ExplicitReadUsesTheOriginalAddressesAndKeepsTheSessionUntilRelease()
    {
        var session = new RecordedSession(address => address switch
        {
            1124 => 9, 1125 => 96, 1132 => 12, 1131 => 128, 1883 => 78, 1884 => 200,
            _ => throw new InvalidOperationException("Unexpected EC address.")
        });
        var opens = 0;
        var reader = new MechrevoUwAcpiReader("caller-selected.dll", path =>
        {
            Assert.Equal("caller-selected.dll", path);
            opens++;
            return session;
        });
        try
        {
            Assert.Equal(0, opens);
            var first = reader.Read();
            Assert.Equal(MechrevoUwAcpiReadStatus.Active, first.Status);
            Assert.Equal(2400d, first.CpuFanSpeedRpm);
            Assert.Equal(39d, first.CpuFanSpeedPercent);
            Assert.Equal(3200d, first.GpuFanSpeedRpm);
            Assert.Equal(100d, first.GpuFanSpeedPercent);
            Assert.Equal(new[] { 1124, 1125, 1132, 1131, 1883, 1884 }, session.Addresses);
            Assert.Equal(first, reader.Read());
            Assert.Equal(1, opens);
            Assert.Equal(0, session.DisposeCount);
        }
        finally { reader.Dispose(); }
        reader.Dispose();
        Assert.Equal(1, session.DisposeCount);
        Assert.Throws<ObjectDisposedException>(() => reader.Read());
        Assert.Equal(1, opens);
    }

    [Fact]
    public void ZeroIsValidAndInvalidEcBytesDoNotProduceReadings()
    {
        using var zero = new MechrevoUwAcpiReader("zero.dll", path => new RecordedSession(address => 0));
        var result = zero.Read();
        Assert.Equal(MechrevoUwAcpiReadStatus.Active, result.Status);
        Assert.Equal(0d, result.CpuFanSpeedRpm);
        Assert.Equal(0d, result.CpuFanSpeedPercent);
        Assert.Equal(0d, result.GpuFanSpeedRpm);
        Assert.Equal(0d, result.GpuFanSpeedPercent);
        using var invalid = new MechrevoUwAcpiReader("invalid.dll", path => new RecordedSession(address => 256));
        Assert.Equal(new MechrevoUwAcpiReadResult(MechrevoUwAcpiReadStatus.NoData,
            null, null, null, null), invalid.Read());
    }

    [Fact]
    public void FailedLoadIsAttemptedOnceAndDisposalDoesNotOpenTheLibrary()
    {
        var opens = 0;
        using var reader = new MechrevoUwAcpiReader("missing.dll", _ => { opens++; return null; });
        Assert.Equal(MechrevoUwAcpiReadStatus.LibraryUnavailable, reader.Read().Status);
        Assert.Equal(MechrevoUwAcpiReadStatus.LibraryUnavailable, reader.Read().Status);
        Assert.Equal(1, opens);
        using var unused = new MechrevoUwAcpiReader("unused.dll", _ => { opens++; return null; });
        unused.Dispose();
        Assert.Throws<ObjectDisposedException>(() => unused.Read());
        Assert.Equal(1, opens);
    }

    [Fact]
    public void NativeReadFailureReturnsEmptyValuesAndTheOwnedSessionIsReleased()
    {
        var session = new RecordedSession(_ => throw new SEHException("recorded-read-error"));
        var reader = new MechrevoUwAcpiReader("recorded.dll", _ => session);
        try
        {
            var result = reader.Read();
            Assert.Equal(MechrevoUwAcpiReadStatus.ReadFailed, result.Status);
            Assert.Equal("recorded-read-error", result.Error);
            Assert.Null(result.CpuFanSpeedRpm);
            Assert.Null(result.CpuFanSpeedPercent);
            Assert.Null(result.GpuFanSpeedRpm);
            Assert.Null(result.GpuFanSpeedPercent);
        }
        finally { reader.Dispose(); }
        Assert.Equal(1, session.DisposeCount);
    }

    [Fact]
    public async Task ReleaseWaitsForAnInFlightRead()
    {
        using var entered = new ManualResetEventSlim();
        using var continueRead = new ManualResetEventSlim();
        using var releaseRequested = new ManualResetEventSlim();
        var session = new RecordedSession(_ =>
        {
            entered.Set();
            if (!continueRead.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
            return 0;
        });
        using var reader = new MechrevoUwAcpiReader("in-flight.dll", _ => session);
        var read = Task.Run(reader.Read);
        Task? release = null;
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            release = Task.Run(() => { releaseRequested.Set(); reader.Dispose(); });
            Assert.True(releaseRequested.Wait(TimeSpan.FromSeconds(5)));
            Assert.Equal(0, session.DisposeCount);
        }
        finally
        {
            continueRead.Set();
            await read.WaitAsync(TimeSpan.FromSeconds(10));
            if (release is not null) await release.WaitAsync(TimeSpan.FromSeconds(10));
        }
        Assert.Equal(1, session.DisposeCount);
        Assert.Equal(6, session.Addresses.Count);
        Assert.Throws<ObjectDisposedException>(() => reader.Read());
    }

    [Fact]
    public void ProductMonitoringZoneExposesTheSameReleaseBoundary()
    {
        Assert.IsAssignableFrom<IDisposable>(new NotebookOemFanMonitoringZone()).Dispose();
        Assert.Equal(typeof(MechrevoUwAcpiReader).Assembly, typeof(IUwAcpiReadSession).Assembly);
        Assert.NotEqual(typeof(NotebookOemFanMonitoringZone).Assembly, typeof(MechrevoUwAcpiReader).Assembly);
    }

    private sealed class RecordedSession(Func<int, int> read) : IUwAcpiReadSession
    {
        public List<int> Addresses { get; } = [];
        public int DisposeCount;
        public int ReadEc(int address)
        {
            Assert.Equal(0, Volatile.Read(ref DisposeCount));
            Addresses.Add(address);
            return read(address);
        }
        public void Dispose() => Interlocked.Increment(ref DisposeCount);
    }
}
