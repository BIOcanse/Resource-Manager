using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Hosting;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.ServiceHosting;
using ResourceManager.Launcher;
using ResourceManager.Shared.ServiceHosting;

namespace Resource_Manager_APP.Tests;

public sealed class DesktopStartupTests
{
    [Fact]
    public void FailedLoginLaunchIsNotRecordedAsSuccessful()
    {
        var plans = new RuntimePlanProvider(new HostManagerDeploymentState());
        plans.Publish(Plan(1, true));
        var broker = new Sessions { FailNextLaunch = true };
        using var owner = new NativeUiLaunchHostedService(broker, plans, NullLogger<NativeUiLaunchHostedService>.Instance);
        owner.ScanActiveSessions("fixture-ui.exe");
        Assert.Empty(broker.Launches);
        owner.ScanActiveSessions("fixture-ui.exe");
        owner.ScanActiveSessions("fixture-ui.exe");
        Assert.Single(broker.Launches);
    }

    [Fact]
    public void LoginLaunchWaitsForSettingsAndStartsInTrayOncePerSession()
    {
        var plans = new RuntimePlanProvider(new HostManagerDeploymentState());
        var broker = new Sessions();
        using var owner = new NativeUiLaunchHostedService(broker, plans, NullLogger<NativeUiLaunchHostedService>.Instance);
        owner.ScanActiveSessions("fixture-ui.exe");
        Assert.Empty(broker.Launches);
        plans.Publish(Plan(1, true));
        owner.ScanActiveSessions("fixture-ui.exe");
        owner.ScanActiveSessions("fixture-ui.exe");
        Assert.Equal((7u, "fixture-ui.exe", "--background-startup"), Assert.Single(broker.Launches));
        broker.Active = [];
        owner.ScanActiveSessions("fixture-ui.exe");
        broker.Active = [7];
        owner.ScanActiveSessions("fixture-ui.exe");
        Assert.Equal(2, broker.Launches.Count);
    }

    [Theory]
    [InlineData(false, NativeUiProcessPresence.Absent)]
    [InlineData(true, NativeUiProcessPresence.Expected)]
    [InlineData(true, NativeUiProcessPresence.Conflicting)]
    public void LoginDoesNotLaunchWhenDisabledOrUiAlreadyPresent(bool enabled, NativeUiProcessPresence presence)
    {
        var plans = new RuntimePlanProvider(new HostManagerDeploymentState());
        plans.Publish(Plan(1, enabled));
        var broker = new Sessions { Presence = presence };
        using var owner = new NativeUiLaunchHostedService(broker, plans, NullLogger<NativeUiLaunchHostedService>.Instance);
        owner.ScanActiveSessions("fixture-ui.exe");
        Assert.Empty(broker.Launches);
    }

    [Fact]
    public async Task InteractiveBackendDoesNotMutateInstalledService()
    {
        var plans = new RuntimePlanProvider(new HostManagerDeploymentState());
        var owner = new ServiceAutoStartSettings(plans, BackendHostEnvironment.Interactive, NullLogger<ServiceAutoStartSettings>.Instance);
        await owner.StartAsync(CancellationToken.None);
        Assert.Empty(plans.Publish(Plan(1, false)).DeliveryFailures);
        Assert.Single(plans.Publish(Plan(2, true)).DeliveryFailures);
        await owner.StopAsync(CancellationToken.None);
        Assert.Empty(plans.Publish(Plan(3, true)).DeliveryFailures);
    }

    [Fact]
    public void DesktopInstallationReadsRealIsolatedRegistryAndRequiresLauncher()
    {
        var id = Guid.NewGuid().ToString("N");
        var folder = Path.Combine(Path.GetTempPath(), "rm-desktop-install-" + id);
        var keyPath = @"Software\ResourceManager.Launcher.Tests\" + id;
        Directory.CreateDirectory(folder);
        using var hive = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        try
        {
            using var root = hive.CreateSubKey(keyPath);
            using var product = root.CreateSubKey(@"Software\ResourceManager");
            product.SetValue("InstallRoot", folder);
            product.SetValue("ServiceName", WindowsServiceRegistration.ProductServiceName);
            foreach (var value in new[] { "BackendPath", "NativeUiPath", "LauncherPath" })
            {
                var path = Path.Combine(folder, value + ".exe");
                File.WriteAllText(path, "non-executable test payload");
                product.SetValue(value, path);
            }
            var installation = DesktopInstallation.Read(root);
            Assert.Equal(folder, installation.Root);
            Assert.Equal(Path.Combine(folder, "LauncherPath.exe"), installation.Launcher);
            product.DeleteValue("LauncherPath");
            Assert.Throws<InvalidOperationException>(() => DesktopInstallation.Read(root));
        }
        finally
        {
            hive.DeleteSubKeyTree(keyPath, false);
            foreach (var file in Directory.EnumerateFiles(folder)) File.Delete(file);
            Directory.Delete(folder);
        }
    }

    [Fact]
    public void ServiceIdentityRejectsOtherBinaryOrAccountWithoutChangingScm()
    {
        var binary = Path.Combine(Path.GetTempPath(), "fixture backend.exe");
        WindowsServiceRegistration.RequireExpectedBinary(new(1, 3, $"\"{binary}\"", "LocalSystem"), binary);
        Assert.Throws<InvalidOperationException>(() => WindowsServiceRegistration.RequireExpectedBinary(new(1, 3, binary + ".other", "LocalSystem"), binary));
        Assert.Throws<InvalidOperationException>(() => WindowsServiceRegistration.RequireExpectedBinary(new(1, 3, binary, "LocalService"), binary));
        Assert.Null(WindowsServiceRegistration.Read("ResourceManager.AbsentTest." + Guid.NewGuid().ToString("N")));
    }

    private static CompiledRuntimePlan Plan(long version, bool autoStart) => CompiledRuntimePlan.Default with
    {
        Version = version, AutoStartEnabled = autoStart, HostManager = HostManagerTestPlanFactory.CreatePlan()
    };

    private sealed class Sessions : IInteractiveUserSessionBroker
    {
        public IReadOnlyList<uint> Active = [7];
        public NativeUiProcessPresence Presence;
        public bool FailNextLaunch;
        public List<(uint Session, string Path, string Arguments)> Launches { get; } = [];
        public IReadOnlyList<uint> GetActiveSessionIds() => Active;
        public NativeUiProcessPresence GetNativeUiProcessPresence(uint sessionId, string expectedExecutablePath) => Presence;
        public uint LaunchNativeUi(uint sessionId, string executablePath, string arguments)
        {
            if (FailNextLaunch)
            {
                FailNextLaunch = false;
                throw new System.ComponentModel.Win32Exception(5);
            }
            Launches.Add((sessionId, executablePath, arguments));
            return 1;
        }
    }
}
