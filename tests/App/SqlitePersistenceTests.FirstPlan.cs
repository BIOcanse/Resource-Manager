using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Domain.SoftwareDiscovery;
using ResourceManager.App.Hosting;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.SoftwareDiscovery;
using ResourceManager.App.StateItems;
using ResourceManager.Shared.BusinessBlocks;

namespace Resource_Manager_APP.Tests;

public sealed partial class SqlitePersistenceTests
{
    [Fact]
    public async Task PortableRegistryColdStartWaitsForRealPlanWithoutInventingARegistry()
    {
        var deployment = new HostManagerDeploymentState();
        var provider = new RuntimePlanProvider(deployment);
        var database = CreateDatabase();
        using var registry = new SqlitePortableSoftwareRegistry(database, provider,
            new HostManagerPortableSoftwareRegistryRuntime(provider, deployment),
            NullLogger<SqlitePortableSoftwareRegistry>.Instance, ledgerOwner);
        Assert.Empty(registry.GetSnapshot());
        await registry.StartAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(registry.GetSnapshot());
        Assert.Null(database.InitializationState);
        var refresh = registry.RefreshAsync(default);
        Assert.False(refresh.IsCompleted);

        provider.Publish(CompiledRuntimePlan.Default with
        { Version = 1, HostManager = HostManagerTestPlanFactory.CreatePlan() });
        Assert.Empty(await refresh.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.NotNull(database.InitializationState);
        await registry.ObserveAsync(new PortableSoftwareObservation("catalog:cold", "cold", "Cold", "other",
            "c:/portable/cold/app.exe", "c:/portable/cold"), default);
        Assert.Equal("catalog:cold", Assert.Single(registry.GetSnapshot()).SoftwareId);
        await registry.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task PortableRegistryObservationBeforeFirstPlanIsCancellableWithoutWriting()
    {
        var deployment = new HostManagerDeploymentState();
        var provider = new RuntimePlanProvider(deployment);
        var database = CreateDatabase();
        using var registry = new SqlitePortableSoftwareRegistry(database, provider,
            new HostManagerPortableSoftwareRegistryRuntime(provider, deployment),
            NullLogger<SqlitePortableSoftwareRegistry>.Instance, ledgerOwner);
        await registry.StartAsync(default);
        using var cancellation = new CancellationTokenSource();
        var observation = new PortableSoftwareObservation("catalog:canceled", "canceled", "Canceled", "other",
            "c:/portable/canceled/app.exe", "c:/portable/canceled");
        var admission = registry.ObserveAsync(observation, cancellation.Token);
        Assert.False(admission.IsCompleted);
        Assert.Empty(registry.GetSnapshot());
        Assert.Null(database.InitializationState);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => admission.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(registry.ExecuteTask!.IsCompleted);

        provider.Publish(CompiledRuntimePlan.Default with
        { Version = 1, HostManager = HostManagerTestPlanFactory.CreatePlan() });
        var initialized = typeof(SqlitePortableSoftwareRegistry).GetField("initializationTask",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        await ((Task)initialized.GetValue(registry)!).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(registry.GetSnapshot());
        await registry.ObserveAsync(observation, default);
        Assert.Equal("catalog:canceled", Assert.Single(registry.GetSnapshot()).SoftwareId);
        await registry.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task PortableRegistryStopCancelsInitializationWhileItOwnsTheOperationGate()
    {
        var deployment = new HostManagerDeploymentState();
        var provider = new RuntimePlanProvider(deployment);
        provider.Publish(CompiledRuntimePlan.Default with
        { Version = 1, HostManager = HostManagerTestPlanFactory.CreatePlan() });
        var database = CreateDatabase();
        using var registry = new SqlitePortableSoftwareRegistry(database, provider,
            new HostManagerPortableSoftwareRegistryRuntime(provider, deployment),
            NullLogger<SqlitePortableSoftwareRegistry>.Instance, ledgerOwner);
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var databaseGate = (SemaphoreSlim)database.GetType().GetField("initializationGate", flags)!.GetValue(database)!;
        var operationGate = (SemaphoreSlim)registry.GetType().GetField("operationGate", flags)!.GetValue(registry)!;
        await databaseGate.WaitAsync();
        var start = registry.StartAsync(default);
        Task<IReadOnlyList<PortableSoftwareRegistration>>? read = null;
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (operationGate.CurrentCount != 0) await Task.Delay(1, deadline.Token);
            Assert.False(start.IsCompleted);
            var execution = registry.ExecuteTask;
            Assert.NotNull(execution);
            Assert.False(execution!.IsCompleted);
            // A first read must not queue behind initialization's database wait.
            read = Task.Run(registry.GetSnapshot);
            Assert.Empty(await read.WaitAsync(TimeSpan.FromSeconds(5)));
            await registry.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
            await execution.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start);
            Assert.True(execution.IsCompletedSuccessfully);
            Assert.Empty(registry.GetSnapshot());
            Assert.Null(database.InitializationState);
        }
        finally
        {
            databaseGate.Release();
            try { await start.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException) { }
            if (read is not null) await read.WaitAsync(TimeSpan.FromSeconds(5));
            await registry.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task PortableRegistryColdStopDrainsBeforePublicationAndIgnoresLaterPlan()
    {
        var deployment = new HostManagerDeploymentState();
        var provider = new RuntimePlanProvider(deployment);
        var database = CreateDatabase();
        using var registry = new SqlitePortableSoftwareRegistry(database, provider,
            new HostManagerPortableSoftwareRegistryRuntime(provider, deployment),
            NullLogger<SqlitePortableSoftwareRegistry>.Instance, ledgerOwner);
        await registry.StartAsync(default);
        var execution = registry.ExecuteTask;
        Assert.NotNull(execution);
        var refresh = registry.RefreshAsync(default);
        Assert.False(refresh.IsCompleted);
        await registry.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
        await execution!.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => refresh);
        provider.Publish(CompiledRuntimePlan.Default with
        { Version = 1, HostManager = HostManagerTestPlanFactory.CreatePlan() });
        Assert.Empty(registry.GetSnapshot());
        Assert.Null(database.InitializationState);
    }

    [Fact]
    public async Task HostedPortableRegistryCanStopWhileItsFirstPlanInputIsAbsent()
    {
        var deployment = new HostManagerDeploymentState();
        var provider = new RuntimePlanProvider(deployment);
        var database = CreateDatabase();
        using var registry = new SqlitePortableSoftwareRegistry(database, provider,
            new HostManagerPortableSoftwareRegistryRuntime(provider, deployment),
            NullLogger<SqlitePortableSoftwareRegistry>.Instance, ledgerOwner);
        using var host = new HostBuilder().ConfigureServices(services =>
        {
            services.AddSingleton(registry);
            services.AddHostedServiceAlias<SqlitePortableSoftwareRegistry>(BlockUids.SqlitePortableSoftwareRegistry,
                static service => service.ExecuteTask, forwardRunFailureToHost: true);
            NativeBackendBlockTestSupport.AddFullTreeHostFixture(services);
        }).Build();
        await host.StartAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await BusinessBlockTestSupport.State(host.Services.GetRequiredService<
            ResourceManager.App.Infrastructure.RuntimeSpecialization.BusinessBlocks.BusinessBlockScheduler>(),
            BlockUids.SqlitePortableSoftwareRegistry, BusinessBlockState.Running);
        await host.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(registry.ExecuteTask!.IsCompletedSuccessfully);
        Assert.Empty(registry.GetSnapshot());
        Assert.Null(database.InitializationState);
    }
}
