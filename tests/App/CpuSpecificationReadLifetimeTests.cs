using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Application.CpuTopology;
using ResourceManager.App.Domain.CpuTopology;
using ResourceManager.App.Infrastructure.CpuTopology;
using ResourceManager.ReadWrite.Windows.Platform;

namespace Resource_Manager_APP.Tests;

public sealed class CpuSpecificationReadLifetimeTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(24)]
    public async Task ProductionTopologyCompositionQueriesSpecificationOnceAndKeepsSampling(int captures)
    {
        var queries = new List<string>();
        var usageReads = 0;
        var sampler = new WindowsCpuTopologyReader(new Overrides(), command =>
        {
            queries.Add(command);
            return new([Row("Test CPU")], null);
        }, () =>
        {
            usageReads++;
            return new Dictionary<int, WindowsCpuOsTopologyReader.ProcessorTimes>
                { [0] = new(usageReads, usageReads * 2, usageReads) };
        });
        using var sampling = new HostManagerSamplingSubscriptionTestFixture();
        using var provider = new CpuTopologySnapshotProvider(sampler, sampling.Owner,
            NullLogger<CpuTopologySnapshotProvider>.Instance);
        Assert.Empty(queries);
        for (var index = 0; index < captures; index++)
        {
            await provider.CaptureInformationAsync(CancellationToken.None);
            Assert.Equal("Test CPU", provider.GetSnapshot()!.Specification.Name);
            Assert.Equal("Win32_Processor", provider.GetSnapshot()!.Specification.Source);
        }
        var query = Assert.Single(queries);
        Assert.DoesNotContain("CurrentClockSpeed", query);
        Assert.Equal(captures, usageReads);
    }

    [Fact]
    public void OsTopologyOwnerRetainsThreadCapabilitiesAndTheirFailureNotes()
    {
        var reads = 0;
        var owner = new WindowsCpuOsTopologyReader(() => new Dictionary<int, WindowsCpuOsTopologyReader.ProcessorTimes>(),
            (ids, notes) =>
            {
                reads++;
                notes.Add("original CPUID observation");
                return ids.Select(id => new CpuThreadCpuidInformation(id, null)).ToArray();
            });
        for (var index = 0; index < 24; index++)
        {
            var notes = new List<string>();
            Assert.Single(owner.ReadThreadCpuidOnce([0], notes));
            Assert.Equal(new[] { "original CPUID observation" }, notes);
        }
        Assert.Equal(1, reads);
    }

    [Fact]
    public async Task SubscribedProductionProviderKeepsRefreshingUsageWithOneSpecificationRead()
    {
        var queries = 0;
        var usageReads = 0;
        var sampler = new WindowsCpuTopologyReader(new Overrides(), _ =>
        {
            Interlocked.Increment(ref queries);
            return new([Row("Test CPU")], null);
        }, () =>
        {
            var round = Interlocked.Increment(ref usageReads);
            return new Dictionary<int, WindowsCpuOsTopologyReader.ProcessorTimes>
                { [0] = new(round, round * 2, round) };
        });
        using var sampling = new HostManagerSamplingSubscriptionTestFixture();
        using var provider = new CpuTopologySnapshotProvider(sampler, sampling.Owner,
            NullLogger<CpuTopologySnapshotProvider>.Instance);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var stream = provider.SubscribeAsync("fixed-spec-live-usage", TimeSpan.FromMilliseconds(100),
            timeout.Token).GetAsyncEnumerator();
        var first = stream.MoveNextAsync().AsTask();
        await provider.StartAsync(CancellationToken.None);
        try
        {
            Assert.True(await first.WaitAsync(timeout.Token));
            var firstReads = Volatile.Read(ref usageReads);
            Assert.True(await stream.MoveNextAsync());
            Assert.True(Volatile.Read(ref usageReads) > firstReads);
            Assert.Equal(1, Volatile.Read(ref queries));
        }
        finally
        {
            await provider.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public void FailedFixedReadKeepsItsVisibleFallbackWithoutRetrying()
    {
        var queries = 0;
        var sampler = new WindowsCpuTopologyReader(new Overrides(), _ =>
        {
            queries++;
            return new([], "unavailable");
        });
        var first = sampler.CaptureTopology();
        var second = sampler.CaptureTopology();
        Assert.Equal("RuntimeInformation", first.Specification.Source);
        Assert.Equal(first.Specification.Name, second.Specification.Name);
        Assert.Equal(1, queries);
    }

    [Fact]
    public void ReadOnceValueBelongsToTheBackendOwner()
    {
        var queries = 0;
        QueryResult Query(string _) { queries++; return new([Row("Test CPU")], null); }
        var first = new WindowsCpuTopologyReader(new Overrides(), Query);
        var second = new WindowsCpuTopologyReader(new Overrides(), Query);
        _ = first.CaptureTopology();
        _ = first.CaptureTopology();
        _ = second.CaptureTopology();
        Assert.Equal(2, queries);
    }

    private static Dictionary<string, object?> Row(string name) => new()
    {
        ["Name"] = name, ["Manufacturer"] = "Test", ["NumberOfCores"] = 1,
        ["NumberOfLogicalProcessors"] = 1, ["MaxClockSpeed"] = 3000,
        ["L2CacheSize"] = 256, ["L3CacheSize"] = 1024
    };

    private sealed class Overrides : ICpuCorePerformanceOverrideStore
    {
        public CpuPerformanceOverrides LoadConfiguration(string cpuName) => new(LoadScores(cpuName), null);
        public IReadOnlyDictionary<int, double> LoadScores(string cpuName) => new Dictionary<int, double>();
        public void SaveBaselineRatio(double ratio) => throw new NotSupportedException();
        public void ResetBaselineRatio() => throw new NotSupportedException();
        public CpuCorePerformanceOverrideResult Save(CpuCorePerformanceOverrideRequest request) => throw new NotSupportedException();
        public CpuCorePerformanceOverrideResult Reset(string cpuName) => throw new NotSupportedException();
    }
}
