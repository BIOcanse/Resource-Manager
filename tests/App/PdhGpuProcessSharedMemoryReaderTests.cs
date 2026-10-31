using ResourceManager.App.Infrastructure.ResourceBreakdown;
using ResourceManager.ReadWrite.Native.Pdh;
using ResourceManager.ReadWrite.Windows.Pdh;
using ResourceManager.ReadWrite.Windows.Processes;

namespace Resource_Manager_APP.Tests;

/// <summary>Synthetic PDH values for the per-process shared GPU memory reader. No real PDH query is opened.</summary>
public sealed class PdhGpuProcessSharedMemoryReaderTests
{
    private const ulong LuidA = 0x0000_0001_0000_ABCD;
    private const ulong LuidB = 0x0000_0002_0000_1234;
    private static readonly DateTimeOffset Observed = DateTimeOffset.UnixEpoch.AddSeconds(5);

    [Fact]
    public void NoRequestedAdapterOpensNoQuery()
    {
        var api = new RecordedPdhApi();
        var reader = new PdhGpuProcessSharedMemoryReader(api, new FixedClock(Observed));

        var empty = reader.Read([]);
        var zeroOnly = reader.Read([0UL]);

        Assert.Same(PdhGpuProcessSharedMemoryRead.NotRequested, empty);
        Assert.Same(PdhGpuProcessSharedMemoryRead.NotRequested, zeroOnly);
        Assert.Equal(0, api.OpenCalls);
        Assert.Null(api.AddedPath);
    }

    [Fact]
    public void SubscribedReadSumsPhysicalInstancesPerProcessAndAdapterAndClosesTheQuery()
    {
        var api = new RecordedPdhApi
        {
            Values =
            [
                Value(Name(41, LuidA, 0), 100),
                Value(Name(41, LuidA, 1), 20),
                Value(Name(41, LuidB, 0), 7),
                Value(Name(42, LuidA, 0), 300),
                Value(AdapterName(LuidA, 0), 999), // adapter-level instance, not a process
                Value(Name(43, 0x0000_0009_0000_0001, 0), 555) // adapter not requested
            ]
        };
        var reader = new PdhGpuProcessSharedMemoryReader(api, new FixedClock(Observed));

        var read = reader.Read([LuidA, LuidB]);

        Assert.Equal(NativePdhProviderAvailability.Available, read.ProviderAvailability);
        Assert.Equal(NativePdhResultCode.Ok, read.ResultCode);
        Assert.Equal(Observed, read.ObservedAt);
        Assert.Equal(PdhGpuProcessSharedMemoryReader.SharedUsageWildcardPath, api.AddedPath);
        Assert.Equal(@"\GPU Process Memory(*)\Shared Usage", api.AddedPath);
        Assert.Equal(3, read.SharedUsageBytes.Count);
        Assert.Equal(120UL, read.SharedUsageBytes[(LuidA, 41)]);
        Assert.Equal(7UL, read.SharedUsageBytes[(LuidB, 41)]);
        Assert.Equal(300UL, read.SharedUsageBytes[(LuidA, 42)]);
        Assert.Equal(1, api.OpenCalls);
        Assert.Equal(1, api.CollectCalls);
        Assert.Equal(api.OpenCalls, api.CloseCalls);
    }

    [Fact]
    public void ReleaseLeavesNoQueryOpenAndLaterUnsubscribedReadsOpenNothing()
    {
        var api = new RecordedPdhApi { Values = [Value(Name(41, LuidA, 0), 10)] };
        var reader = new PdhGpuProcessSharedMemoryReader(api, new FixedClock(Observed));

        _ = reader.Read([LuidA]);
        _ = reader.Read([LuidA]);
        Assert.Equal(2, api.OpenCalls);
        Assert.Equal(2, api.CloseCalls); // each read closes its own query; nothing survives between reads

        _ = reader.Read([]); // holder released: the sampler requests no adapter
        Assert.Equal(2, api.OpenCalls);
        Assert.Equal(2, api.CloseCalls);
    }

    [Fact]
    public void FailedCollectStillClosesTheQueryAndReportsUnavailable()
    {
        var api = new RecordedPdhApi { CollectStatus = unchecked((int)0xC000_0BBA), Values = [Value(Name(41, LuidA, 0), 10)] };
        var reader = new PdhGpuProcessSharedMemoryReader(api, new FixedClock(Observed));

        var read = reader.Read([LuidA]);

        Assert.Equal(NativePdhProviderAvailability.Unavailable, read.ProviderAvailability);
        Assert.Equal(NativePdhResultCode.PdhError, read.ResultCode);
        Assert.Null(read.ObservedAt);
        Assert.Empty(read.SharedUsageBytes);
        Assert.Equal(1, api.CloseCalls);
    }

    [Fact]
    public void BadStatusOrDuplicateInstanceDropsOnlyThatProcessAdapterPair()
    {
        var api = new RecordedPdhApi
        {
            Values =
            [
                Value(Name(41, LuidA, 0), 10),
                Value(Name(41, LuidA, 1), 10, status: 0xC000_0BC6), // partial sum → no value for (A, 41)
                Value(Name(42, LuidA, 0), 30),
                Value(Name(42, LuidA, 0), 30), // repeated instance name → no value for (A, 42)
                Value(Name(44, LuidA, 0), 50)
            ]
        };
        var reader = new PdhGpuProcessSharedMemoryReader(api, new FixedClock(Observed));

        var read = reader.Read([LuidA]);

        Assert.Equal(NativePdhProviderAvailability.Available, read.ProviderAvailability);
        Assert.Equal(2, read.FailedCounterCount);
        var only = Assert.Single(read.SharedUsageBytes);
        Assert.Equal((LuidA, 44), only.Key);
        Assert.Equal(50UL, only.Value);
    }

