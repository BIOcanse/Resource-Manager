using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Hosting;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.ServiceHosting;
using ResourceManager.Launcher;
using ResourceManager.Shared.ServiceHosting;
using ResourceManager.Shared.Localization;
using ResourceManager.App.Domain.Adaptation;
using ResourceManager.Shared.Runtime;

namespace Resource_Manager_APP.Tests;

public sealed class DesktopStartupTests
{
    public static IEnumerable<object[]> LauncherLanguages =>
        AppLanguage.SupportedIds.Select(language => new object[] { language });

    [Theory]
    [MemberData(nameof(LauncherLanguages))]
    public void InstallationFailuresUseTheSelectedLauncherLanguage(string language)
    {
        var text = ToolText.For(language);
        var id = Guid.NewGuid().ToString("N");
        var folder = Path.Combine(Path.GetTempPath(), "rm-launcher-language-" + id);
        var keyPath = @"Software\ResourceManager.Launcher.Tests\" + id;
        Directory.CreateDirectory(folder);
        using var hive = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        try
        {
            using var root = hive.CreateSubKey(keyPath);
            Assert.Equal(text.NotInstalled,
                Assert.Throws<InvalidOperationException>(() => DesktopInstallation.Read(root, text)).Message);
            using var product = root.CreateSubKey(@"Software\ResourceManager");
            product.SetValue("ServiceName", "other-service");
            Assert.Equal(text.LauncherRegistrationInvalid,
                Assert.Throws<InvalidOperationException>(() => DesktopInstallation.Read(root, text)).Message);
            product.SetValue("ServiceName", WindowsServiceRegistration.ProductServiceName);
            product.SetValue("InstallRoot", folder);
            Assert.Equal(text.Format(text.LauncherValueMissingFormat, "BackendPath"),
                Assert.Throws<InvalidOperationException>(() => DesktopInstallation.Read(root, text)).Message);
            foreach (var name in new[] { "BackendPath", "NativeUiPath", "LauncherPath" })
            {
                var path = Path.Combine(folder, name + ".exe");
                File.WriteAllText(path, "non-executable language fixture");
                product.SetValue(name, path);
            }
            Assert.Equal(folder, DesktopInstallation.Read(root, text).Root);
            var missing = Path.Combine(folder, "missing-ui.exe");
            product.SetValue("NativeUiPath", missing);
            var error = Assert.Throws<FileNotFoundException>(() => DesktopInstallation.Read(root, text));
            Assert.Equal(text.LauncherProgramMissing, error.Message);
            Assert.Equal(missing, error.FileName);
            product.SetValue("NativeUiPath", Path.GetFullPath(Path.Combine(folder, "..", "outside-ui.exe")));
            Assert.Equal(text.LauncherProgramMissing,
                Assert.Throws<FileNotFoundException>(() => DesktopInstallation.Read(root, text)).Message);
        }
        finally
        {
            hive.DeleteSubKeyTree(keyPath, false);
            foreach (var file in Directory.EnumerateFiles(folder)) File.Delete(file);
            Directory.Delete(folder);
        }
    }

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

    [Theory]
    [InlineData(false, NativeUiProcessPresence.Absent)]
    [InlineData(false, NativeUiProcessPresence.Expected)]
    [InlineData(true, NativeUiProcessPresence.Absent)]
    public void ExplicitElevatedOpeningUsesTheDesktopSessionWithAutoStartOff(bool background, NativeUiProcessPresence presence)
    {
        var plans = new RuntimePlanProvider(new HostManagerDeploymentState());
        plans.Publish(Plan(1, false));
        var broker = new Sessions { Presence = presence };
        using var owner = new NativeUiLaunchHostedService(broker, plans, NullLogger<NativeUiLaunchHostedService>.Instance);
        owner.ScanActiveSessions("fixture-ui.exe");
        Assert.Empty(broker.Launches);

        // Simulate over-the-shoulder UAC: an administrator's token in the desktop
        // user's session. The launch must use session 7's SID, not the admin SID.
        var caller = LauncherCaller();
        Assert.NotEqual(Sessions.DesktopSid, caller.WindowsSid);
        Assert.Equal(1u, NativeUiLaunchHostedService.OpenInterface(broker, caller, 7, Sessions.DesktopSid,
            LauncherPath, "fixture-ui.exe", background));
        Assert.Equal(Sessions.DesktopSid, broker.LaunchedSid);
        Assert.Equal((7u, "fixture-ui.exe", background ? "--background-startup" : "--show-existing"),
            Assert.Single(broker.Launches));
    }

