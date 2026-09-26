using ResourceManager.App.Application.GpuPlacement;
using ResourceManager.App.Application.RuntimeSpecialization;
using ResourceManager.App.Domain.GpuPlacement;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.GpuPlacement;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace Resource_Manager_APP.Tests;

public sealed class GpuLaunchInterceptionTests
{
    [Fact]
    public void ProcessPolicy_DefaultsStartupInterceptionToDisabled()
    {
        var policy = new GpuPlacementProcessPolicy(
            "software",
            "process",
            "process.exe",
            @"C:\Apps\process.exe",
            true,
            GpuPlacementPolicyModes.Inherit,
            GpuPlacementRiskLevels.Low,
            [],
            GpuPlacementTargets.SystemDefaultGpu,
            GpuPlacementExplicitSelectionModes.DefaultSkip,
            null,
            DateTimeOffset.UnixEpoch);

        Assert.False(policy.StartupInterceptionEnabled);
    }

    [Fact]
    public void StartupProvider_IsIndependentFromRuntimeHotSwitchMode()
    {
        var policy = new ResolvedGpuPlacementPolicy(
            GpuPlacementPolicyModes.Auto,
            GpuPlacementRiskLevels.Low,
            [GpuPlacementProviderIds.D3dDeviceCreateShim],
            GpuPlacementSchedulingModes.Precise,
            GpuPlacementTargets.AutoIdleGpu,
            GpuPlacementTargets.AutoIdleGpu,
            GpuPlacementRuntimeSchedulingModes.Ordinary,
            GpuPlacementExplicitSelectionModes.DefaultSkip,
            false,
            GpuPlacementRuntimeSwitchMethods.FutureFrameTakeover,
            true,
            false,
            false,
            CpuMaximumOccupancyModes.SingleCcd,
            false,
            [],
            []);

        Assert.True(policy.AllowsStartupShimExecution());
        Assert.False(policy.AcceptsRuntimeGpuScheduling());
    }

