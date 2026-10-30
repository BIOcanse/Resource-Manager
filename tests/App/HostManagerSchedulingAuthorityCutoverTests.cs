using System.Collections.Immutable;
using System.Text.Json.Nodes;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Optimization;

namespace Resource_Manager_APP.Tests;

public sealed class HostManagerSchedulingAuthorityCutoverTests
{
    [Fact]
    public void UnavailableBeforeTheFirstCaptureAllowsRoundZero()
    {
        var authority = new HostManagerSchedulingAuthority();
        var binding = CreateBinding();
        authority.PublishUnavailable(0, binding, DateTimeOffset.UtcNow, "hardware-snapshot-unavailable");
        var current = authority.Capture();
        Assert.Equal(0UL, current.RoundNumber);
        Assert.Equal(HostManagerSchedulingAuthorityAvailability.Unavailable, current.Availability);
        Assert.Equal("hardware-snapshot-unavailable", current.UnavailableReason);
    }

    [Fact]
    public void UnavailableAttemptRevokesThePreviousActionablePublication()
    {
        var authority = new HostManagerSchedulingAuthority();
        var compute = CreateCompute(1);
        var binding = CreateBinding();
        authority.PublishReady(
            compute,
            CreateMemoryModes(1),
            DateTimeOffset.UtcNow,
            binding,
            CreateEvidence(binding));

        authority.PublishUnavailable(
            2,
            binding,
            DateTimeOffset.UtcNow,
            "compute-sample-incomplete");

        var current = authority.Capture();
        Assert.Equal(2UL, current.RoundNumber);
        Assert.Equal(
            HostManagerSchedulingAuthorityAvailability.Unavailable,
            current.Availability);
        Assert.Null(current.Compute);
        Assert.Null(current.MemoryModes);
        Assert.Null(current.PolicyEvidence);
        Assert.Equal("compute-sample-incomplete", current.UnavailableReason);
    }

    [Fact]
    public void ComputeOnlyPublicationCannotRetainOldMemoryModes()
    {
        var authority = new HostManagerSchedulingAuthority();
        var binding = CreateBinding();
        authority.PublishReady(
            CreateCompute(1),
            CreateMemoryModes(1),
            DateTimeOffset.UtcNow,
            binding,
            CreateEvidence(binding));

        authority.PublishComputeOnly(
            CreateCompute(2),
            binding,
            DateTimeOffset.UtcNow,
            "memory-mode-calibration-unavailable");

        var current = authority.Capture();
        Assert.Equal(
            HostManagerSchedulingAuthorityAvailability.ComputeOnly,
            current.Availability);
        Assert.Equal(2UL, current.Compute?.RoundNumber);
        Assert.Null(current.MemoryModes);
        Assert.Null(current.PolicyEvidence);
        Assert.Equal(
            "memory-mode-calibration-unavailable",
            current.UnavailableReason);
    }

    [Fact]
    public void AuthorityReplacesRepeatedRoundsInPublicationOrder()
    {
        var authority = new HostManagerSchedulingAuthority();
        var binding = CreateBinding();
        authority.PublishUnavailable(2, binding, DateTimeOffset.UtcNow, "first");
        authority.PublishUnavailable(2, binding, DateTimeOffset.UtcNow, "recomputed");
        Assert.Equal("recomputed", authority.Capture().UnavailableReason);
        authority.PublishComputeOnly(CreateCompute(1), binding, DateTimeOffset.UtcNow, "current");
        Assert.Equal(1UL, authority.Capture().RoundNumber);
        Assert.Equal("current", authority.Capture().UnavailableReason);
    }

    [Fact]
    public void ReadyRequiresTheCurrentMemoryConfigurationAndPolicyBinding()
    {
        var authority = new HostManagerSchedulingAuthority();
        var binding = CreateBinding();

        Assert.Throws<InvalidDataException>(() => authority.PublishReady(
            CreateCompute(1),
            CreateMemoryModes(1, configurationGeneration: 51),
            DateTimeOffset.UtcNow,
            binding,
            CreateEvidence(binding)));

        var wrongEvidence = CreateEvidence(binding) with
        {
            ConfigurationSha256 = new string('C', 64)
        };
        Assert.Throws<InvalidDataException>(() => authority.PublishReady(
            CreateCompute(1),
            CreateMemoryModes(1),
            DateTimeOffset.UtcNow,
            binding,
            wrongEvidence));
    }

