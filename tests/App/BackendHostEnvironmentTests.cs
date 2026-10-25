using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ResourceManager.App.Hosting;
using ResourceManager.App.Hosting.StartupCapabilities;
using ResourceManager.App.Infrastructure.ServiceHosting;

namespace Resource_Manager_APP.Tests;

public sealed class BackendHostEnvironmentTests
{
    [Fact]
    public void Compile_RejectsWindowsServiceOutsideLocalSystem()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            BackendHostEnvironment.Compile(
                isWindowsService: true,
                isLocalSystem: false));

        Assert.Contains(
            BackendHostEnvironment.WindowsServiceName,
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ServiceGraph_RegistersOnlyTheWtsNativeUiOwner()
    {
        using var provider = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IHostEnvironment>(new FanTestEnvironment())
            .AddResourceManagerApp(
                [],
                StartupCapabilitySet.NormalReadOnly,
                BackendHostEnvironment.Compile(
                    isWindowsService: true,
                    isLocalSystem: true))
            .BuildServiceProvider();

        Assert.IsType<WindowsInteractiveUserSessionBroker>(
            provider.GetRequiredService<IInteractiveUserSessionBroker>());
        Assert.Contains(
            provider.GetServices<IHostedService>(),
            static service => service is NativeUiLaunchHostedService);
    }

    [Fact]
    public void InteractiveAndNoUiGraphsKeepSessionValidationWithoutNativeUiLauncher()
    {
        using var interactiveProvider = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IHostEnvironment>(new FanTestEnvironment())
            .AddResourceManagerApp(
                [],
                StartupCapabilitySet.NormalReadOnly,
                BackendHostEnvironment.Interactive)
            .BuildServiceProvider();
        using var headlessServiceProvider = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IHostEnvironment>(new FanTestEnvironment())
            .AddResourceManagerApp(
                ["--no-native-ui"],
                StartupCapabilitySet.NormalReadOnly,
                BackendHostEnvironment.Compile(
                    isWindowsService: true,
                    isLocalSystem: true))
            .BuildServiceProvider();

        Assert.IsType<WindowsInteractiveUserSessionBroker>(
            Assert.Single(interactiveProvider.GetServices<IInteractiveUserSessionBroker>()));
        Assert.IsType<WindowsInteractiveUserSessionBroker>(
            Assert.Single(headlessServiceProvider.GetServices<IInteractiveUserSessionBroker>()));
        Assert.DoesNotContain(
            interactiveProvider.GetServices<IHostedService>(),
            static service => service is NativeUiLaunchHostedService);
        Assert.DoesNotContain(
            headlessServiceProvider.GetServices<IHostedService>(),
            static service => service is NativeUiLaunchHostedService);
    }
}
