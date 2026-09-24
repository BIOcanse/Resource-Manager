using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ResourceManager.App.Application.Indexing;
using ResourceManager.App.Application.ProcessAttribution;
using ResourceManager.App.Application.PublicResources;
using ResourceManager.App.Application.Operations;
using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Application.PublicServices;
using ResourceManager.App.Application.PublicServices.AiModels;
using ResourceManager.App.Application.SoftwareDiscovery;
using ResourceManager.App.Application.Software;
using ResourceManager.App.Hosting;
using ResourceManager.App.Hosting.StartupCapabilities;
using ResourceManager.App.Infrastructure.DeviceTopology;
using ResourceManager.App.Infrastructure.Diagnostics;
using ResourceManager.App.Infrastructure.GpuPlacement;
using ResourceManager.App.Infrastructure.Indexing;
using ResourceManager.App.Infrastructure.Operations;
using ResourceManager.App.Infrastructure.Optimization;
using ResourceManager.App.Infrastructure.Optimization.Reports;
using ResourceManager.App.Infrastructure.Persistence;
using ResourceManager.App.Infrastructure.Persistence.Legacy;
using ResourceManager.App.Infrastructure.PublicServices;
using ResourceManager.App.Infrastructure.PublicResources;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.SoftwareDiscovery;
using ResourceManager.App.Domain.SoftwareDiscovery;

namespace Resource_Manager_APP.Tests;