    [Fact]
    public void AuthorityReplacesTheCurrentPlanBindingWithoutOrderingIt()
    {
        var authority = new HostManagerSchedulingAuthority();
        authority.PublishUnavailable(1, CreateBinding(publicationSequence: 2), DateTimeOffset.UtcNow, "first");
        var binding = CreateBinding(publicationSequence: 1);
        authority.PublishUnavailable(1, binding, DateTimeOffset.UtcNow, "replaced");
        Assert.Equal(binding, authority.Capture().PlanBinding);
        Assert.Equal("replaced", authority.Capture().UnavailableReason);
    }

    [Fact]
    public void DefaultMemoryModePolicyIsActionableAndBoundToTheInlineBaseline()
    {
        var hostPlan = HostManagerTestPlanFactory.CreatePlan();
        var source = new ProductBaselineHostManagerMemoryModePolicySource();

        var capture = source.Capture(
            CreateBinding(hostPlan),
            hostPlan.SmartCoordinator);

        var policy = Assert.IsType<HostManagerMemoryModePolicy>(capture.Policy);
        Assert.Null(capture.UnavailableReason);
        Assert.True(policy.AllowUnrestricted);
        Assert.Equal(10_000U, policy.Configuration.RatioUnitsMaximum);
        Assert.Equal(1_000U, policy.Configuration.StrongBeginFreeRatioUnits);
        Assert.Equal(3_000U, policy.Configuration.NormalMinimumFreeRatioUnits);
        Assert.Equal(6_000U, policy.Configuration.UnrestrictedMinimumFreeRatioUnits);
        Assert.Equal(3U, policy.OptimizeMemoryPriority);
        Assert.Equal(1U, policy.PagedFrozenMemoryPriority);
        Assert.True(policy.PolicyEvidence.IsBoundTo(
            CreateBinding(hostPlan),
            allowUnrestricted: true));
    }

    [Fact]
    public void ProductionCycleCannotCreateLegacyCentralResourceActions()
    {
        var appRoot = FindAppRoot();
        var cycle = File.ReadAllText(Path.Combine(
            appRoot,
            "Infrastructure",
            "Optimization",
            "HostManagerSmartCoordinator.NativeRuntime.cs"));
        Assert.DoesNotContain(
            "RunResourceSchedulingCycleAsync(",
            cycle,
            StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(
            appRoot,
            "Infrastructure",
            "Optimization",
            "HostManagerSmartCoordinator.ResourceScheduling.cs")));