    [Theory]
    [InlineData("unauthenticated")]
    [InlineData("other-session")]
    [InlineData("inactive")]
    [InlineData("changed-owner")]
    [InlineData("other-image")]
    [InlineData("not-elevated")]
    [InlineData("medium-integrity")]
    [InlineData("conflict")]
    public void ExplicitOpeningRejectsUntrustedOrUnavailableLauncherSession(string reason)
    {
        var broker = new Sessions();
        TrustedAdapterCallerIdentity? caller = LauncherCaller();
        switch (reason)
        {
            case "unauthenticated": caller = null; break;
            case "other-session": caller = caller with { WindowsSessionId = 8 }; break;
            case "inactive": broker.Active = []; break;
            case "changed-owner": broker.UserSid = "S-1-5-21-100-200-300-1003"; break;
            case "other-image": caller = caller with { CanonicalExecutablePath = Path.GetFullPath("other.exe") }; break;
            case "not-elevated": caller = caller with { IsElevated = false }; break;
            case "medium-integrity": caller = caller with { IntegrityLevelRid = 0x2000 }; break;
            case "conflict": broker.Presence = NativeUiProcessPresence.Conflicting; break;
        }
        Assert.Throws<UnauthorizedAccessException>(() => NativeUiLaunchHostedService.OpenInterface(broker, caller,
            7, Sessions.DesktopSid, LauncherPath, "fixture-ui.exe", false));
        Assert.Empty(broker.Launches);
    }

    [Theory]
    [InlineData(NativeUiOpenSessionContract.OpenForeground, false)]
    [InlineData(NativeUiOpenSessionContract.OpenBackground, true)]
    public async Task ExplicitOpenProtocolReadsOnlyDisplayIntent(byte mode, bool expectedBackground)
    {
        using var stream = new MemoryStream(new byte[] { mode });
        Assert.Equal(expectedBackground, await NativeUiOpenSessionContract.ReadRequestAsync(stream, CancellationToken.None));
    }

    [Theory]
    [InlineData(new byte[] { })]
    [InlineData(new byte[] { 2 })]
    public async Task ExplicitOpenProtocolRejectsMissingOrUnknownIntent(byte[] frame)
    {
        using var stream = new MemoryStream(frame);
        if (frame.Length == 0)
            await Assert.ThrowsAsync<EndOfStreamException>(() => NativeUiOpenSessionContract.ReadRequestAsync(stream, CancellationToken.None));
        else
            await Assert.ThrowsAsync<InvalidDataException>(() => NativeUiOpenSessionContract.ReadRequestAsync(stream, CancellationToken.None));
    }

    private static readonly string LauncherPath = Path.GetFullPath("ResourceManager.Launcher.exe");
    private static TrustedAdapterCallerIdentity LauncherCaller() => new(1, 1, 123, DateTime.UtcNow.Ticks,
        "S-1-5-21-100-200-300-1002", 7, 1, 0x3000, true, LauncherPath, new(1, 1, 1), 1);

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
        internal const string DesktopSid = "S-1-5-21-100-200-300-1001";
        public string UserSid = DesktopSid;
        public string? LaunchedSid;
        public IReadOnlyList<uint> Active = [7];
        public NativeUiProcessPresence Presence;
        public bool FailNextLaunch;
        public List<(uint Session, string Path, string Arguments)> Launches { get; } = [];
        public IReadOnlyList<uint> GetActiveSessionIds() => Active;
        public string? GetUserSid(uint sessionId) => UserSid;
        public NativeUiProcessPresence GetNativeUiProcessPresence(uint sessionId, string expectedExecutablePath) => Presence;
        public uint LaunchNativeUi(uint sessionId, string expectedUserSid, string executablePath, string arguments)
        {
            LaunchedSid = expectedUserSid;
            return LaunchNativeUi(sessionId, executablePath, arguments);
        }
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