[Collection(LocalResourceCapabilityHandlerProcessStateCollection.Name)]
public sealed class NormalReadOnlyServiceGraphTests
{
    [Fact]
    public async Task NormalReadOnlyProfileDoesNotRegisterAutomaticWriteOwners()
    {
        using var host = Host.CreateDefaultBuilder()
            .ConfigureServices(static services =>
                services.AddResourceManagerApp(["--no-native-ui"], StartupCapabilitySet.NormalReadOnly))
            .Build();

        var capabilities = host.Services.GetRequiredService<StartupCapabilitySet>();
        var hostedServices = host.Services.GetServices<IHostedService>().ToArray();

        Assert.Same(StartupCapabilitySet.NormalReadOnly, capabilities);
        Assert.IsType<NonOwningHostedService<RuntimeSpecializationCoordinator>>(
            hostedServices[0]);
        Assert.DoesNotContain(hostedServices, static service =>
            service is ResourceManagerDatabaseInitializer
            or SqliteLegacyDataImportHostedService
            or GpuLaunchInterceptionReconciler);
        Assert.DoesNotContain(hostedServices, static service => service.GetType().Name == "GpuGraphicsApiDiscoveryService");
        AssertNoHostedAlias<SqlitePortableSoftwareRegistry>(hostedServices);
        AssertNoHostedAlias<HostManagerPublicServiceCoordinatorOwner>(hostedServices);
        AssertHostedAlias<HostManagerDisplayCoordinatorOwner>(hostedServices);
        Assert.False(host.Services
            .GetRequiredService<HostManagerDisplayCoordinatorOwner>()
            .PersistenceEnabled);
        AssertNoHostedAlias<HostManagerOperationCoordinatorOwner>(hostedServices);
        AssertNoHostedAlias<JsonDebugDiagnosticLogWriter>(hostedServices);
        AssertNoHostedAlias<HostManagerReportCoordinatorOwner>(hostedServices);
        AssertNoHostedAlias<HostManagerSmartCoordinator>(hostedServices);
        AssertNoHostedAlias<PortableSoftwareDiscoveryService>(hostedServices);
        Assert.DoesNotContain(hostedServices, static service =>
            service is SharedMemoryPublicResourceBroker
            or SharedResourceSubscriptionMaintenanceService);

        var publicResourceCapability = host.Services
            .GetRequiredService<IHostPublicResourceCapability>()
            .GetCapability();
        Assert.False(publicResourceCapability.Available);
        Assert.Contains(
            "profile-disabled",
            publicResourceCapability.Reason,
            StringComparison.Ordinal);
        Assert.Null(host.Services.GetService<IHostManagerOperationCommandService>());
        Assert.Null(host.Services.GetService<IHostManagerOperationQueryService>());
        Assert.Null(host.Services.GetService<IHostManagerSmartCoordinator>());
        Assert.Null(host.Services.GetService<IHostManagerReportService>());
        Assert.Null(host.Services.GetService<ILocalServiceCatalogQueries>());
        Assert.Null(host.Services.GetService<ILocalAiModelService>());
        Assert.Null(host.Services.GetService<ResourceManagerDatabase>());
        Assert.Null(host.Services.GetService<SqliteSoftwareFileIndex>());
        Assert.Null(host.Services.GetService<ISoftwareFileIndex>());
        Assert.Null(host.Services.GetService<ISoftwareFileIndexRefresher>());

        var portableSoftwareRegistry = host.Services
            .GetRequiredService<IPortableSoftwareRegistry>();
        Assert.IsType<StartupDisabledPortableSoftwareRegistry>(portableSoftwareRegistry);
        Assert.Empty(portableSoftwareRegistry.GetSnapshot());
        var disabled = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            portableSoftwareRegistry.RefreshAsync(CancellationToken.None));
        Assert.Contains("profile-disabled", disabled.Message, StringComparison.Ordinal);
        var observeDisabled = Assert.Throws<InvalidOperationException>(() =>
            portableSoftwareRegistry.Observe(new PortableSoftwareObservation(
                "test",
                "test",
                "Test",
                "portable",
                @"C:\Test\test.exe",
                @"C:\Test")));
        Assert.Contains("profile-disabled", observeDisabled.Message, StringComparison.Ordinal);
        var confirmDisabled = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            portableSoftwareRegistry.ConfirmRootPathAsync(
                new PortableSoftwareRootConfirmationRequest("test", @"C:\Test"),
                CancellationToken.None));
        Assert.Contains("profile-disabled", confirmDisabled.Message, StringComparison.Ordinal);

        var publicServiceAccessPolicy = host.Services
            .GetRequiredService<ILocalPublicServiceAccessPolicy>();
        var accessDecision = await publicServiceAccessPolicy
            .EvaluateAsync(new DefaultHttpContext(), CancellationToken.None);
        Assert.False(accessDecision.Allowed);
        Assert.Equal(StatusCodes.Status404NotFound, accessDecision.StatusCode);
        Assert.Equal("service-disabled", accessDecision.Reason);
        Assert.Equal(0UL, accessDecision.CompletionHandle);
    }

    [Fact]
    public void ExplicitFullProfileRetainsWriteOwners()
    {
        using var host = Host.CreateDefaultBuilder()
            .ConfigureServices(static services =>
                services.AddResourceManagerApp(
                    ["--no-native-ui"],
                    StartupCapabilitySet.Full))
            .Build();

        var hostedServices = host.Services.GetServices<IHostedService>().ToArray();

        Assert.Contains(hostedServices, static service =>
            service is ResourceManagerDatabaseInitializer);
        Assert.Contains(hostedServices, static service =>
            service is SqliteLegacyDataImportHostedService);
        Assert.Contains(hostedServices, static service =>
            service is GpuLaunchInterceptionReconciler);
        Assert.DoesNotContain(hostedServices, static service => service.GetType().Name == "GpuGraphicsApiDiscoveryService");
        AssertHostedAlias<SqlitePortableSoftwareRegistry>(hostedServices);
        AssertHostedAlias<HostManagerOperationCoordinatorOwner>(hostedServices);
        Assert.True(host.Services
            .GetRequiredService<HostManagerDisplayCoordinatorOwner>()
            .PersistenceEnabled);
        AssertHostedAlias<HostManagerSmartCoordinator>(hostedServices);
        AssertHostedAlias<PortableSoftwareDiscoveryService>(hostedServices);
        Assert.Same(
            host.Services.GetRequiredService<SqlitePortableSoftwareRegistry>(),
            host.Services.GetRequiredService<IPortableSoftwareRegistry>());
        Assert.Same(
            host.Services.GetRequiredService<SqliteSoftwareFileIndex>(),
            host.Services.GetRequiredService<ISoftwareFileIndex>());
        Assert.Same(
            host.Services.GetRequiredService<SqliteSoftwareFileIndex>(),
            host.Services.GetRequiredService<ISoftwareFileIndexRefresher>());
        Assert.Contains(hostedServices, static service =>
            service is SharedMemoryPublicResourceBroker);
        Assert.Contains(hostedServices, static service =>
            service is SharedResourceSubscriptionMaintenanceService);
    }

    [Fact]
    public async Task NormalReadOnlyProfileSoftwareAttributionReadDoesNotMutatePackageRoot()
    {
        var packageRoot = Path.Combine(
            Path.GetTempPath(),
            $"resource-manager-normal-read-{Guid.NewGuid():N}");
        Directory.CreateDirectory(packageRoot);
        try
        {
            using var host = Host.CreateDefaultBuilder()
                .UseContentRoot(packageRoot)
                .ConfigureServices(static services =>
                    services.AddResourceManagerApp(["--no-native-ui"], StartupCapabilitySet.NormalReadOnly))
                .Build();
            var before = EnumerateRelativePaths(packageRoot);
            var runtimeCoordinator = host.Services
                .GetRequiredService<RuntimeSpecializationCoordinator>();
            await runtimeCoordinator.StartAsync(CancellationToken.None);

            try
            {
                var records = await host.Services
                    .GetRequiredService<ISoftwareRegistryView>()
                    .GetSoftwareAsync(CancellationToken.None);
                var catalog = await host.Services
                    .GetRequiredService<IRuntimeProcessAttributionCatalogProvider>()
                    .GetCatalogAsync(CancellationToken.None);

                Assert.NotEmpty(records);
                Assert.True(catalog.Generation >= 1);
                Assert.Equal(before, EnumerateRelativePaths(packageRoot));
            }
            finally
            {
                await runtimeCoordinator.StopAsync(CancellationToken.None);
            }
        }
        finally
        {
            Directory.Delete(packageRoot, recursive: true);
        }
    }

    private static string[] EnumerateRelativePaths(string root)
        => Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(root, path))
            .OrderBy(static path => path, StringComparer.Ordinal)
            .ToArray();

    private static void AssertNoHostedAlias<T>(IEnumerable<IHostedService> services)
        where T : class, IHostedService
        => Assert.DoesNotContain(
            services,
            static service => service is NonOwningHostedService<T>);

    private static void AssertHostedAlias<T>(IEnumerable<IHostedService> services)
        where T : class, IHostedService
        => Assert.Contains(
            services,
            static service => service is NonOwningHostedService<T>);
}