        var configuration = JsonNode.Parse(File.ReadAllText(Path.Combine(
            appRoot,
            "Configuration",
            "HostManager",
            "default.json")))!;
        Assert.False(configuration["hot_publish"]!["resource_scheduler"]!
            ["dispatch"]!["execution_capability_enabled"]!.GetValue<bool>());
    }

    [Fact]
    public void ProductionCompositionHasNoHostSelfPrivateResourceExecutor()
    {
        var appRoot = FindAppRoot();
        var registration = File.ReadAllText(Path.Combine(
            appRoot,
            "Hosting",
            "OptimizationServiceRegistration.cs"));

        Assert.Equal(
            1,
            Count(registration, "services.AddSingleton<HostManagerSchedulingAuthority>();"));
        Assert.Equal(
            1,
            Count(registration, "ProductBaselineHostManagerMemoryModePolicySource"));
        Assert.Equal(
            1,
            Count(registration, "new HostManagerMemoryModePolicyAuthority("));
        Assert.Equal(
            1,
            Count(
                registration,
                "services.AddSingleton<ResourceManagerSelfLocalResourceManager>();"));
        Assert.DoesNotContain(
            "HostManagerTypedResourceActionRouter",
            registration,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "HostManagerResourceExecutionRuntime",
            registration,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "HostManagerLegacyResourceTransactionRecovery",
            registration,
            StringComparison.Ordinal);
        var coordinator = File.ReadAllText(Path.Combine(
            appRoot,
            "Infrastructure",
            "Optimization",
            "HostManagerSmartCoordinator.cs"));
        Assert.DoesNotContain(
            "HostManagerResourceExecutionRuntime",
            coordinator,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "HostManagerLegacyResourceTransactionRecovery",
            coordinator,
            StringComparison.Ordinal);

        var recovery = File.ReadAllText(Path.Combine(
            appRoot,
            "Infrastructure",
            "Optimization",
            "HostManagerSmartCoordinator.NativeTransactionRecovery.cs"));
        Assert.DoesNotContain(
            "RecoverTypedResourceAction(",
            recovery,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "TryApplyBatch(",
            recovery,
            StringComparison.Ordinal);
        // A resource record settles as already restored; nothing replays the trim.
        Assert.Contains(
            "RecoverResourceTransactionRecordAsync(",
            recovery,
            StringComparison.Ordinal);

        var adaptationRoot = Path.Combine(
            appRoot,
            "Infrastructure",
            "Adaptation");
        foreach (var retiredFile in new[]
        {
            "ResourceManagerSelfResourceMarker.Resources.cs",
            "ResourceManagerSelfResourceMarker.Snapshot.cs",
            "ResourceManagerSelfResourceMarker.Ledger.cs",
            "ResourceManagerSelfResourceMarker.TypedResources.cs"
        })
        {
            Assert.False(File.Exists(Path.Combine(adaptationRoot, retiredFile)));
        }
        Assert.Empty(Directory.GetFiles(
            adaptationRoot,
            "ResourceManagerSelfResourceMarker*.cs"));
        var schedulingControlSource = string.Join(
            Environment.NewLine,
            Directory.GetFiles(
                    adaptationRoot,
                    "ResourceManagerSelfSchedulingControl*.cs")
                .Select(File.ReadAllText));
        Assert.DoesNotContain(
            "NativeAdapterResourceLedgerSession",
            schedulingControlSource,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ManagerDirect",
            schedulingControlSource,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "HostSelfExecutorProof",
            schedulingControlSource,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "TryApplyBatch(",
            schedulingControlSource,
            StringComparison.Ordinal);
    }

    private static HostManagerComputeScoringCycleResult CreateCompute(ulong generation)
        => new(
            generation,
            new(generation, ImmutableArray<HostManagerComputeScore>.Empty),
            new(generation, ImmutableArray<HostManagerComputeScore>.Empty));

    private static HostManagerMemoryModeDesiredSnapshot CreateMemoryModes(
        ulong generation,
        ulong configurationGeneration = 50)
        => new(
            configurationGeneration,
            generation,
            false,
            0,
            0,
            ImmutableArray<HostManagerDesiredMemoryMode>.Empty);

    private static HostManagerSchedulingPlanBinding CreateBinding(
        ulong publicationSequence = 1)
        => new(
            publicationSequence,
            10,
            new string('C', 64),
            20,
            new string('D', 64),
            true,
            HostManagerMemoryModePolicySourceKinds.ProductBaseline,
            50,
            new string('E', 64));

    private static HostManagerSchedulingPlanBinding CreateBinding(
        CompiledHostManagerPlan plan,
        ulong publicationSequence = 1)
    {
        var smart = plan.SmartCoordinator;
        var policy = smart.HotPublish.MemoryModePolicy;
        return new HostManagerSchedulingPlanBinding(
            publicationSequence,
            plan.PlanEpoch,
            plan.PlanSha256,
            smart.ConfigurationGeneration,
            smart.ConfigurationSha256,
            policy.Enabled,
            policy.SourceKind,
            policy.ConfigurationGeneration,
            policy.ConfigurationSha256);
    }

    private static HostManagerMemoryModePolicyEvidence CreateEvidence(
        HostManagerSchedulingPlanBinding binding)
        => HostManagerMemoryModePolicyEvidence.Create(
            binding,
            binding.MemoryModePolicySourceKind,
            binding.MemoryModeConfigurationSha256,
            allowUnrestricted: false);

    private static int Count(string source, string value)
    {
        var count = 0;
        var offset = 0;
        while ((offset = source.IndexOf(value, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += value.Length;
        }
        return count;
    }

    private static string FindAppRoot(
        [System.Runtime.CompilerServices.CallerFilePath] string sourceFilePath = "")
    {
        var sourceDirectory = Path.GetDirectoryName(sourceFilePath)!;
        var appRoot = Path.GetFullPath(
            Path.Combine(sourceDirectory, "..", "..", "src", "Core"));
        return File.Exists(Path.Combine(appRoot, "ResourceManager.App.csproj"))
            ? appRoot
            : throw new DirectoryNotFoundException(
                "Could not locate Resource Manager-APP from the test source path.");
    }
}