    [Fact]
    public void NoInstanceForTheAdapterIsNoDataNotZero()
    {
        var api = new RecordedPdhApi { Values = [Value(Name(41, LuidB, 0), 10)] };
        var reader = new PdhGpuProcessSharedMemoryReader(api, new FixedClock(Observed));

        var read = reader.Read([LuidA]);

        Assert.Equal(NativePdhProviderAvailability.Unavailable, read.ProviderAvailability);
        Assert.Equal(NativePdhResultCode.NoData, read.ResultCode);
        Assert.Empty(read.SharedUsageBytes);
    }

    [Theory]
    [InlineData("pid_41_luid_0x00000001_0x0000ABCD_phys_0", true, 41, LuidA)]
    [InlineData("PID_7_LUID_0X00000002_0X00001234_PHYS_3", true, 7, LuidB)]
    [InlineData("luid_0x00000001_0x0000ABCD_phys_0", false, 0, 0UL)]
    [InlineData("pid_0_luid_0x00000001_0x0000ABCD_phys_0", false, 0, 0UL)]
    [InlineData("pid__luid_0x00000001_0x0000ABCD_phys_0", false, 0, 0UL)]
    [InlineData("pid_41_luid_0x00000001_0x0000ABCD", false, 0, 0UL)]
    public void ProcessInstanceNameParsesExactWindowsShape(string name, bool parses, int pid, ulong luid)
    {
        Assert.Equal(parses, PdhGpuCounterInstanceName.TryParseProcess(name, out var parsedPid, out var parsedLuid));
        Assert.Equal(pid, parsedPid);
        Assert.Equal(luid, parsedLuid);
    }

    [Fact]
    public void ProcessGpuReaderBindsValuesToValidatedInstancesAndOpensNothingWithoutAdapters()
    {
        var api = new RecordedPdhApi
        {
            Values = [Value(Name(41, LuidA, 0), 100), Value(Name(42, LuidA, 0), 200), Value(Name(43, LuidA, 0), 300)]
        };
        var sharedReader = new PdhGpuProcessSharedMemoryReader(api, new FixedClock(Observed));
        // 42 was reused between the process sample and the PDH read: validation does not return its expected instance.
        var reader = new PdhProcessGpuReader(UnavailableNativePdhSnapshotSource.Instance,
            static instances => instances.Where(static process => process.ProcessId != 42).ToHashSet(), sharedReader);
        ProcessInstanceKey[] expected = [new(41, 1000), new(42, 2000)];

        var notRequested = reader.ReadSharedSystemMemory([], expected);
        Assert.Same(ProcessGpuSharedSystemMemoryRead.NotRequested, notRequested);
        Assert.Equal(0, api.OpenCalls);

        var read = reader.ReadSharedSystemMemory([LuidA], expected);

        Assert.Equal(NativePdhProviderAvailability.Available, read.Availability);
        Assert.Equal(Observed, read.ObservedAt);
        var only = Assert.Single(read.Bytes); // 42 failed validation; 43 was not an expected instance
        Assert.Equal((LuidA, 41, 1000UL), only.Key);
        Assert.Equal(100D, only.Value);
        Assert.Equal(1, api.CloseCalls);
    }

    [Fact]
    public void SourceLessProcessGpuReaderReportsUnavailableWithoutReading()
    {
        var read = new PdhProcessGpuReader().ReadSharedSystemMemory([LuidA], [new(41, 1000)]);

        Assert.Equal(NativePdhProviderAvailability.Unavailable, read.Availability);
        Assert.Empty(read.Bytes);
    }

    private static string Name(int pid, ulong luid, int physical)
        => FormattableString.Invariant($"pid_{pid}_{AdapterName(luid, physical)}");

    private static string AdapterName(ulong luid, int physical)
        => FormattableString.Invariant($"luid_0x{(uint)(luid >> 32):X8}_0x{(uint)luid:X8}_phys_{physical}");

    private static PdhFormattedCounterValue Value(string name, long value, uint status = 0)
        => new(name, status, value);

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class RecordedPdhApi : IPdhFormattedCounterArrayApi
    {
        public int CollectStatus { get; init; }
        public IReadOnlyList<PdhFormattedCounterValue> Values { get; init; } = [];
        public int OpenCalls { get; private set; }
        public int CollectCalls { get; private set; }
        public int CloseCalls { get; private set; }
        public string? AddedPath { get; private set; }

        public int OpenQuery(out IntPtr query)
        {
            OpenCalls++;
            query = new IntPtr(1);
            return 0;
        }

        public int AddEnglishCounter(IntPtr query, string counterPath, out IntPtr counter)
        {
            AddedPath = counterPath;
            counter = new IntPtr(2);
            return 0;
        }

        public int CollectQueryData(IntPtr query)
        {
            CollectCalls++;
            return CollectStatus;
        }

        public int GetFormattedCounterArray(IntPtr counter, uint format,
            out IReadOnlyList<PdhFormattedCounterValue> values)
        {
            values = Values;
            return 0;
        }

        public int CloseQuery(IntPtr query)
        {
            CloseCalls++;
            return 0;
        }
    }
}