    [Fact]
    public void IfeoRuleName_IsStableForPathCase()
    {
        var first = WindowsIfeoGpuLaunchInterceptionRegistry.CreateRuleName(@"C:\Apps\Game\game.exe");
        var second = WindowsIfeoGpuLaunchInterceptionRegistry.CreateRuleName(@"c:\apps\game\GAME.EXE");

        Assert.Equal(first, second);
        Assert.StartsWith("ResourceManager-", first, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(GpuPlacementProviderIds.VulkanExplicitLayer, true)]
    [InlineData(GpuPlacementProviderIds.VulkanImplicitLayer, false)]
    [InlineData(GpuPlacementProviderIds.WindowsGraphicsPreference, false)]
    public void VulkanUsesTheExistingExplicitShimPermissionForBothPhases(string provider, bool startupAllowed)
    {
        var policy = ResolvedGpuPlacementPolicy.FromSoftware(
            GpuPlacementPolicyDefaults.CreateSoftwarePolicy("software", "Software") with
            {
                EnabledMode = GpuPlacementPolicyModes.Manual,
                SchedulingMode = GpuPlacementSchedulingModes.Precise,
                RuntimeSchedulingMode = GpuPlacementRuntimeSchedulingModes.Precise,
                RuntimeHotSwitchEnabled = true,
                AllowedProviders = [provider]
            });
        Assert.Equal(startupAllowed, policy.AllowsStartupShimExecution());
        Assert.Equal(startupAllowed ? new[] { provider } : [], policy.GetStartupProviders(GpuGraphicsApi.Vulkan));
        Assert.Equal(startupAllowed ? GpuPlacementProviderIds.VulkanExplicitLayer : null,
            policy.GetRuntimeProvider(GpuGraphicsApi.Vulkan));
        Assert.DoesNotContain(GpuPlacementProviderIds.VulkanExplicitLayer, GpuPlacementProviderIds.Defaults);
    }

    [Fact]
    public void BrokerProtocol_CarriesBothExplicitStartupProviders()
    {
        var policy = ResolvedGpuPlacementPolicy.FromSoftware(
            GpuPlacementPolicyDefaults.CreateSoftwarePolicy("software", "Software") with
            {
                EnabledMode = GpuPlacementPolicyModes.Manual,
                SchedulingMode = GpuPlacementSchedulingModes.Precise,
                AllowedProviders = [GpuPlacementProviderIds.VulkanExplicitLayer, GpuPlacementProviderIds.D3dDeviceCreateShim]
            });
        var decision = new GpuStartupPlacementDecision(GpuStartupPlacementDecisionKinds.Inject,
            "ready", "configured", @"C:\Apps\target.exe", "software", "process", "GPU0", "gpu:0",
            @"C:\Package\UserData\GpuPlacement\gpu-policy.txt", "GPU", "0x00000000_0x00000001",
            [GpuPlacementProviderIds.D3dDeviceCreateShim, GpuPlacementProviderIds.VulkanExplicitLayer]);
        Assert.Equal(new[] { GpuPlacementProviderIds.D3dDeviceCreateShim, GpuPlacementProviderIds.VulkanExplicitLayer }, decision.StartupProviders);
        Assert.Contains("startupProviders=d3d-device-create-shim,vulkan-explicit-layer\n",
            GpuLaunchBrokerProtocol.Serialize(decision), StringComparison.Ordinal);
        Assert.Empty((policy with { EnabledMode = GpuPlacementPolicyModes.Disabled }).GetStartupProviders(GpuGraphicsApi.D3D11));
    }

    [Fact]
    public void IfeoDebuggerCommand_QuotesBrokerPathAndUsesDedicatedMode()
    {
        var command = WindowsIfeoGpuLaunchInterceptionRegistry.BuildDebuggerCommand(
            @"C:\Program Files\Resource Manager\ResourceManager.GpuLaunchBroker.exe");

        Assert.Equal(
            "\"C:\\Program Files\\Resource Manager\\ResourceManager.GpuLaunchBroker.exe\" --resource-manager-ifeo",
            command);
    }

    [Fact]
    public void IfeoRegistration_RejectsMissingBrokerBeforeRegistryAccess()
    {
        var executablePath = Environment.ProcessPath;
        Assert.False(string.IsNullOrWhiteSpace(executablePath));
        Assert.True(WindowsIfeoGpuLaunchInterceptionRegistry.TryReadMachine(executablePath!, out _));
        var registry = new WindowsIfeoGpuLaunchInterceptionRegistry(
            Path.Combine(Path.GetTempPath(), $"missing-broker-{Guid.NewGuid():N}.exe"));
        var policy = new GpuPlacementProcessPolicy(
            "software",
            "process",
            Path.GetFileName(executablePath),
            executablePath,
            true,
            GpuPlacementPolicyModes.Auto,
            GpuPlacementRiskLevels.Low,
            [GpuPlacementProviderIds.D3dDeviceCreateShim],
            GpuPlacementTargets.SystemDefaultGpu,
            GpuPlacementExplicitSelectionModes.DefaultSkip,
            null,
            DateTimeOffset.UnixEpoch,
            true);

        var status = registry.Apply(policy);

        Assert.False(status.Registered);
        Assert.Equal(GpuLaunchInterceptionStatuses.BrokerMissing, status.Status);
    }

    [Fact]
    public void BrokerProtocol_RemovesLineBreaksFromValues()
    {
        var serialized = GpuLaunchBrokerProtocol.Serialize(new GpuStartupPlacementDecision(
            GpuStartupPlacementDecisionKinds.PassThrough,
            "test",
            "first\r\nsecond",
            @"C:\Apps\test.exe",
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            []));

        Assert.Contains("message=first  second\n", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("\r", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public void BrokerProtocol_ParsesExecutionReport()
    {
        var protocol = string.Join('\n',
            "executablePath=C:\\Apps\\test.exe",
            "softwareId=software",
            "processKey=process",
            "processId=42",
            "outcome=provider-ready",
            "message=ready",
            "startupTargetGpu=AutoIdleGpu",
            "assignedPositionId=gpu:1",
            "targetAdapterName=Test GPU");

        Assert.True(GpuLaunchBrokerProtocol.TryParseExecutionReport(
            protocol,
            out var report,
            out var error), error);
        Assert.NotNull(report);
        Assert.Equal(42, report.ProcessId);
        Assert.Equal("provider-ready", report.Outcome);
        Assert.Equal("gpu:1", report.AssignedPositionId);
        Assert.True(GpuLaunchBrokerProtocol.TryParseExecutionReport(
            protocol.Replace("outcome=provider-ready", "outcome=startup-configured", StringComparison.Ordinal),
            out var configured, out error), error);
        Assert.Equal(GpuLaunchExecutionOutcomes.StartupConfigured, configured!.Outcome);
    }

    [Fact]
    public async Task ExecutionReportStore_KeepsOnlyLatestReportPerExecutablePath()
    {
        var root = Path.Combine(Path.GetTempPath(), $"resource-manager-gpu-report-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var store = new JsonGpuLaunchExecutionReportStore(new TestHostEnvironment(root));
            var path = Path.Combine(root, "test.exe");
            await store.RecordAsync(Report(path, GpuLaunchExecutionOutcomes.PassThroughStarted), CancellationToken.None);
            await store.RecordAsync(Report(path.ToUpperInvariant(), GpuLaunchExecutionOutcomes.ProviderReady), CancellationToken.None);

            var reloaded = new JsonGpuLaunchExecutionReportStore(new TestHostEnvironment(root));
            var report = Assert.Single(await reloaded.GetLatestAsync(CancellationToken.None));
            Assert.Equal(GpuLaunchExecutionOutcomes.ProviderReady, report.Outcome);
            Assert.Equal(Path.GetFullPath(path), report.ExecutablePath, ignoreCase: true);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task AutoStartup_WithoutNativePlacementIdentityPassesThrough()
    {
        var executablePath = Environment.ProcessPath;
        Assert.False(string.IsNullOrWhiteSpace(executablePath));

        var softwarePolicy = GpuPlacementPolicyDefaults.CreateSoftwarePolicy("software", "Software") with
        {
            EnabledMode = GpuPlacementPolicyModes.Auto,
            AllowedProviders = [GpuPlacementProviderIds.D3dDeviceCreateShim],
            SchedulingMode = GpuPlacementSchedulingModes.Precise,
            StartupTargetGpu = GpuPlacementTargets.AutoIdleGpu
        };
        var processPolicy = new GpuPlacementProcessPolicy(
            softwarePolicy.SoftwareId,
            "process",
            Path.GetFileName(executablePath),
            executablePath,
            true,
            GpuPlacementPolicyModes.Inherit,
            GpuPlacementRiskLevels.Low,
            [],
            GpuPlacementTargets.SystemDefaultGpu,
            GpuPlacementExplicitSelectionModes.DefaultSkip,
            null,
            DateTimeOffset.UnixEpoch,
            true);
        var compiledGpuPlacement = new CompiledGpuPlacementPlan(
            true,
            new Dictionary<string, ResolvedGpuPlacementPolicy>(StringComparer.OrdinalIgnoreCase)
            {
                [softwarePolicy.SoftwareId] = ResolvedGpuPlacementPolicy.FromSoftware(softwarePolicy)
            },
            new Dictionary<string, ResolvedGpuPlacementPolicy>(StringComparer.OrdinalIgnoreCase));
        var resolver = new GpuStartupPlacementResolver(
            new StaticGpuPlacementPolicyStore(new GpuPlacementPolicyDocument(
                GpuPlacementPolicyDocumentVersions.Current,
                [softwarePolicy],
                [processPolicy],
                DateTimeOffset.UnixEpoch)),
            new StaticRuntimePlanProvider(
                CompiledRuntimePlan.Default with { GpuPlacement = compiledGpuPlacement }),
            new D3d11ProxyShimRuntime(new TestHostEnvironment(Path.GetTempPath())),
            new JsonGpuPlacementProcessHistoryStore(new TestHostEnvironment(Path.GetTempPath())));

        var decision = await resolver.ResolveAsync(
            new GpuStartupPlacementRequest(executablePath!),
            CancellationToken.None);

        Assert.Equal(GpuStartupPlacementDecisionKinds.PassThrough, decision.Decision);
        Assert.Equal("auto-placement-native-plan-required", decision.Status);
        Assert.Equal(GpuPlacementTargets.AutoIdleGpu, decision.StartupTargetGpu);
        Assert.Null(decision.PolicyPath);
        Assert.Null(decision.AssignedPositionId);
    }

    [Fact]
    public async Task Broker_BackendUnavailablePreservesLaunchContract()
    {
        var brokerPath = Path.Combine(
            AppContext.BaseDirectory,
            WindowsIfeoGpuLaunchInterceptionRegistry.BrokerFileName);
        var targetPath = Path.Combine(
            AppContext.BaseDirectory,
            "GpuLaunchBrokerProbe",
            "ResourceManager.GpuLaunchTargetProbe.exe");
        Assert.True(File.Exists(brokerPath), brokerPath);
        Assert.True(File.Exists(targetPath), targetPath);

        var probeRoot = Path.Combine(Path.GetTempPath(), $"resource-manager-gpu-broker-test-{Guid.NewGuid():N}");
        var outputPath = Path.Combine(probeRoot, "result.txt");
        Directory.CreateDirectory(probeRoot);
        try
        {
            var start = new ProcessStartInfo(brokerPath)
            {
                UseShellExecute = false,
                WorkingDirectory = probeRoot
            };
            start.ArgumentList.Add("--resource-manager-ifeo");
            start.ArgumentList.Add(targetPath);
            start.ArgumentList.Add("plain");
            start.ArgumentList.Add("two words");
            start.Environment["RESOURCE_MANAGER_PACKAGE_ROOT"] = probeRoot;
            start.Environment["RM_GPU_LAUNCH_PROBE_OUTPUT"] = outputPath;
            start.Environment["RM_GPU_LAUNCH_PROBE_VALUE"] = "environment-preserved";

            using var process = Process.Start(start);
            Assert.NotNull(process);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await process.WaitForExitAsync(timeout.Token);

            Assert.Equal(37, process.ExitCode);
            var lines = (await File.ReadAllLinesAsync(outputPath, Encoding.Unicode, timeout.Token))
                .Select(static line => line.Split('=', 2))
                .Where(static parts => parts.Length == 2)
                .ToDictionary(static parts => parts[0], static parts => parts[1], StringComparer.Ordinal);
            Assert.Equal("3", lines["argc"]);
            Assert.Equal(targetPath, lines["argv0"]);
            Assert.Equal("plain", lines["argv1"]);
            Assert.Equal("two words", lines["argv2"]);
            Assert.Equal(probeRoot, lines["cwd"]);
            Assert.Equal("environment-preserved", lines["probeEnvironment"]);
        }
        finally
        {
            Directory.Delete(probeRoot, recursive: true);
        }
    }

    private static GpuLaunchExecutionReport Report(string path, string outcome)
    {
        return new GpuLaunchExecutionReport(
            path,
            "software",
            "process",
            42,
            outcome,
            outcome,
            GpuPlacementTargets.AutoIdleGpu,
            "gpu:1",
            "Test GPU",
            DateTimeOffset.UnixEpoch);
    }

    private sealed class TestHostEnvironment(string contentRootPath) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;

        public string ApplicationName { get; set; } = "ResourceManager.Tests";

        public string ContentRootPath { get; set; } = contentRootPath;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class StaticGpuPlacementPolicyStore(GpuPlacementPolicyDocument document)
        : IGpuPlacementPolicyStore
    {
        public Task<GpuPlacementPolicyDocument> GetAsync(CancellationToken cancellationToken)
            => Task.FromResult(document);

        public Task<GpuPlacementSoftwarePolicy> GetOrCreateSoftwarePolicyAsync(
            string softwareId,
            string softwareName,
            string? softwareKind,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<GpuPlacementSoftwarePolicy> SaveSoftwarePolicyAsync(
            GpuPlacementSoftwarePolicy policy,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<GpuPlacementProcessPolicy> SaveProcessPolicyAsync(
            GpuPlacementProcessPolicy policy,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    private sealed class StaticRuntimePlanProvider(CompiledRuntimePlan current) : IRuntimePlanProvider
    {
        public CompiledRuntimePlan Current { get; private set; } = current;

        public RuntimePlanPublicationLease AcquirePublicationLease()
            => RuntimePlanPublicationLease.CreateUntracked(Current, 1);

        public RuntimePlanPublicationResult Publish(CompiledRuntimePlan plan)
        {
            Current = plan;
            return new RuntimePlanPublicationResult(plan, 1, []);
        }
    }
}
