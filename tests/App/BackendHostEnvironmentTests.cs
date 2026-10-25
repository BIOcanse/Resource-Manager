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
    public void ServiceGraph_RegistersGenericWtsSessionBroker()
    {
        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IHostEnvironment>(new FanTestEnvironment())
            .AddResourceManagerApp(
                [],
                StartupCapabilitySet.NormalReadOnly,
                BackendHostEnvironment.Compile(
                    isWindowsService: true,
                    isLocalSystem: true));
        using var provider = services.BuildServiceProvider();

        Assert.IsType<WindowsInteractiveUserSessionBroker>(
            provider.GetRequiredService<IInteractiveUserSessionBroker>());
        Assert.DoesNotContain(
            services,
            static registration => registration.ServiceType == typeof(IHostedService)
                && registration.ImplementationType?.Name.Contains("NativeUi", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void InteractiveAndNoUiGraphsKeepSessionValidationWithoutNativeUiLauncher()
    {
        var interactiveServices = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IHostEnvironment>(new FanTestEnvironment())
            .AddResourceManagerApp(
                [],
                StartupCapabilitySet.NormalReadOnly,
                BackendHostEnvironment.Interactive);
        var headlessServices = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IHostEnvironment>(new FanTestEnvironment())
            .AddResourceManagerApp(
                ["--no-native-ui"],
                StartupCapabilitySet.NormalReadOnly,
                BackendHostEnvironment.Compile(
                    isWindowsService: true,
                    isLocalSystem: true));
        using var interactiveProvider = interactiveServices.BuildServiceProvider();
        using var headlessServiceProvider = headlessServices.BuildServiceProvider();

        Assert.IsType<WindowsInteractiveUserSessionBroker>(
            Assert.Single(interactiveProvider.GetServices<IInteractiveUserSessionBroker>()));
        Assert.IsType<WindowsInteractiveUserSessionBroker>(
            Assert.Single(headlessServiceProvider.GetServices<IInteractiveUserSessionBroker>()));
        Assert.DoesNotContain(
            interactiveServices,
            static registration => registration.ServiceType == typeof(IHostedService)
                && registration.ImplementationType?.Name.Contains("NativeUi", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(
            headlessServices,
            static registration => registration.ServiceType == typeof(IHostedService)
                && registration.ImplementationType?.Name.Contains("NativeUi", StringComparison.Ordinal) == true);
    }
}
