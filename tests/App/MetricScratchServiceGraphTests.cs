using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Application.DiskUsage;
using ResourceManager.App.Hosting;
using ResourceManager.App.Hosting.StartupCapabilities;
using ResourceManager.App.Infrastructure.Adaptation;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.Ledger;
using ResourceManager.ReadWrite.Native;

namespace Resource_Manager_APP.Tests;

public sealed class MetricScratchServiceGraphTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DiDisposesSelfFacadeBeforeEarlierNativeCurrentBorrowWithoutClosingItsSharedLedger(bool closeFacadeFirst)
    {
        // Production metric current storage outlives a stopped sampler. Resolve
        // that same production current factory before the later self façade,
        // so DI must dispose the façade while current still owns its real pin.
        using var host = Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddResourceManagerApp(["--no-native-ui"], StartupCapabilitySet.Full);
                services.AddSingleton(provider => provider.GetRequiredService<HostManagerMetricSnapshotOwner>()
                    .CreateCurrentSession());
            })
            .Build();
        var services = host.Services;
        services.GetRequiredService<RuntimePlanProvider>().Publish(CompiledRuntimePlan.Default with
        {
            Version = 1,
            HostManager = HostManagerTestPlanFactory.CreatePlan()
        });
        var ledger = services.GetRequiredService<ResourceManagerLedgerOwner>();
        var current = services.GetRequiredService<NativeMetricSnapshotSession>();
        var cells = Enumerable.Range(1, 128).Select(index => new NativeMetricCurrentValue
        {
            DatasetHandle = 1, CellHandle = checked((ulong)index), ValueBits = checked((ulong)index),
            ValueKind = (uint)NativeMetricSnapshotValueKind.Unsigned64, Flags = 1
        }).ToArray();
        current.CommitCurrent([1], [], cells, []);
        Assert.True(ledger.ReadTable().RegisteredBytes > 0);
        var facade = services.GetRequiredService<ResourceManagerSelfLocalResourceManager>();
        var allocator = services.GetRequiredService<IDiskUsageTreePayloadAllocator>();
        Assert.NotSame(ledger, allocator);
        Assert.False(allocator is IDisposable);

        if (closeFacadeFirst)
        {
            Assert.Null(Record.Exception(facade.Dispose));
            Assert.Equal(128, current.ReadCurrent().Values.Length);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => facade.TickAsync());
            // Explicit global-close remains honest even after the façade is disposed.
            Assert.Equal(LedgerStatus.InUse, facade.TryClose());
        }
        Assert.Equal(128, current.ReadCurrent().Values.Length);
        var disposalError = Record.Exception(host.Dispose);
        try
        {
            Assert.Null(disposalError);
            Assert.Throws<ObjectDisposedException>(() => current.ReadCurrent());
            Assert.Equal(LedgerStatus.Ok, ledger.TryClose());
        }
        finally
        {
            // The failing-old-implementation path must release the same native pin.
            current.Dispose();
            ledger.Dispose();
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void MetricUsesTheOneLedgerInReadOnlyFullAndEffectsOnlyGraphs(int profile)
    {
        var capabilities = profile switch
        {
            0 => StartupCapabilitySet.NormalReadOnly,
            1 => StartupCapabilitySet.Full,
            _ => new StartupCapabilitySet("effects-only", StartupCapability.RuntimeEffectOwners)
        };
        using var host = Host.CreateDefaultBuilder()
            .ConfigureServices(services => services.AddResourceManagerApp(["--no-native-ui"], capabilities))
            .Build();
        var services = host.Services;
        var metric = services.GetRequiredService<HostManagerMetricSnapshotOwner>();
        Assert.Same(metric, services.GetRequiredService<HostManagerMetricSnapshotOwner>());
        var ledger = Assert.Single(services.GetServices<ResourceManagerLedgerOwner>());
        Assert.Same(ledger, services.GetRequiredService<ResourceManagerLedgerOwner>());
        var pdh = services.GetRequiredService<NativePdhCollector>();
        Assert.Same(pdh, services.GetRequiredService<NativePdhCollector>());
        Assert.Contains(typeof(NativePdhCollector).GetConstructors(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic),
            candidate => candidate.GetParameters().Any(parameter => parameter.ParameterType == typeof(ResourceManagerLedgerOwner)));
        var constructor = Assert.Single(typeof(HostManagerMetricSnapshotOwner).GetConstructors());
        Assert.Contains(constructor.GetParameters(), parameter => parameter.ParameterType == typeof(ResourceManagerLedgerOwner));
        Assert.DoesNotContain(constructor.GetParameters(), parameter => parameter.ParameterType == typeof(ResourceManagerSelfLocalResourceManager));
        if (profile == 0)
        {
            Assert.Null(services.GetService<ResourceManagerSelfLocalResourceManager>());
            Assert.Null(services.GetService<IProcessResourcePolicyWriter>());
        }
        else
        {
            Assert.NotNull(services.GetService<ResourceManagerSelfLocalResourceManager>());
            Assert.NotNull(services.GetService<IProcessResourcePolicyWriter>());
        }
        // Construction resolves the real production DI graph; no hosted start or device sampling.
    }
}
